# shellcheck shell=bash
# Shared helpers for .github/workflows/agent-orchestrator.yml.
# Source this file from a job step; do not execute it directly. Every job sources the same file so that
# Issue/PR mapping and state transitions behave identically everywhere.
#
# Required environment: REPO, READY_LABEL, WORKING_LABEL, AI_REVIEW_LABEL, REVIEW_LABEL, BLOCKED_LABEL,
# COPILOT_REVIEWER, and GH_TOKEN for the gh CLI.
# Optional environment:
#   LAUNCH_MATCH_WINDOW_SECONDS  Max delay between an agent launch and its PR creation (default 1800).
#   CI_WORKFLOW_FILE             Workflow file whose result gates human review (default ci.yml).
#   ACTIONS_TOKEN                Token used to read workflow runs (defaults to GH_TOKEN).
#   MAX_CI_FIX_ATTEMPTS          Consecutive CI repair tasks before human escalation (default 3).
#   BASE_BRANCH                  Default base branch for follow-up agent tasks.

# Prompt for a new coding-agent task. The marker line lets every job map the resulting PR to the Issue
# without relying on branch names, labels, or comments.
build_task_prompt() {
  local issue="$1" title="$2" body="$3" base="$4"
  printf 'Implement GitHub Issue #%s: %s\n\n%s\n\n' "$issue" "$title" "$body"
  printf 'Run restore, build, and all tests. Create a pull request against %s. ' "$base"
  printf 'The PR description must contain these two lines exactly as written, and they must be kept whenever the description is updated:\n\n'
  printf '<!-- agent-orchestrator:issue=%s -->\nCloses #%s\n\nDo not merge.' "$issue" "$issue"
}

issue_from_marker() {
  printf '%s\n' "$1" | sed -nE 's/.*agent-orchestrator:issue=([0-9]+).*/\1/p' | head -1
}

issue_from_branch() {
  printf '%s\n' "$1" | sed -nE 's#^copilot/issue-([0-9]+)-.*#\1#p' | head -1
}

# Succeeds when the number is an Issue (not a pull request) in this repository.
is_repo_issue() {
  local number
  number="$(gh api "repos/$REPO/issues/$1" --jq 'if .pull_request == null then .number else empty end' 2>/dev/null)" || return 2
  [ -n "$number" ] && [ "$number" = "$1" ]
}

# Explicit references only: the orchestrator's own task wording and GitHub closing keywords.
# Incidental mentions such as "see #12" are ignored, and several distinct references are ambiguous.
issue_from_body_reference() {
  local body="$1" refs
  refs="$(printf '%s\n' "$body" \
    | grep -oiE '\b(implement github issue|close[sd]?|fix(e[sd])?|resolve[sd]?)[[:space:]]*:?[[:space:]]+#[0-9]+\b' \
    | grep -oE '#[0-9]+$' | tr -d '#' | sort -un || true)"
  if [ -z "$refs" ]; then return 0; fi
  if [ "$(printf '%s\n' "$refs" | wc -l)" -ne 1 ]; then
    echo "PR body references several Issues ($(printf '%s' "$refs" | tr '\n' ' ')); not guessing" >&2
    return 0
  fi
  if is_repo_issue "$refs"; then
    printf '%s' "$refs"
  else
    local lookup_rc=$?
    [ "$lookup_rc" -eq 1 ] || return 2
    echo "PR body references #$refs, which is not an Issue in $REPO" >&2
  fi
}

# Open Issues that carry any agent state label, one number per line.
agent_issue_numbers() {
  jq -r --arg w "$WORKING_LABEL" --arg a "$AI_REVIEW_LABEL" --arg r "$REVIEW_LABEL" --arg b "$BLOCKED_LABEL" \
    '.[] | select(any(.labels[]?.name; . == $w or . == $a or . == $r or . == $b)) | .number' <<< "$1"
}

issue_comment_bodies() {
  gh issue view "$1" --repo "$REPO" --json comments \
    --jq '.comments[]? | select(.author.login == "github-actions") | .body // empty'
}

# The Agent Tasks API state is the implementation-complete signal. Keep its task ID on the Issue, and use
# head_ref for every follow-up task so completion always refers to work on this PR rather than an older task.
start_agent_task() {
  local prompt="$1" base_ref="$2" head_ref="${3:-}" create_pr="${4:-false}" response
  response="$(jq -n --arg prompt "$prompt" --arg base_ref "$base_ref" --arg head_ref "$head_ref" \
      --argjson create_pr "$create_pr" \
      '{prompt: $prompt, base_ref: $base_ref, create_pull_request: $create_pr}
       + (if $head_ref == "" then {} else {head_ref: $head_ref} end)' \
    | gh api --method POST -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2026-03-10' \
      --input - "agents/repos/$REPO/tasks")" || return 1
  jq -er '.id | select(type == "string" and length > 0)' <<< "$response"
}

record_agent_task() {
  local issue="$1" task_id="$2" source_sha="${3:-initial}" review_id="${4:-}" feedback_signature="${5:-}" \
    launch_id="${6:-}" round_task="${7:-}" human_events="${8:-}" origin_round="${9:-}"
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body \
    "<!-- agent-orchestrator:task-id=$task_id launch-id=$launch_id source-sha=$source_sha review-id=$review_id feedback=$feedback_signature round-task=$round_task human-events=$human_events origin-round=$origin_round -->" >/dev/null
}

record_agent_task_pending() {
  local issue="$1" launch_id="$2" source_sha="$3" review_id="$4" feedback_signature="$5" \
    round_task="${6:-}" human_events="${7:-}" origin_round="${8:-}"
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body \
    "<!-- agent-orchestrator:task-pending=$launch_id source-sha=$source_sha review-id=$review_id feedback=$feedback_signature round-task=$round_task human-events=$human_events origin-round=$origin_round -->" >/dev/null
}

agent_task_launch_pending() {
  local comments task_line pending_line task_launch pending_launch
  comments="$(gh issue view "$1" --repo "$REPO" --json comments \
    --jq '.comments[] | select(.author.login == "github-actions") | .body')" || return 2
  task_line="$(grep -oE 'agent-orchestrator:task-id=[A-Za-z0-9._-]+([^[:cntrl:]]*)' <<< "$comments" | tail -1 || true)"
  pending_line="$(grep -oE 'agent-orchestrator:task-pending=[A-Za-z0-9._-]+' <<< "$comments" | tail -1 || true)"
  [ -n "$pending_line" ] || return 1
  task_launch="$(sed -nE 's/.*launch-id=([A-Za-z0-9._-]*).*/\1/p' <<< "$task_line")"
  pending_launch="${pending_line#*task-pending=}"
  [ "$pending_launch" != "$task_launch" ]
}

start_issue_task() {
  local issue="$1" title="$2" body="$3" base_ref="$4" launch_id task_id labels pending_rc
  if ! issue_open "$issue"; then
    echo "Issue #$issue is closed; not starting a coding task"
    return 1
  fi
  if agent_task_launch_pending "$issue"; then
    echo "An earlier Copilot task launch for Issue #$issue is untracked; refusing to start another task" >&2
    return 3
  else
    pending_rc=$?
    [ "$pending_rc" -eq 1 ] || return 1
  fi
  labels="$(issue_labels "$issue")" || return 1
  if ! printf '%s\n' "$labels" | grep -Fxq "$READY_LABEL"; then
    echo "Issue #$issue is no longer ready; not starting a coding task"
    return 3
  fi
  if printf '%s\n' "$labels" | grep -Fxq "$WORKING_LABEL" ||
      printf '%s\n' "$labels" | grep -Fxq "$AI_REVIEW_LABEL" ||
      printf '%s\n' "$labels" | grep -Fxq "$REVIEW_LABEL" ||
      printf '%s\n' "$labels" | grep -Fxq "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}"; then
    echo "Issue #$issue already has an active agent state; not starting another task"
    return 3
  fi
  set_state "$issue" "$WORKING_LABEL" || {
    echo "Unable to transition Issue #$issue from ready before starting a coding task" >&2
    return 1
  }
  launch_id="$(date -u +%s%N)-$$"
  record_agent_task_pending "$issue" "$launch_id" initial "" "" || {
    echo "Unable to persist task launch intent; not starting a coding task" >&2
    return 1
  }
  task_id="$(start_agent_task "$(build_task_prompt "$issue" "$title" "$body" "$base_ref")" "$base_ref" "" true)" || {
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body \
      "The initial Copilot task could not be started. Please verify the Agent Tasks state before relaunching. <!-- agent-orchestrator:task-launch-failed=$launch_id -->" >/dev/null
    set_state "$issue" "$BLOCKED_LABEL"
    return 1
  }
  if ! record_agent_task "$issue" "$task_id" initial "" "" "$launch_id"; then
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body \
      "Copilot task $task_id was launched but could not be tracked. Review is disabled until a human verifies the task state. <!-- agent-orchestrator:untracked-task=$launch_id -->" >/dev/null
    set_state "$issue" "$BLOCKED_LABEL"
    return 1
  fi
  printf '%s' "$task_id"
}

agent_task_id() {
  local issue="$1" comments task_line pending_line task_id task_launch pending_launch
  comments="$(gh issue view "$issue" --repo "$REPO" --json comments \
    --jq '.comments[] | select(.author.login == "github-actions") | .body')" || return 1
  task_line="$(grep -oE 'agent-orchestrator:task-id=[A-Za-z0-9._-]+([^[:cntrl:]]*)' <<< "$comments" | tail -1 || true)"
  pending_line="$(grep -oE 'agent-orchestrator:task-pending=[A-Za-z0-9._-]+' <<< "$comments" | tail -1 || true)"
  task_id="${task_line#*task-id=}"
  task_id="${task_id%% *}"
  task_launch="$(sed -nE 's/.*launch-id=([A-Za-z0-9._-]*).*/\1/p' <<< "$task_line")"
  pending_launch="${pending_line#*task-pending=}"
  if [ -z "$task_id" ] || { [ -n "$pending_line" ] && [ "$pending_launch" != "$task_launch" ]; }; then
    return 1
  fi
  printf '%s' "$task_id"
}

agent_task_json() {
  local task_id
  task_id="$(agent_task_id "$1")" || return 1
  gh api -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2026-03-10' \
    "agents/repos/$REPO/tasks/$task_id"
}

agent_task_state() {
  agent_task_json "$1" | jq -er '.state | select(type == "string" and length > 0)'
}

issue_has_review_task() {
  local issue="$1" review_id="$2" comments
  comments="$(gh issue view "$issue" --repo "$REPO" --json comments \
    --jq '.comments[] | select(.author.login == "github-actions") | .body')" || return 2
  printf '%s\n' "$comments" | grep -Fq "review-id=$review_id "
}

record_human_feedback_finding() {
  local pr="$1" signature="$2" marker existing
  marker="<!-- agent-orchestrator:finding=$signature feedback=$signature path-base64= -->"
  existing="$(pr_bot_comments "$pr")" || return 1
  if ! printf '%s\n' "$existing" | grep -Fq "$marker"; then
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$marker" >/dev/null || return 1
  fi
}

issue_open() {
  local state
  state="$(gh issue view "$1" --repo "$REPO" --json state --jq '.state')" || return 2
  [ "$state" = "OPEN" ]
}

issue_has_processed_review() {
  local issue="$1" review_id="$2" comments
  comments="$(gh issue view "$issue" --repo "$REPO" --json comments \
    --jq '.comments[] | select(.author.login == "github-actions") | .body')" || return 2
  printf '%s\n' "$comments" | grep -Fq "agent-orchestrator:review-processed=$review_id"
}

review_already_handled() {
  local issue="$1" review_id="$2" result
  if issue_has_processed_review "$issue" "$review_id"; then return 0; else result=$?; fi
  [ "$result" -eq 1 ] || return 2
  if issue_has_review_task "$issue" "$review_id"; then return 0; else result=$?; fi
  [ "$result" -eq 1 ] || return 2
  return 1
}

record_review_processed() {
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$2" --repo "$REPO" --body \
    "<!-- agent-orchestrator:review-processed=$1 -->" >/dev/null
}

pr_bot_comments() {
  gh pr view "$1" --repo "$REPO" --json comments \
    --jq '.comments[] | select(.author.login == "github-actions") | .body'
}

pending_human_feedback() {
  local comments event_ids event_id
  comments="$(pr_bot_comments "$1")" || return 2
  event_ids="$(grep -oE 'agent-orchestrator:human-feedback-event=[A-Za-z0-9._-]+' <<< "$comments" \
    | sed 's/.*=//' | sort -u || true)"
  while IFS= read -r event_id; do
    [ -n "$event_id" ] || continue
    if ! grep -Fq "agent-orchestrator:human-feedback-processed=$event_id " <<< "$comments"; then
      return 0
    fi
  done <<< "$event_ids"
  return 1
}

block_for_human() {
  local pr="$1" issue="$2" reason="$3" marker="$4" existing labels issue_state_rc
  if issue_open "$issue"; then
    :
  else
    issue_state_rc=$?
    [ "$issue_state_rc" -eq 1 ] || return 1
    echo "Issue #$issue is closed; not changing its state"
    return 0
  fi
  labels="$(issue_labels "$issue")" || return 1
  if printf '%s\n' "$labels" | grep -Fxq "$BLOCKED_LABEL"; then
    echo "Issue #$issue is already blocked; leaving it for a human"
    return 0
  fi
  existing="$(pr_bot_comments "$pr")" || return 1
  if ! printf '%s\n' "$existing" | grep -Fq "$marker"; then
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body \
      "$(printf '%s\n\n%s' "$reason" "$marker")" >/dev/null || return 1
  fi
  set_state "$issue" "$BLOCKED_LABEL" || return 1
}

coder_response_for_feedback() {
  local pr="$1" signature="$2" issue="$3" finding_id="$4" task_id="${5:-}" task_comments task_comment launch_id launch_at comments response
  [ -n "$task_id" ] || task_id="$(agent_task_id "$issue")" || return 2
  task_comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 2
  task_comment="$(jq -cer --arg task "$task_id" \
    '[.comments[] | select(.author.login == "github-actions")
      | select((.body | split("agent-orchestrator:task-id=")[1] // "" | split(" ")[0]) == $task)]
      | last // empty' <<< "$task_comments")" || return 2
  launch_id="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*launch-id=([A-Za-z0-9._-]+).*/\1/p')"
  if [ -n "$launch_id" ]; then
    launch_at="$(jq -er --arg launch "$launch_id" \
      '[.comments[] | select(.author.login == "github-actions")
        | select((.body | split("agent-orchestrator:task-pending=")[1] // "" | split(" ")[0]) == $launch)
        | .createdAt] | last | select(type == "string" and length > 0)' <<< "$task_comments")" || return 2
  else
    launch_at="$(jq -er '.createdAt | select(type == "string" and length > 0)' <<< "$task_comment")" || return 2
  fi
  comments="$(gh pr view "$pr" --repo "$REPO" --json comments)" || return 2
  response="$(jq -cer --arg marker "<!-- agent-orchestrator:response-feedback=$signature -->" --arg finding "$finding_id" --arg launched "$launch_at" \
    '[.comments[] | select(.author.login == "copilot-swe-agent")
      | select(.createdAt >= $launched) | select(.body | contains($marker))
      | select(.body | test("(?im)^Finding:[[:space:]]*" + $finding + "[[:space:]]*$"))
      | select(.body | test("(?im)^Outcome:[[:space:]]*(fixed|rebutted)[[:space:]]*$"))
      | select([.body | split("\n")[] | select(test("^[[:space:]]*Evidence:"))
          | sub("^[[:space:]]*Evidence:"; "") | gsub("[[:space:]]"; "") | length] | any(. >= 40))
      | select(.body | test("(?im)^Evidence:.*[[:alnum:]_./-]+\\.(cs|sh|ya?ml|json|md)([^[:alnum:]_]|$)"))]
     | last // empty' <<< "$comments")" || return 1
  printf '%s' "$response"
}

response_content_fingerprint() {
  printf '%s\n' "$1" | sed -E '/agent-orchestrator:response-feedback=/d; s/^[[:space:]]+//; s/[[:space:]]+$//' \
    | tr '[:upper:]' '[:lower:]' | tr -s '[:space:]' ' ' | sha256sum | cut -d' ' -f1
}

feedback_target_signature() {
  local finding="$1" target
  target="$(printf '%s' "$finding" | sed -E 's/^- [^ ]+:[0-9]+ //' | tr '[:upper:]' '[:lower:]' \
    | sed -E 's/\b(undo|revert|reverse|restore|fix|add|remove|delete|change|update|keep|retain|prior|previous|earlier|preceding|the|a|an|to|of|for|in|on|with|and|or|this|that|should|must|please|blocking|finding)\b/ /g' \
    | tr -cs '[:alnum:]_' '\n' | sort -u | paste -sd' ' -)"
  [ -n "$target" ] || return 1
  printf '%s' "$target" | sha256sum | cut -d' ' -f1
}

feedback_target_operation() {
  local text="$1" verb
  verb="$(grep -Eio '\b(add|create|insert|include|enable|allow|retain|keep|preserve|remove|delete|exclude|disable|drop|discard)\b' \
    <<< "$text" | head -1 | tr '[:upper:]' '[:lower:]' || true)"
  case "$verb" in
    add|create|insert|include|enable|allow|retain|keep|preserve) printf positive ;;
    remove|delete|exclude|disable|drop|discard) printf negative ;;
    *) printf unknown ;;
  esac
}

feedback_target_action_for_id() {
  local feedback="$1" wanted="$2" finding target_id
  while IFS= read -r finding; do
    [ -n "$finding" ] || continue
    target_id="$(feedback_target_signature "$finding")" || continue
    if [ "$target_id" = "$wanted" ]; then
      feedback_target_operation "$finding"
      return 0
    fi
  done <<< "$feedback"
  return 1
}

completed_round_progress() {
  local pr="$1" issue="$2" sha="$3" task_id task_state comments task_comment source_sha signature finding_ids finding_id response response_rc response_id response_hash existing path encoded_path compare changed_paths outcome marker response_body evidence changed_file round_task response_task_comment response_task_id response_at
  task_id="$(agent_task_id "$issue")" || return 2
  task_state="$(agent_task_state "$issue")" || return 2
  [ "$task_state" = completed ] || return 2
  comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 2
  task_comment="$(jq -cer --arg task "$task_id" \
    '[.comments[] | select(.author.login == "github-actions")
      | select((.body | split("agent-orchestrator:task-id=")[1] // "" | split(" ")[0]) == $task)]
      | last // empty' <<< "$comments")" || return 2
  source_sha="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*source-sha=([^ ]*).*/\1/p')"
  signature="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*feedback=([A-Za-z0-9]+).*/\1/p')"
  round_task="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*round-task=([^ ]*).*/\1/p')"
  if [ -z "$signature" ]; then
    return 0
  fi
  response_task_id="${round_task:-$task_id}"
  if [ -n "$round_task" ]; then
    response_task_comment="$(jq -cer --arg task "$round_task" \
      '[.comments[] | select(.author.login == "github-actions")
        | select((.body | split("agent-orchestrator:task-id=")[1] // "" | split(" ")[0]) == $task)]
       | last // empty' <<< "$comments")" || return 2
    source_sha="$(jq -r '.body' <<< "$response_task_comment" | sed -nE 's/.*source-sha=([^ ]*).*/\1/p')"
  fi
  [ -n "$source_sha" ] || return 2
  existing="$(pr_bot_comments "$pr")" || return 2
  finding_ids="$(printf '%s\n' "$existing" | grep -F "agent-orchestrator:finding=" \
    | grep -F "feedback=$signature " | sed -nE 's/.*agent-orchestrator:finding=([A-Za-z0-9]+).*/\1/p' | sort -u)"
  [ -n "$finding_ids" ] || return 1
  for finding_id in $finding_ids; do
    if response="$(coder_response_for_feedback "$pr" "$signature" "$issue" "$finding_id" "$response_task_id")"; then
      :
    else
      response_rc=$?
      [ "$response_rc" -eq 1 ] && return 1
      return 2
    fi
    response_id="$(jq -er '.id | tostring' <<< "$response")" || return 2
    response_hash="$(response_content_fingerprint "$(jq -r '.body' <<< "$response")")"
    response_at="$(jq -er '.createdAt | select(type == "string" and length > 0)' <<< "$response")" || return 2
    marker="<!-- agent-orchestrator:response-used-content=$response_hash finding=$finding_id feedback=$signature task=$task_id response=$response_id response-at=$response_at -->"
    if printf '%s\n' "$existing" | grep -Fq "agent-orchestrator:response-used=$response_id finding=$finding_id " &&
       ! printf '%s\n' "$existing" | grep -Fq " task=$task_id "; then
      return 1
    fi
    if printf '%s\n' "$existing" | grep -Fq "agent-orchestrator:response-used-content=$response_hash finding=$finding_id " &&
       ! printf '%s\n' "$existing" | grep -Fq "$marker"; then
      if [ -z "$round_task" ] ||
         ! printf '%s\n' "$existing" | grep -Fq "agent-orchestrator:response-used-content=$response_hash finding=$finding_id feedback=$signature task=$response_task_id "; then
        return 1
      fi
    fi
    response_body="$(jq -r '.body' <<< "$response")"
    outcome="$(sed -nE 's/^[[:space:]]*Outcome:[[:space:]]*(fixed|rebutted)[[:space:]]*$/\1/ip' <<< "$response_body" | head -1 | tr '[:upper:]' '[:lower:]')"
    evidence="$(sed -nE 's/^[[:space:]]*Evidence:[[:space:]]*(.*)$/\1/ip' <<< "$response_body" | head -1)"
    encoded_path="$(printf '%s\n' "$existing" | grep -F "agent-orchestrator:finding=$finding_id " \
      | grep -F "feedback=$signature " | tail -1 | sed -nE 's/.*path-base64=([A-Za-z0-9+/=]*).*/\1/p')"
    path=""
    [ -z "$encoded_path" ] || path="$(printf '%s' "$encoded_path" | base64 --decode)" || return 2
    if [ -n "$path" ] && ! grep -Fq "$path" <<< "$evidence"; then
      return 1
    fi
    if [ "$outcome" = fixed ] && [ "$source_sha" != "$sha" ]; then
      changed_file="$(sed -nE 's/^[[:space:]]*Changed file:[[:space:]]*(.+)[[:space:]]*$/\1/ip' <<< "$response_body" | head -1)"
      [ -n "$changed_file" ] || return 1
      grep -Fq "$changed_file" <<< "$evidence" || return 1
      compare="$(gh api "repos/$REPO/compare/$source_sha...$sha")" || return 2
      changed_paths="$(jq -r '.files[]?.filename // empty' <<< "$compare")" || return 2
      printf '%s\n' "$changed_paths" | grep -Fxq "$changed_file" || return 1
    elif [ "$outcome" = fixed ] && [ "$source_sha" = "$sha" ]; then
      return 1
    fi
    if ! printf '%s\n' "$existing" | grep -Fq "$marker"; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$marker" >/dev/null || return 2
      existing+=$'\n'"$marker"
    fi
  done
  return 0
}

start_pr_fix_task() {
  local pr="$1" issue="$2" sha="$3" prompt="$4" review_id="${5:-}" feedback_signature="${6:-}" \
    round_task="${7:-}" human_events="${8:-}" origin_round="${9:-}"
  local pr_data branch base_ref task_id task_state launch_id labels review_lookup_rc
  pr_data="$(gh pr view "$pr" --repo "$REPO" --json state,headRefOid,headRefName,baseRefName)" || return 1
  if ! jq -e --arg sha "$sha" '.state == "OPEN" and .headRefOid == $sha' <<< "$pr_data" >/dev/null; then
    echo "PR #$pr is closed or its head moved; not starting a fix task for $sha" >&2
    return 1
  fi
  branch="$(jq -er '.headRefName | select(type == "string" and length > 0)' <<< "$pr_data")" || return 1
  base_ref="$(jq -er '.baseRefName | select(type == "string" and length > 0)' <<< "$pr_data")" || return 1
  labels="$(issue_labels "$issue")" || return 1
  if printf '%s\n' "$labels" | grep -Fxq "$BLOCKED_LABEL"; then
    echo "Issue #$issue is blocked for human attention; not starting a coding task"
    return 3
  fi
  if ! issue_open "$issue"; then
    echo "Issue #$issue is closed; not starting a coding task"
    return 1
  fi
  if [ -n "$review_id" ]; then
    if issue_has_review_task "$issue" "$review_id"; then
      echo "Review $review_id was already handed off"
      return 3
    else
      review_lookup_rc=$?
      [ "$review_lookup_rc" -eq 1 ] || return 1
    fi
  fi
  task_state="$(agent_task_state "$issue")" || {
    echo "Unable to read the latest Copilot task for Issue #$issue; refusing to launch another task" >&2
    return 1
  }
  case "$task_state" in
    queued|in_progress)
      echo "Copilot task for Issue #$issue is already $task_state; not starting another task"
      return 3 ;;
    completed|failed|cancelled) ;;
    *)
      echo "Copilot task for Issue #$issue has unknown state '$task_state'; refusing to launch another task" >&2
      return 1 ;;
  esac
  launch_id="$(date -u +%s%N)-$$"
  record_agent_task_pending "$issue" "$launch_id" "$sha" "$review_id" "$feedback_signature" \
    "$round_task" "$human_events" "$origin_round" || {
    echo "Unable to persist task launch intent; not starting another task" >&2
    return 1
  }
  task_id="$(start_agent_task "$prompt" "$base_ref" "$branch" false)" || {
    block_for_human "$pr" "$issue" \
      "The Copilot task for the current review feedback could not be started. Please inspect the task service before retrying." \
      "<!-- agent-orchestrator:task-launch-failed=$launch_id -->"
    return 1
  }
  if ! record_agent_task "$issue" "$task_id" "$sha" "$review_id" "$feedback_signature" \
    "$launch_id" "$round_task" "$human_events" "$origin_round"; then
    block_for_human "$pr" "$issue" \
      "Copilot task $task_id was launched but could not be tracked. Review is disabled until a human verifies the task state." \
      "<!-- agent-orchestrator:untracked-task=$launch_id -->"
    return 1
  fi
  printf '%s' "$task_id"
}

record_human_feedback_event() {
  local pr="$1" event_id="$2" sha="$3" kind="$4" feedback="$5" marker existing encoded
  [ -n "$event_id" ] || return 1
  marker="<!-- agent-orchestrator:human-feedback-event=$event_id -->"
  existing="$(pr_bot_comments "$pr")" || return 1
  if printf '%s\n' "$existing" | grep -Fq "$marker"; then return 0; fi
  encoded="$(printf '%s' "$feedback" | base64 -w0)" || return 1
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body \
    "$(printf 'Human %s feedback on %s:\nfeedback-base64=%s\n\n%s' "$kind" "$sha" "$encoded" "$marker")" >/dev/null
}

reconcile_human_feedback_events() {
  local pr="$1" issue="$2" pr_data started_at reviews comments events event
  pr_data="$(gh pr view "$pr" --repo "$REPO" --json createdAt,state)" || return 1
  started_at="$(jq -er '.createdAt | select(type == "string" and length > 0)' <<< "$pr_data")" || return 1
  reviews="$(gh api --paginate -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/pulls/$pr/reviews?per_page=100")" || return 1
  comments="$(gh api --paginate -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/pulls/$pr/comments?per_page=100")" || return 1
  events="$(jq -cn --arg started "$started_at" --slurpfile reviews <(jq -s 'add // []' <<< "$reviews") \
      --slurpfile comments <(jq -s 'add // []' <<< "$comments") '
    [($reviews[0][]? | select((.user.type // "") != "Bot" and (.state // "") != "APPROVED")
       | select((.submitted_at // "") >= $started)
       | {id: ("review-" + (.id | tostring)), sha: (.commit_id // ""), kind: "review", body: (.body // "")}),
     ($comments[0][]? | select((.user.type // "") != "Bot")
       | select((.created_at // "") >= $started)
       | {id: ("comment-" + (.id | tostring)), sha: (.commit_id // ""), kind: "inline review", body: (.body // "")})]
    | sort_by(.id)')"
  while IFS= read -r event; do
    [ -n "$event" ] || continue
    record_human_feedback_event "$pr" "$(jq -r '.id' <<< "$event")" \
      "$(jq -r '.sha' <<< "$event")" "$(jq -r '.kind' <<< "$event")" "$(jq -r '.body' <<< "$event")" || return 1
  done < <(jq -c '.[]' <<< "$events")
}

process_human_feedback() {
  local pr="$1" issue="$2" pr_data sha comments issue_comments task_lines event_ids pending_event_ids="" event_id event_body event_feedback encoded feedback="" signature handoff_id handoff_fingerprint task_id task_result state processed_markers="" labels task_line task_events event_handoff event_handoff_feedback issue_state_rc origin_task_id origin_task_comment origin_feedback origin_round progress_rc
  pr_data="$(gh pr view "$pr" --repo "$REPO" --json state,headRefOid,comments)" || return 1
  if ! jq -e '.state == "OPEN"' <<< "$pr_data" >/dev/null; then return 0; fi
  if issue_open "$issue"; then :; else
    issue_state_rc=$?
    [ "$issue_state_rc" -eq 1 ] && return 0
    return 1
  fi
  labels="$(issue_labels "$issue")" || return 1
  if printf '%s\n' "$labels" | grep -Fxq "$BLOCKED_LABEL"; then
    echo "Issue #$issue is blocked for human attention; preserving pending human feedback"
    return 0
  fi
  sha="$(jq -er '.headRefOid | select(type == "string" and length > 0)' <<< "$pr_data")" || return 1
  comments="$(jq -r '.comments[] | select(.author.login == "github-actions") | .body' <<< "$pr_data")" || return 1
  event_ids="$(grep -oE 'agent-orchestrator:human-feedback-event=[A-Za-z0-9._-]+' <<< "$comments" \
    | sed 's/.*=//' | sort -u || true)"
  [ -n "$event_ids" ] || return 0
  issue_comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
  task_lines="$(jq -r '.comments[]? | select(.author.login == "github-actions") | .body
    | select(contains("agent-orchestrator:task-id="))' <<< "$issue_comments")" || return 1
  while IFS= read -r event_id; do
    [ -n "$event_id" ] || continue
    if printf '%s\n' "$comments" | grep -Fq "agent-orchestrator:human-feedback-processed=$event_id "; then
      continue
    fi
    event_handoff=""
    while IFS= read -r task_line; do
      task_events="$(sed -nE 's/.*human-events=([^ ]*).*/\1/p' <<< "$task_line")"
      if printf '%s\n' "$task_events" | tr ',' '\n' | grep -Fxq "$event_id"; then
        event_handoff="$(sed -nE 's/.*review-id=([^ ]*).*/\1/p' <<< "$task_line")"
        event_handoff_feedback="$(sed -nE 's/.*feedback=([A-Za-z0-9]+).*/\1/p' <<< "$task_line")"
        break
      fi
    done <<< "$task_lines"
    if [ -n "$event_handoff" ]; then
      [ -n "$event_handoff_feedback" ] || return 1
      record_human_feedback_finding "$pr" "$event_handoff_feedback" || return 1
      processed_markers+="<!-- agent-orchestrator:human-feedback-processed=$event_id handoff=$event_handoff -->"$'\n'
      continue
    fi
    pending_event_ids+="$event_id"$'\n'
    event_body="$(jq -er --arg marker "agent-orchestrator:human-feedback-event=$event_id" \
      '[.comments[] | select(.author.login == "github-actions") | select(.body | contains($marker)) | .body] | last // empty' \
      <<< "$pr_data")" || return 1
    if ! grep -Eq '^feedback-base64=[A-Za-z0-9+/=]*$' <<< "$event_body"; then
      echo "Human feedback event $event_id has no valid encoded feedback field" >&2
      return 1
    fi
    encoded="$(sed -nE 's/^feedback-base64=([A-Za-z0-9+/=]*)$/\1/p' <<< "$event_body")"
    event_feedback="$(printf '%s' "$encoded" | base64 --decode)" || return 1
    [ -z "$event_feedback" ] || feedback+="$event_feedback"$'\n\n'
  done <<< "$event_ids"
  if [ -z "$pending_event_ids" ]; then
    if [ -n "$processed_markers" ]; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$processed_markers" >/dev/null || return 1
    fi
    return 0
  fi
  if [ -z "$(printf '%s' "$feedback" | tr -d '[:space:]')" ]; then
    echo "Pending human events contain no substantive text; no coder task is needed"
    while IFS= read -r event_id; do
      [ -n "$event_id" ] || continue
      processed_markers+="<!-- agent-orchestrator:human-feedback-processed=$event_id handoff=none -->"$'\n'
    done <<< "$pending_event_ids"
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$processed_markers" >/dev/null || return 1
    return 0
  fi
  handoff_fingerprint="$(printf '%s' "$pending_event_ids" | sort -u | sha256sum | cut -d' ' -f1)"
  signature="$handoff_fingerprint"
  handoff_id="human-$handoff_fingerprint"
  if issue_has_review_task "$issue" "$handoff_id"; then
    task_result=0
  else
    task_result=$?
    [ "$task_result" -eq 1 ] || return 1
    state="$(agent_task_state "$issue")" || return 1
    case "$state" in
      queued|in_progress)
        echo "Human feedback for PR #$pr is durably recorded; waiting for the active task"
        return 3 ;;
      completed|failed|cancelled) ;;
      *)
        echo "Latest Copilot task has unknown state '$state'; preserving human feedback" >&2
        return 1 ;;
    esac
    origin_task_id="$(agent_task_id "$issue")" || return 1
    origin_task_comment="$(jq -cer --arg task "$origin_task_id" \
      '[.comments[] | select(.author.login == "github-actions")
        | select((.body | split("agent-orchestrator:task-id=")[1] // "" | split(" ")[0]) == $task)]
       | last // empty' <<< "$issue_comments")" || return 1
    origin_feedback="$(jq -r '.body' <<< "$origin_task_comment" \
      | sed -nE 's/.*feedback=([A-Za-z0-9]+).*/\1/p')"
    origin_round=""
    if [ -n "$origin_feedback" ]; then
      if completed_round_progress "$pr" "$issue" "$sha"; then
        :
      else
        progress_rc=$?
        if [ "$progress_rc" -eq 1 ]; then
          block_for_human "$pr" "$issue" \
            "The completed Copilot feedback round has no verified finding-linked response. Human feedback is preserved until a person resolves the outstanding round." \
            "<!-- agent-orchestrator:human-feedback-round-stalled=$origin_task_id -->" || return 1
          return 0
        fi
        echo "Unable to validate the completed Copilot feedback round; preserving human feedback" >&2
        return 1
      fi
      origin_round="$(jq -r '.body' <<< "$origin_task_comment" \
        | sed -nE 's/.*round-task=([^ ]*).*/\1/p')"
      origin_round="${origin_round:-$origin_task_id}:$origin_feedback"
    fi
    task_id="$(start_pr_fix_task "$pr" "$issue" "$sha" \
      "Address only the pending human review events on this PR. Their event IDs are: $(tr '\n' ' ' <<< "$pending_event_ids"). Preserve those identities, implement requested changes, run relevant tests, and resolve addressed threads. For this handoff, post a separate response comment containing the exact marker below, Finding: $signature, Outcome: fixed or Outcome: rebutted, and an Evidence: line of at least 40 non-whitespace characters naming a test/spec or source file. Fixed responses must also name Changed file: <path> in the revision. Do not merge.\n\n<!-- agent-orchestrator:response-feedback=$signature -->\n\n$feedback" \
      "$handoff_id" "$signature" "" "$(tr '\n' ',' <<< "$pending_event_ids" | sed 's/,$//')" "$origin_round")" || {
      task_result=$?
      [ "$task_result" -eq 3 ] && return 3
      return "$task_result"
    }
    echo "Started Copilot task $task_id for human feedback on PR #$pr"
  fi
  record_human_feedback_finding "$pr" "$signature" || return 1
  while IFS= read -r event_id; do
    [ -n "$event_id" ] || continue
    processed_markers+="<!-- agent-orchestrator:human-feedback-processed=$event_id handoff=$signature -->"$'\n'
  done <<< "$pending_event_ids"
  if [ -n "$processed_markers" ]; then
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$processed_markers" >/dev/null || return 1
  fi
  set_state "$issue" "$WORKING_LABEL"
}

issue_events() {
  local response
  response="$(gh api --paginate "repos/$REPO/issues/$1/events?per_page=100")" || return 1
  jq -s 'add // []' <<< "$response"
}

issue_from_mapping_comment() {
  local pr="$1" open_json="$2" candidate comments
  for candidate in $(agent_issue_numbers "$open_json"); do
    comments="$(issue_comment_bodies "$candidate")" || return 1
    if printf '%s\n' "$comments" | grep -Eq "agent-orchestrator:pr=${pr}([^0-9]|$)|Mapped automatically to PR #${pr}([^0-9]|$)"; then
      printf '%s' "$candidate"; return 0
    fi
  done
}

# Succeeds when the Issue was (re)launched via agent:ready -> agent:working shortly before the PR was created.
launched_before() {
  local issue="$1" created="$2" events
  events="$(issue_events "$issue")" || return 2
  [ -n "$events" ] || return 1
  if jq -e --arg r "$READY_LABEL" --arg w "$WORKING_LABEL" --arg created "$created" \
      --argjson window "${LAUNCH_MATCH_WINDOW_SECONDS:-1800}" '
    ([.[] | select(.event == "labeled" and .label.name == $r) | .created_at | fromdateiso8601] | max) as $ready
    | ($created | fromdateiso8601) as $pr
    | $ready != null
      and any(.[]; .event == "labeled" and .label.name == $w
        and (.created_at | fromdateiso8601) >= $ready
        and (.created_at | fromdateiso8601) < $pr)
      and $pr >= $ready and ($pr - $ready) <= $window' <<< "$events" >/dev/null 2>&1; then
    return 0
  else
    local parse_rc=$?
    [ "$parse_rc" -eq 1 ] && return 1
    return 2
  fi
}

# Succeeds when no other PR mapped to the Issue is still open or newer than this PR.
no_other_live_pr() {
  local issue="$1" pr="$2" mapped other state comments
  comments="$(issue_comment_bodies "$issue")" || return 2
  mapped="$(grep -oE 'agent-orchestrator:pr=[0-9]+' <<< "$comments" | sed 's/.*=//' | sort -un || true)"
  for other in $mapped; do
    if [ "$other" = "$pr" ]; then continue; fi
    if [ "$other" -gt "$pr" ]; then return 1; fi
    state="$(gh pr view "$other" --repo "$REPO" --json state --jq '.state' 2>/dev/null)" || return 2
    if [ "$state" != "CLOSED" ] && [ "$state" != "MERGED" ]; then return 1; fi
  done
}

# Replacement PRs: an Issue in any agent state (including agent:blocked after a closed-unmerged PR) that was
# relaunched shortly before this PR was opened and has no other live PR. The match must be unambiguous.
issue_from_relaunch() {
  local pr="$1" created="$2" open_json="$3" candidate matches=()
  if [ -z "$created" ]; then return 0; fi
  for candidate in $(agent_issue_numbers "$open_json"); do
    if launched_before "$candidate" "$created"; then
      if no_other_live_pr "$candidate" "$pr"; then
        matches+=("$candidate")
      else
        local live_pr_rc=$?
        [ "$live_pr_rc" -eq 1 ] || return 2
      fi
    else
      local launch_rc=$?
      [ "$launch_rc" -eq 1 ] || return 2
    fi
  done
  if [ "${#matches[@]}" -eq 1 ]; then
    echo "Mapped PR #$pr to Issue #${matches[0]}, relaunched shortly before the PR was opened" >&2
    printf '%s' "${matches[0]}"
  elif [ "${#matches[@]}" -gt 1 ]; then
    echo "PR #$pr could replace several relaunched Issues (${matches[*]}); not guessing" >&2
  fi
}

resolve_issue() {
  # Order: PR body marker -> branch name -> Issue comment -> explicit PR body reference
  #        -> Issue relaunched just before the PR was opened -> single active Issue.
  local pr="$1" branch="$2" pr_json body created issue open_json comments active
  pr_json="$(gh pr view "$pr" --repo "$REPO" --json body,createdAt)" || return 1
  body="$(jq -r '.body // ""' <<< "$pr_json")"
  created="$(jq -r '.createdAt // ""' <<< "$pr_json")"
  issue="$(issue_from_marker "$body")"
  if [ -n "$issue" ]; then
    comments="$(issue_comment_bodies "$issue")" || return 1
    if ! printf '%s\n' "$comments" | grep -Eq "agent-orchestrator:pr=${pr}([^0-9]|$)"; then
      issue=""
    fi
  fi
  if [ -z "$issue" ]; then issue="$(issue_from_branch "$branch")"; fi
  open_json="$(gh issue list --repo "$REPO" --state open --limit 200 --json number,labels)" || return 1
  if [ -z "$issue" ]; then issue="$(issue_from_mapping_comment "$pr" "$open_json")" || return 1; fi
  if [ -z "$issue" ]; then issue="$(issue_from_body_reference "$body")" || return 1; fi
  if [ -z "$issue" ]; then issue="$(issue_from_relaunch "$pr" "$created" "$open_json")" || return 1; fi
  if [ -z "$issue" ]; then
    active="$(jq -r --arg w "$WORKING_LABEL" --arg a "$AI_REVIEW_LABEL" --arg r "$REVIEW_LABEL" \
        '[.[] | select(any(.labels[]?.name; . == $w or . == $a or . == $r)) | .number] | if length == 1 then .[0] else empty end' <<< "$open_json")"
    if [ -n "$active" ]; then echo "Mapped PR #$pr to the only active Issue #$active" >&2; issue="$active"; fi
  fi
  if [ -n "$issue" ]; then
    if ! printf '%s\n' "$body" | grep -Eq "agent-orchestrator:issue=${issue}([^0-9]|$)"; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr edit "$pr" --repo "$REPO" --body \
        "$(printf '%s\n\n<!-- agent-orchestrator:issue=%s -->\nCloses #%s\n' "$body" "$issue" "$issue")" >/dev/null || return 1
    fi
    comments="$(issue_comment_bodies "$issue")" || return 1
    if ! printf '%s\n' "$comments" | grep -Eq "agent-orchestrator:pr=${pr}([^0-9]|$)"; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body \
        "$(printf '<!-- agent-orchestrator:pr=%s -->\nMapped automatically to PR #%s.' "$pr" "$pr")" >/dev/null || return 1
    fi
  fi
  printf '%s' "$issue"
}

issue_labels() {
  gh issue view "$1" --repo "$REPO" --json labels --jq '.labels[]?.name // empty'
}

set_state() {
  local issue="$1" target="$2" labels label issue_state_rc
  if [ -n "$target" ]; then
    if issue_open "$issue"; then :; else
      issue_state_rc=$?
      [ "$issue_state_rc" -eq 1 ] && {
        echo "Issue #$issue is closed; not restoring agent state labels"
        return 0
      }
      return 1
    fi
  fi
  labels="$(issue_labels "$issue")" || return 1
  for label in "$READY_LABEL" "$WORKING_LABEL" "$AI_REVIEW_LABEL" "$REVIEW_LABEL" "$BLOCKED_LABEL"; do
    if [ "$label" != "$target" ] && printf '%s\n' "$labels" | grep -Fxq "$label"; then
      gh issue edit "$issue" --repo "$REPO" --remove-label "$label" >/dev/null || return 1
    fi
  done
  if [ -n "$target" ] && ! printf '%s\n' "$labels" | grep -Fxq "$target"; then
    gh issue edit "$issue" --repo "$REPO" --add-label "$target" >/dev/null || return 1
  fi
}

# Succeeds when the Issue moved on after the PR closed: its latest mapping points at a different PR, or it was
# relaunched (agent:ready) after the PR closed. A late close event must then not re-block the Issue.
issue_superseded() {
  local issue="$1" pr="$2" closed_at="$3" latest events comments
  comments="$(issue_comment_bodies "$issue")" || return 2
  latest="$(grep -oE 'agent-orchestrator:pr=[0-9]+' <<< "$comments" | sed 's/.*=//' | tail -1 || true)"
  if [ -n "$latest" ] && [ "$latest" != "$pr" ]; then return 0; fi
  if [ -z "$closed_at" ]; then return 1; fi
  events="$(issue_events "$issue")" || return 2
  [ -n "$events" ] || return 1
  jq -e --arg r "$READY_LABEL" --arg closed "$closed_at" \
    'any(.[]; ((.event == "labeled" and .label.name == $r) or .event == "reopened")
      and (.created_at | fromdateiso8601) >= ($closed | fromdateiso8601))' \
    <<< "$events" >/dev/null 2>&1
}

handle_pr_closed() {
  local pr="$1" issue="$2" merged="$3" closed_at="$4" issue_state_rc
  if issue_superseded "$issue" "$pr" "$closed_at"; then
    echo "Issue #$issue was relaunched or mapped to a newer PR; leaving its state unchanged"
    return 0
  else
    issue_state_rc=$?
    [ "$issue_state_rc" -eq 1 ] || return 1
  fi
  if [ "$merged" = "true" ]; then
    if issue_open "$issue"; then
      set_state "$issue" "" || return 1
      gh issue close "$issue" --repo "$REPO" --reason completed || return 1
    else
      issue_state_rc=$?
      [ "$issue_state_rc" -eq 1 ] || return 1
    fi
  else
    if issue_open "$issue"; then
      set_state "$issue" "$BLOCKED_LABEL" || return 1
    else
      issue_state_rc=$?
      [ "$issue_state_rc" -eq 1 ] || return 1
    fi
  fi
}

record_terminal_transition() {
  local pr="$1" issue="$2" merged="$3" closed_at="$4" terminal_id marker issue_data comments labels
  terminal_id="$pr-$(printf '%s' "$closed_at" | tr -c 'A-Za-z0-9._-' '-')"
  marker="<!-- agent-orchestrator:terminal-pending=$terminal_id pr=$pr merged=$merged closed-at=$closed_at -->"
  issue_data="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
  comments="$(jq -r '.comments[]? | select(.author.login == "github-actions") | .body // empty' <<< "$issue_data")" || return 1
  labels="$(issue_labels "$issue")" || return 1
  if ! printf '%s\n' "$labels" | grep -Fxq "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}"; then
    gh issue edit "$issue" --repo "$REPO" --add-label "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}" >/dev/null || return 1
  fi
  if ! printf '%s\n' "$comments" | grep -Fq "$marker"; then
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body "$marker" >/dev/null || return 1
  fi
  labels="$(issue_labels "$issue")" || return 1
  if ! printf '%s\n' "$labels" | grep -Fxq "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}"; then
    gh issue edit "$issue" --repo "$REPO" --add-label "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}" >/dev/null || return 1
  fi
}

recover_terminal_transition_for_issue() {
  local issue="$1" issue_data issue_comments pending_prs latest_pr pr pr_data state closed_at merged labels
  issue_data="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
  issue_comments="$(jq -r '.comments[]? | select(.author.login == "github-actions") | .body // empty' <<< "$issue_data")" || return 1
  pending_prs="$(grep -oE 'agent-orchestrator:terminal-pending=[A-Za-z0-9._-]+ pr=[0-9]+' \
    <<< "$issue_comments" | sed -E 's/.* pr=//' | sort -un || true)"
  latest_pr="$(grep -oE 'agent-orchestrator:pr=[0-9]+' <<< "$issue_comments" | sed 's/.*=//' | tail -1)"
  if [ -z "$latest_pr" ]; then
    [ -n "$pending_prs" ] && return 0
    echo "Issue #$issue has no trusted mapped PR for its pending terminal state" >&2
    return 1
  fi
  pr_data="$(gh pr view "$latest_pr" --repo "$REPO" --json state,closedAt,mergedAt)" || return 1
  state="$(jq -er '.state | select(type == "string")' <<< "$pr_data")" || return 1
  case "$state" in
    CLOSED|MERGED)
      closed_at="$(jq -r '.closedAt // empty' <<< "$pr_data")"
      [ -n "$closed_at" ] || return 1
      merged=false
      [ -z "$(jq -r '.mergedAt // empty' <<< "$pr_data")" ] || merged=true
      if ! grep -Fq "pr=$latest_pr merged=$merged closed-at=$closed_at -->" <<< "$issue_comments"; then
        record_terminal_transition "$latest_pr" "$issue" "$merged" "$closed_at" || return 1
      fi ;;
    OPEN)
      if [ -z "$pending_prs" ]; then
        labels="$(issue_labels "$issue")" || return 1
        if printf '%s\n' "$labels" | grep -Fxq "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}"; then
          gh issue edit "$issue" --repo "$REPO" --remove-label "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}" >/dev/null || return 1
        fi
      fi ;;
    *)
      echo "Mapped PR #$latest_pr has unknown state '$state'; preserving pending terminal state" >&2
      return 1 ;;
  esac
}

process_pending_terminal_issue() {
  local issue="$1" comments comment_bodies record terminal_id pr merged closed_at processed unprocessed=0 found=0 labels pr_data pr_state retired
  recover_terminal_transition_for_issue "$issue" || return 1
  comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
  comment_bodies="$(jq -r '.comments[]? | select(.author.login == "github-actions") | .body // empty' <<< "$comments")" || return 1
  while IFS= read -r record; do
    [ -n "$record" ] || continue
    found=1
    terminal_id="$(sed -nE 's/.*agent-orchestrator:terminal-pending=([A-Za-z0-9._-]+).*/\1/p' <<< "$record")"
    pr="$(sed -nE 's/.* pr=([0-9]+) .*/\1/p' <<< "$record")"
    merged="$(sed -nE 's/.* merged=(true|false) .*/\1/p' <<< "$record")"
    closed_at="$(sed -nE 's/.* closed-at=([^ ]*).*/\1/p' <<< "$record")"
    [ -n "$terminal_id" ] && [ -n "$pr" ] && [ -n "$merged" ] || return 1
    processed="agent-orchestrator:terminal-processed=$terminal_id"
    retired="agent-orchestrator:terminal-retired=$terminal_id"
    if grep -Fq "$processed" <<< "$comment_bodies" || grep -Fq "$retired" <<< "$comment_bodies"; then
      continue
    fi
    pr_data="$(gh pr view "$pr" --repo "$REPO" --json state)" || return 1
    pr_state="$(jq -er '.state | select(type == "string")' <<< "$pr_data")" || return 1
    if [ "$pr_state" = OPEN ]; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body \
        "<!-- $retired -->" >/dev/null || return 1
    else
      handle_pr_closed "$pr" "$issue" "$merged" "$closed_at" || return 1
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh issue comment "$issue" --repo "$REPO" --body \
        "<!-- $processed -->" >/dev/null || return 1
    fi
  done < <(jq -r '.comments[]? | select(.author.login == "github-actions") | .body
    | select(contains("agent-orchestrator:terminal-pending="))
    | select(test("agent-orchestrator:terminal-pending=[A-Za-z0-9._-]+ pr=[0-9]+ merged=(true|false)"))' <<< "$comments")
  if [ "$found" -eq 0 ]; then
    labels="$(issue_labels "$issue")" || return 1
    if printf '%s\n' "$labels" | grep -Fxq "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}"; then
      echo "Issue #$issue has a pending terminal label but no durable closure record" >&2
      return 1
    fi
    return 0
  fi
  comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
  comment_bodies="$(jq -r '.comments[]? | select(.author.login == "github-actions") | .body // empty' <<< "$comments")" || return 1
  while IFS= read -r record; do
    [ -n "$record" ] || continue
    terminal_id="$(sed -nE 's/.*agent-orchestrator:terminal-pending=([A-Za-z0-9._-]+).*/\1/p' <<< "$record")"
    if ! grep -Fq "agent-orchestrator:terminal-processed=$terminal_id" <<< "$comment_bodies" &&
       ! grep -Fq "agent-orchestrator:terminal-retired=$terminal_id" <<< "$comment_bodies"; then
      unprocessed=1
      break
    fi
  done < <(jq -r '.comments[]? | select(.author.login == "github-actions") | .body
    | select(contains("agent-orchestrator:terminal-pending="))
    | select(test("agent-orchestrator:terminal-pending=[A-Za-z0-9._-]+ pr=[0-9]+ merged=(true|false)"))' <<< "$comments")
  if [ "$unprocessed" -eq 0 ]; then
    labels="$(issue_labels "$issue")" || return 1
    if printf '%s\n' "$labels" | grep -Fxq "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}"; then
      gh issue edit "$issue" --repo "$REPO" --remove-label "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}" >/dev/null || return 1
    fi
    comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
    comment_bodies="$(jq -r '.comments[]? | select(.author.login == "github-actions") | .body // empty' <<< "$comments")" || return 1
    while IFS= read -r record; do
      [ -n "$record" ] || continue
      terminal_id="$(sed -nE 's/.*agent-orchestrator:terminal-pending=([A-Za-z0-9._-]+).*/\1/p' <<< "$record")"
      if ! grep -Fq "agent-orchestrator:terminal-processed=$terminal_id" <<< "$comment_bodies" &&
         ! grep -Fq "agent-orchestrator:terminal-retired=$terminal_id" <<< "$comment_bodies"; then
        unprocessed=1
        break
      fi
    done < <(jq -r '.comments[]? | select(.author.login == "github-actions") | .body
      | select(contains("agent-orchestrator:terminal-pending="))
      | select(test("agent-orchestrator:terminal-pending=[A-Za-z0-9._-]+ pr=[0-9]+ merged=(true|false)"))' <<< "$comments")
    if [ "$unprocessed" -eq 1 ]; then
      labels="$(issue_labels "$issue")" || return 1
      if ! printf '%s\n' "$labels" | grep -Fxq "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}"; then
        gh issue edit "$issue" --repo "$REPO" --add-label "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}" >/dev/null || return 1
      fi
    fi
  fi
}

terminal_pending_issue_numbers() {
  local issues
  issues="$(gh issue list --repo "$REPO" --state all --label "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}" \
    --limit 1000 --json number,labels)" || return 1
  jq -r --arg label "${TERMINAL_PENDING_LABEL:-agent:terminal-pending}" \
    '.[] | select(any(.labels[]?.name; . == $label)) | .number' <<< "$issues"
}

reconcile_terminal_transitions() {
  local issues issue
  issues="$(terminal_pending_issue_numbers)" || return 1
  while IFS= read -r issue; do
    [ -n "$issue" ] || continue
    process_pending_terminal_issue "$issue" || return 1
  done <<< "$issues"
}

current_copilot_review() {
  local pr="$1" sha="$2" issue="$3" reviews task_json task_updated task_id issue_comments task_comment signature pr_comments response_at review
  reviews="$(gh api --paginate -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/pulls/$pr/reviews?per_page=100")" || return 2
  task_json="$(agent_task_json "$issue")" || return 2
  task_updated="$(jq -er '.updated_at | select(type == "string" and length > 0)' <<< "$task_json")" || return 2
  task_id="$(agent_task_id "$issue")" || return 2
  issue_comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 2
  task_comment="$(jq -cer --arg task "$task_id" \
    '[.comments[] | select(.author.login == "github-actions")
      | select((.body | split("agent-orchestrator:task-id=")[1] // "" | split(" ")[0]) == $task)]
     | last // empty' <<< "$issue_comments")" || return 2
  signature="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*feedback=([A-Za-z0-9]+).*/\1/p')"
  if [ -n "$signature" ]; then
    pr_comments="$(pr_bot_comments "$pr")" || return 2
    response_at="$(printf '%s\n' "$pr_comments" | grep -F "agent-orchestrator:response-used-content=" \
      | grep -F "feedback=$signature " | grep -F "task=$task_id " \
      | sed -nE 's/.*response-at=([^ ]*).*/\1/p' | sort | tail -1)"
    [ -n "$response_at" ] || return 1
  else
    response_at="$task_updated"
  fi
  jq -se 'add // [] | type == "array"' <<< "$reviews" >/dev/null 2>&1 || return 2
  review="$(jq -sc --arg sha "$sha" --arg updated "$task_updated" --arg response "$response_at" \
    'add // [] | map(select((.user.login // "" | startswith("copilot-pull-request-reviewer")) and .commit_id == $sha and (.submitted_at // "") >= $updated and (.submitted_at // "") > $response))
     | sort_by(.submitted_at // "") | last // empty' <<< "$reviews")" || return 2
  [ -n "$review" ] && [ "$review" != "null" ] || return 1
  printf '%s' "$review"
}

request_copilot_review() {
  local pr="$1" issue="$2" sha="$3" requested result
  if current_copilot_review "$pr" "$sha" "$issue" >/dev/null 2>&1; then
    echo "Copilot already reviewed current task response for $sha"
    return 0
  else
    result=$?
  fi
  [ "$result" -eq 2 ] && return 1
  requested="$(gh api -H 'Accept: application/vnd.github+json' "repos/$REPO/pulls/$pr/requested_reviewers")" || return 1
  jq -e 'type == "object" and (.users | type == "array")' <<< "$requested" >/dev/null || return 1
  if jq -e 'any(.users[]?; .login | startswith("copilot-pull-request-reviewer"))' <<< "$requested" >/dev/null; then
    echo "Copilot review already pending"; return 0
  fi
  gh api --silent --method POST -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/pulls/$pr/requested_reviewers" -f "reviewers[]=$COPILOT_REVIEWER" || return 1
  echo "Copilot review requested for PR #$pr"
}

review_eligible() {
  local pr="$1" issue="$2" sha="$3" pr_data task_state progress_rc labels feedback_rc
  pr_data="$(gh pr view "$pr" --repo "$REPO" --json state,headRefOid)" || return 1
  if ! jq -e --arg sha "$sha" '.state == "OPEN" and .headRefOid == $sha' <<< "$pr_data" >/dev/null; then
    echo "PR #$pr is closed or its head no longer matches $sha; ignoring review event"
    return 1
  fi
  labels="$(issue_labels "$issue")" || return 1
  if printf '%s\n' "$labels" | grep -Fxq "$BLOCKED_LABEL"; then
    echo "Issue #$issue is blocked for human attention; ignoring automated review"
    return 1
  fi
  if pending_human_feedback "$pr"; then
    echo "PR #$pr has pending human feedback; deferring automated review"
    return 1
  else
    feedback_rc=$?
    [ "$feedback_rc" -eq 1 ] || return 1
  fi
  if ! issue_open "$issue"; then
    echo "Issue #$issue is closed; ignoring automated review"
    return 1
  fi
  task_state="$(agent_task_state "$issue")" || {
    echo "Unable to read Copilot task state for Issue #$issue; not reviewing"
    return 1
  }
  case "$task_state" in
    completed) ;;
    queued|in_progress)
      echo "Copilot implementation for Issue #$issue is not complete; not reviewing $sha"
      return 1 ;;
    *)
      block_for_human "$pr" "$issue" \
        "Copilot task for Issue #$issue ended in state '$task_state'. Please inspect the task and decide how to proceed." \
        "<!-- agent-orchestrator:task-failure=$task_state -->"
      return 1 ;;
  esac
  if ! ci_green "$sha"; then
    echo "CI for current PR head $sha is not green; not reviewing"
    return 1
  fi
  if completed_round_progress "$pr" "$issue" "$sha"; then
    progress_rc=0
  else
    progress_rc=$?
  fi
  if [ "$progress_rc" -eq 1 ]; then
    block_for_human "$pr" "$issue" \
      "The completed coding task did not provide a verified finding-linked fix or new substantive response. Please decide how to resolve the outstanding feedback." \
      "<!-- agent-orchestrator:no-progress-task=$sha -->"
    return 1
  elif [ "$progress_rc" -ne 0 ]; then
    echo "Unable to verify completed response evidence for $sha; deferring review" >&2
    return 1
  fi
  local green_comments green_attempts green_run_id green_marker
  green_comments="$(pr_bot_comments "$pr")" || return 1
  green_attempts="$(ci_fix_attempt_count "$green_comments")"
  if [ "$green_attempts" -gt 0 ]; then
    green_run_id="$(ci_green_run_identity "$sha")" || return 1
    [ -n "$green_run_id" ] || return 1
    green_run_id="$(printf '%s' "$green_run_id" | tr -c 'A-Za-z0-9._-' '-')"
    green_marker="<!-- agent-orchestrator:ci-fix-green=$sha-$green_run_id -->"
    if ! printf '%s\n' "$green_comments" | grep -Fq "$green_marker"; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$green_marker" >/dev/null || return 1
    fi
  fi
  return 0
}

request_review_if_eligible() {
  local pr="$1" issue="$2" sha="$3" task_id marker existing result timeout_result review
  review_eligible "$pr" "$issue" "$sha" || return 1
  task_id="$(agent_task_id "$issue")" || return 1
  marker="<!-- agent-orchestrator:review-requested-sha=$sha task-id=$task_id -->"
  existing="$(pr_bot_comments "$pr")" || return 2
  if current_copilot_review "$pr" "$sha" "$issue" >/dev/null 2>&1; then
    result=0
  else
    result=$?
    [ "$result" -eq 2 ] && {
      echo "Unable to determine whether the current-task review exists; deferring" >&2
      return 2
    }
  fi
  if [ "$result" -eq 1 ] && printf '%s\n' "$existing" | grep -Fq "$marker"; then
    if review_request_expired "$pr" "$sha" "$task_id"; then timeout_result=0; else timeout_result=$?; fi
    if [ "$timeout_result" -eq 2 ]; then
      echo "Unable to verify the Copilot review request timestamp; deferring" >&2
      return 2
    fi
    if [ "$timeout_result" -eq 0 ]; then
      block_for_human "$pr" "$issue" \
        "Copilot review did not arrive before the review timeout for $sha. Please review the PR manually or re-enable automated review." \
        "<!-- agent-orchestrator:review-timeout=$sha task-id=$task_id -->"
      return 0
    fi
  fi
  if [ "$result" -eq 1 ]; then
    request_copilot_review "$pr" "$issue" "$sha" || return 2
    existing="$(pr_bot_comments "$pr")" || return 1
    if ! printf '%s\n' "$existing" | grep -Fq "$marker"; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body \
        "Requested Copilot review for the completed task on $sha. $marker" >/dev/null || return 2
    fi
  fi
  if [ "$result" -eq 0 ]; then
    review="$(current_copilot_review "$pr" "$sha" "$issue")" || return 2
  else
    review=""
  fi
  if [ -n "$review" ] && [ "$review" != "null" ]; then
    result="$(jq -er '.id | tostring' <<< "$review")" || return 2
    if review_already_handled "$issue" "$result"; then
      echo "Review $result is already processed; preserving the existing Issue state"
      return 0
    else
      timeout_result=$?
      [ "$timeout_result" -eq 1 ] || return 2
    fi
  fi
  set_state "$issue" "$AI_REVIEW_LABEL"
}

review_request_expired() {
  local pr="$1" sha="$2" task_id="$3" comments created_at requested_at
  comments="$(gh pr view "$pr" --repo "$REPO" --json comments)" || return 2
  created_at="$(jq -r --arg sha "$sha" --arg task "$task_id" \
    '[.comments[] | select(.author.login == "github-actions")
      | select(.body | contains("agent-orchestrator:review-requested-sha=" + $sha + " task-id=" + $task))
      | .createdAt] | last // empty' <<< "$comments")"
  [ -n "$created_at" ] || return 2
  requested_at="$(date -u -d "$created_at" +%s)" || return 2
  if [ "$(( $(date -u +%s) - requested_at ))" -ge "${REVIEW_TIMEOUT_SECONDS:-1800}" ]; then
    return 0
  fi
  return 1
}

# Returns the latest CI conclusion for the exact SHA.
ci_conclusion() {
  local sha="$1"
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh api -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/actions/workflows/${CI_WORKFLOW_FILE:-ci.yml}/runs?head_sha=$sha&per_page=20" \
    --jq '[.workflow_runs[]?] | sort_by(.created_at) | last | .conclusion // empty' 2>/dev/null
}

# Green CI is necessary but is never evidence by itself that the coding session has completed.
ci_green() {
  [ "$(ci_conclusion "$1" || true)" = "success" ]
}

ci_fix_attempt_count() {
  local attempts=0 marker
  while IFS= read -r marker; do
    case "$marker" in
      agent-orchestrator:ci-fix-attempt=*) attempts="${marker##*=}" ;;
      agent-orchestrator:ci-fix-green=*) attempts=0 ;;
    esac
  done < <(grep -oE 'agent-orchestrator:ci-fix-(attempt=[0-9]+|green=[A-Za-z0-9._-]+)' <<< "$1" || true)
  printf '%s' "$attempts"
}

ci_green_run_identity() {
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh api -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/actions/workflows/${CI_WORKFLOW_FILE:-ci.yml}/runs?head_sha=$1&per_page=20" \
    --jq '[.workflow_runs[]?] | sort_by(.created_at) | last | (.id // .created_at // empty) | tostring'
}

recover_ci_handoff_metadata() {
  local pr="$1" issue="$2" sha="$3" comments task_id task_comment source_sha review_id attempt existing marker
  comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 2
  task_id="$(agent_task_id "$issue")" || return 1
  task_comment="$(jq -cer --arg task "$task_id" \
    '[.comments[] | select(.author.login == "github-actions")
      | select((.body | split("agent-orchestrator:task-id=")[1] // "" | split(" ")[0]) == $task)]
     | last // empty' <<< "$comments")" || return 1
  source_sha="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*source-sha=([^ ]*).*/\1/p')"
  review_id="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*review-id=([^ ]*).*/\1/p')"
  [[ "$source_sha" == "$sha" && "$review_id" == "ci-$sha-"* ]] || return 1
  attempt="${review_id#ci-"$sha"-}"
  [[ "$attempt" =~ ^[0-9]+$ ]] || return 1
  existing="$(pr_bot_comments "$pr")" || return 2
  marker="<!-- agent-orchestrator:ci-failure-sha=$sha -->"
  if printf '%s\n' "$existing" | grep -Fq "$marker" &&
     printf '%s\n' "$existing" | grep -Fq "agent-orchestrator:ci-fix-attempt=$attempt"; then
    return 0
  fi
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body \
    "Recovered CI repair task $task_id for $sha. $marker <!-- agent-orchestrator:ci-fix-task-sha=$sha --> <!-- agent-orchestrator:ci-fix-attempt=$attempt -->" >/dev/null || return 2
}

review_threads() {
  local pr="$1"
  # shellcheck disable=SC2016 # GraphQL variables, not shell expansions.
  gh api graphql \
    -f query='query($owner:String!,$repo:String!,$number:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$number){reviewThreads(first:100){nodes{isResolved comments(first:100){nodes{body path line commit{oid} author{login} pullRequestReview{databaseId}}}}}}}}' \
    -F owner="${REPO%%/*}" -F repo="${REPO#*/}" -F number="$pr"
}

review_findings() {
  local threads="$1" sha="$2" review_id="$3"
  jq -c --arg sha "$sha" --arg review "$review_id" \
    '[.data.repository.pullRequest.reviewThreads.nodes[]?
      | select(.isResolved == false)
      | .comments.nodes[]?
      | select((.author.login // "" | startswith("copilot-pull-request-reviewer")) and .commit.oid == $sha)
      | select((.pullRequestReview.databaseId | tostring) == $review)
      | {path: (.path // "general"), line: (.line // ""), body: (.body // "")}]' <<< "$threads"
}

review_blocking_findings() {
  jq -r '.[] | select(.body | test("^[[:space:]]*(\\[blocking\\]|blocking:)"; "i"))
    | "- \(.path):\(.line) \(.body | sub("^[[:space:]]*(\\[blocking\\]|blocking:)[[:space:]]*"; "")
        | gsub("[[:space:]]+"; " ") | sub("^ "; "") | sub(" $"; ""))"' <<< "$1"
}

feedback_signature() {
  printf '%s\n' "$1" | sed -E 's/^(- [^:]+):[0-9]+ /\1 /' | sort -u | sha256sum | cut -d' ' -f1
}

feedback_finding_signatures() {
  printf '%s\n' "$1" | sed -E 's/^(- [^:]+):[0-9]+ /\1 /' | sed '/^[[:space:]]*$/d' \
    | sort -u | while IFS= read -r finding; do
      printf '%s' "$finding" | sha256sum | cut -d' ' -f1
    done
}

feedback_path_for_finding() {
  local feedback="$1" wanted="$2" finding normalized signature
  while IFS= read -r finding; do
    [ -n "$finding" ] || continue
    normalized="$(sed -E 's/^(- [^:]+):[0-9]+ /\1 /' <<< "$finding")"
    signature="$(printf '%s' "$normalized" | sha256sum | cut -d' ' -f1)"
    if [ "$signature" = "$wanted" ]; then
      printf '%s' "$finding" | sed -nE 's/^- ([^:]+):[0-9]* .*/\1/p'
      return 0
    fi
  done <<< "$feedback"
  return 1
}

feedback_target_signatures() {
  printf '%s\n' "$1" | sed '/^[[:space:]]*$/d' | while IFS= read -r finding; do
    feedback_target_signature "$finding" || true
  done | sort -u
}

feedback_contradicts_prior() {
  local feedback="$1" existing="$2" target_id action prior_action marker marker_target
  while IFS= read -r target_id; do
    [ -n "$target_id" ] || continue
    action="$(feedback_target_action_for_id "$feedback" "$target_id" || printf unknown)"
    while IFS= read -r marker; do
      [ -n "$marker" ] || continue
      marker_target="$(sed -nE 's/.*agent-orchestrator:recommendation=([^ ]*).*/\1/p' <<< "$marker")"
      [ "$marker_target" = "$target_id" ] || continue
      prior_action="$(sed -nE 's/.* action=([^ ]*).*/\1/p' <<< "$marker")"
      case "$action:$prior_action" in
        positive:negative|negative:positive) return 0 ;;
      esac
    done < <(grep -F "agent-orchestrator:recommendation=$target_id " <<< "$existing" || true)
  done < <(feedback_target_signatures "$feedback")
  if printf '%s\n' "$feedback" | grep -Eiq '\b(undo|revert|reverse|restore)\b.*\b(previous|prior|earlier|preceding)\b'; then
    while IFS= read -r target_id; do
      [ -n "$target_id" ] || continue
      if printf '%s\n' "$existing" | grep -Eq "agent-orchestrator:recommendation=$target_id([^A-Za-z0-9._-]|$)"; then
        return 0
      fi
    done < <(feedback_target_signatures "$feedback")
  fi
  return 1
}

parse_blocking_summary() {
  awk '
    function clean(text) {
      sub(/^[[:space:]]+/, "", text)
      sub(/[[:space:]]+$/, "", text)
      return text
    }
    BEGIN { active = 0; found = 0; invalid = 0; none = 0; count = 0 }
    {
      line = $0
      if (!active && line ~ /^[[:space:]]*Blocking findings:/) {
        sub(/^[[:space:]]*Blocking findings:[[:space:]]*/, "", line)
        active = 1
        found = 1
        if (line != "") {
          value = clean(line)
          if (value ~ /^-[[:space:]]+/) {
            sub(/^-[[:space:]]+/, "", value)
            if (tolower(value) ~ /^none[.!]?$/) invalid = 1
            else { items[++count] = "- " value }
          } else if (tolower(value) ~ /^none[.!]?$/) none = 1
          else invalid = 1
        }
        next
      }
      if (active && line ~ /^[[:space:]]*(Advisory|Suggestions?|Non-blocking suggestions?|Notes?|Follow-up):/) {
        active = 0
        next
      }
      if (active && line ~ /^[[:space:]]*[A-Z][A-Za-z -]*:[[:space:]]*$/) {
        active = 0
        next
      }
      if (active) {
        value = clean(line)
        if (value == "") next
        if (value !~ /^-[[:space:]]+/) {
          if (tolower(value) ~ /^none[.!]?$/) none = 1
          else invalid = 1
          next
        }
        sub(/^-[[:space:]]+/, "", value)
        if (tolower(value) ~ /^none[.!]?$/) invalid = 1
        else { items[++count] = "- " value }
      }
    }
    END {
      if (!found) print "absent"
      else if (invalid || (none && count > 0) || (count == 0 && !none)) print "invalid"
      else if (none) print "none"
      else {
        print "valid"
        for (i = 1; i <= count; i++) print items[i]
      }
    }' <<< "$1"
}

escape_external_metadata() {
  printf '%s' "$1" | sed 's/agent-orchestrator:/agent-orchestrator\&#58;/g'
}

handoff_blocking_feedback() {
  local pr="$1" issue="$2" sha="$3" review_id="$4" feedback="$5" summary="${6:-}"
  local signature existing issue_comments task_records response response_id response_hash response_used="" prompt task_id prior_task_id rc marker finding_marker recommendation_marker finding_id finding_ids target_id target_ids target_action prior prior_signature progress_rc response_rc escaped_feedback escaped_summary finding_path path_base64 response_contract
  signature="$(feedback_signature "$feedback")"
  prior_task_id="$(agent_task_id "$issue")" || return 1
  finding_ids="$(feedback_finding_signatures "$feedback")"
  target_ids="$(feedback_target_signatures "$feedback")"
  marker="<!-- agent-orchestrator:feedback=$signature findings=$(tr '\n' ',' <<< "$finding_ids" | sed 's/,$//') -->"
  existing="$(pr_bot_comments "$pr")" || return 1
  issue_comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
  task_records="$(jq -r '.comments[] | select(.author.login == "github-actions") | .body
    | select(contains("agent-orchestrator:task-id="))' <<< "$issue_comments")" || return 1
  response=""
  for finding_id in $finding_ids; do
    if printf '%s\n' "$existing" | grep -Eq "agent-orchestrator:finding=$finding_id([^A-Za-z0-9._-]|$)"; then
      prior="$(printf '%s\n' "$existing" | grep -F "agent-orchestrator:finding=$finding_id" | tail -1)"
      prior_signature="$(sed -nE 's/.*feedback=([A-Za-z0-9]+).*/\1/p' <<< "$prior")"
      if ! printf '%s\n' "$task_records" | grep -Fq "feedback=$prior_signature "; then
        continue
      fi
      if response="$(coder_response_for_feedback "$pr" "$prior_signature" "$issue" "$finding_id")"; then
        :
      else
        response_rc=$?
        [ "$response_rc" -eq 1 ] || return 1
      fi
      if [ -z "$response" ]; then
        block_for_human "$pr" "$issue" \
          "A blocking finding repeated after a completed coding response without a new evidence-based rebuttal. Please decide how to resolve the finding." \
          "<!-- agent-orchestrator:no-progress=$finding_id -->"
        return 0
      fi
      response_id="$(jq -r '.id' <<< "$response")"
      response_hash="$(response_content_fingerprint "$(jq -r '.body' <<< "$response")")"
      if printf '%s\n' "$existing" | grep -Fq "agent-orchestrator:response-used=$response_id finding=$finding_id " ||
         printf '%s\n' "$existing" | grep -Fq "agent-orchestrator:response-used-content=$response_hash finding=$finding_id "; then
        block_for_human "$pr" "$issue" \
          "The same blocking finding repeated after its coding response content was already reviewed. Please decide how to proceed." \
          "<!-- agent-orchestrator:no-progress=$finding_id -->"
        return 0
      fi
      response_used="$response_used<!-- agent-orchestrator:response-used=$response_id finding=$finding_id feedback=$signature task=$prior_task_id -->"$'\n'
      response_used="$response_used<!-- agent-orchestrator:response-used-content=$response_hash finding=$finding_id feedback=$signature task=$prior_task_id response=$response_id -->"$'\n'
    fi
  done
  if [ -n "$response_used" ]; then
    response_id="${response_id:-}"
  elif printf '%s\n' "$existing" | grep -Fq "agent-orchestrator:feedback=$signature "; then
    block_for_human "$pr" "$issue" \
      "Blocking feedback repeated after a completed coding response without verified new evidence. Please decide how to resolve the finding." \
      "<!-- agent-orchestrator:no-progress=$signature -->"
    return 0
  fi
  if feedback_contradicts_prior "$feedback" "$existing"; then
    block_for_human "$pr" "$issue" \
      "The review feedback appears to reverse or contradict an earlier requested change. Please resolve the conflicting recommendations." \
      "<!-- agent-orchestrator:contradictory-feedback=$signature -->"
    return 0
  fi
  if completed_round_progress "$pr" "$issue" "$sha"; then
    progress_rc=0
  else
    progress_rc=$?
  fi
  if [ "$progress_rc" -eq 1 ]; then
    block_for_human "$pr" "$issue" \
      "A completed coding response round made no verified finding-linked fix or new evidence-based response. Please decide how to resolve the outstanding feedback." \
      "<!-- agent-orchestrator:no-progress-round=$sha -->"
    return 0
  elif [ "$progress_rc" -ne 0 ]; then
    echo "Unable to verify progress for the previous coding response; deferring feedback handoff" >&2
    return 1
  fi
  if ! review_eligible "$pr" "$issue" "$sha"; then return 0; fi
  response_contract=""
  for finding_id in $finding_ids; do
    printf -v response_contract '%s\nFinding: %s\nOutcome: fixed|rebutted\nChanged file: <required for fixed outcomes>\nEvidence: <at least 40 non-whitespace characters naming a test/spec or source file and explaining how it resolves this finding>\n' \
      "$response_contract" "$finding_id"
  done
  prompt="$(printf '%s\n\n%s\n\n%s\n%s' \
    'Address only these explicitly blocking review findings. For each finding, post a separate PR comment containing the exact response marker and Finding ID below, followed by Outcome: fixed or Outcome: rebutted, and a substantive Evidence: line. Evidence must contain at least 40 non-whitespace characters, name a test/spec or source file, and explain how that evidence resolves the specific finding. For fixed outcomes, also include Changed file: <path>; it must appear in the revision comparison and in the Evidence line. Evidence for inline findings must also reference the finding path, but the changed file may differ when the evidence links the implementation to that finding. A rebuttal must identify concrete test, file, or behavior evidence for fresh reviewer evaluation. If you cannot supply this evidence, stop and request a human decision. Run relevant tests and resolve addressed threads. Do not merge.' \
    "<!-- agent-orchestrator:response-feedback=$signature -->" "$response_contract" "$feedback")"
  escaped_feedback="$(escape_external_metadata "$feedback")"
  escaped_summary="$(escape_external_metadata "$summary")"
  for finding_id in $finding_ids; do
    finding_path="$(feedback_path_for_finding "$feedback" "$finding_id" || true)"
    path_base64="$(printf '%s' "$finding_path" | base64 -w0)"
    finding_marker="<!-- agent-orchestrator:finding=$finding_id feedback=$signature path-base64=$path_base64 -->"
    if ! printf '%s\n' "$existing" | grep -Fq "$finding_marker"; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$finding_marker" >/dev/null || return 1
      existing+=$'\n'"$finding_marker"
    fi
  done
  while IFS= read -r target_id; do
    [ -n "$target_id" ] || continue
    target_action="$(feedback_target_action_for_id "$feedback" "$target_id" || printf unknown)"
    recommendation_marker="<!-- agent-orchestrator:recommendation=$target_id action=$target_action feedback=$signature -->"
    if ! printf '%s\n' "$existing" | grep -Fq "$recommendation_marker"; then
      GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$recommendation_marker" >/dev/null || return 1
      existing+=$'\n'"$recommendation_marker"
    fi
  done <<< "$target_ids"
  task_id="$(start_pr_fix_task "$pr" "$issue" "$sha" "$prompt" "$review_id" "$signature")" || {
    rc=$?
    [ "$rc" -eq 3 ] && return 0
    return "$rc"
  }
  {
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$(printf 'Started Copilot task %s for blocking review feedback on %s.\n\n%s\n\n%s\n\n<!-- agent-orchestrator:review-feedback=%s -->\n%s' \
      "$task_id" "$sha" "$escaped_feedback" "$escaped_summary" "$review_id" "$marker $response_used")" >/dev/null
  } || return 1
  set_state "$issue" "$WORKING_LABEL"
}

# The reviewer integration exposes GitHub's review state, so APPROVED is explicit acceptance and CHANGES_REQUESTED
# is actionable only with inline findings explicitly marked blocking or a structured "Blocking findings:" summary.
# COMMENTED and other outcomes are ambiguous and go to a human. No-progress rounds and conflicting recommendations
# pause automation without a round cap.
process_review() {
  local pr="$1" issue="$2" sha="$3" review review_id state body threads findings feedback inline_blockers is_draft result summary summary_state
  local summary_feedback=""
  if ! review_eligible "$pr" "$issue" "$sha"; then return 0; fi
  if review="$(current_copilot_review "$pr" "$sha" "$issue")"; then
    :
  else
    result=$?
    if [ "$result" -eq 2 ]; then
      echo "Unable to fetch current-task Copilot review for $sha; deferring" >&2
      return 1
    fi
    echo "No current-task Copilot review for $sha; leaving review processing unchanged"
    return 0
  fi
  review_id="$(jq -er '.id | tostring' <<< "$review")" || return 1
  if review_already_handled "$issue" "$review_id"; then
    echo "Review $review_id was already processed"
    return 0
  else
    result=$?
    [ "$result" -eq 1 ] || return 1
  fi
  state="$(jq -r '.state // empty' <<< "$review")"
  body="$(jq -r '.body // ""' <<< "$review")"
  summary="$(parse_blocking_summary "$body")" || return 1
  summary_state="${summary%%$'\n'*}"
  summary_feedback="${summary#*$'\n'}"
  [ "$summary_state" != "$summary" ] || summary_feedback=""
  if [ "$summary_state" = absent ] && grep -Fq 'Blocking findings:' <<< "$body"; then
    summary_state=invalid
  fi
  threads="$(review_threads "$pr")" || {
    echo "Unable to fetch review threads; deferring review processing" >&2
    return 1
  }
  findings="$(review_findings "$threads" "$sha" "$review_id")" || return 1
  case "$state" in
    APPROVED)
      inline_blockers="$(review_blocking_findings "$findings")"
      if [ "$summary_state" = valid ] || [ "$summary_state" = invalid ] || [ -n "$inline_blockers" ]; then
        block_for_human "$pr" "$issue" \
          "The Copilot review both approved the PR and declared blocking feedback in its current summary or inline findings. Please resolve the contradictory outcome." \
          "<!-- agent-orchestrator:ambiguous-review=$review_id -->"
        return 0
      fi
      if ! review_eligible "$pr" "$issue" "$sha"; then return 0; fi
      is_draft="$(gh pr view "$pr" --repo "$REPO" --json isDraft --jq '.isDraft')" || return 1
      if [ "$is_draft" = "true" ]; then gh pr ready "$pr" --repo "$REPO" >/dev/null || return 1; fi
      set_state "$issue" "$REVIEW_LABEL" || return 1
      record_review_processed "$review_id" "$issue" || return 1
      return 0 ;;
    CHANGES_REQUESTED)
      inline_blockers="$(review_blocking_findings "$findings")"
      if [ "$summary_state" = invalid ]; then
        block_for_human "$pr" "$issue" \
          "Copilot requested changes but the Blocking findings section is empty, malformed, or contradictory. Please classify the required feedback." \
          "<!-- agent-orchestrator:ambiguous-review=$review_id -->"
        return 0
      fi
      [ "$summary_state" = valid ] || summary_feedback=""
      feedback="$(printf '%s\n%s' "$summary_feedback" "$inline_blockers" | sed '/^[[:space:]]*$/d')"
      if [ -n "$feedback" ]; then
        handoff_blocking_feedback "$pr" "$issue" "$sha" "$review_id" "$feedback" "$body" || return 1
        return 0
      fi
      block_for_human "$pr" "$issue" \
        "Copilot requested changes but did not identify which feedback is blocking. Please clarify the required decision." \
        "<!-- agent-orchestrator:ambiguous-review=$review_id -->"
      return 0 ;;
    *)
      block_for_human "$pr" "$issue" \
        "Copilot review outcome '$state' is not an explicit acceptance or actionable blocking result. Please classify the feedback." \
        "<!-- agent-orchestrator:ambiguous-review=$review_id -->"
      return 0 ;;
  esac
}

handle_ci_failure() {
  # Failed CI is never a review result: hand it back to Copilot (agent:working) or ask a human (agent:blocked).
  local pr="$1" issue="$2" sha="$3" conclusion="$4" run_url="$5" pr_data labels existing marker task_id prompt task_state rc latest_conclusion
  local attempts max_attempts review_id feedback_signature round_task task_comments task_comment next_attempt recovered recovery_rc
  pr_data="$(gh pr view "$pr" --repo "$REPO" --json state,headRefOid)" || return 1
  if ! jq -e --arg sha "$sha" '.state == "OPEN" and .headRefOid == $sha' <<< "$pr_data" >/dev/null; then
    echo "PR #$pr is closed or its head moved; CI result for $sha is outdated"
    return 0
  fi
  latest_conclusion="$(ci_conclusion "$sha")" || return 1
  if [ "$latest_conclusion" != "$conclusion" ]; then
    echo "CI event for $sha concluded $conclusion, but the latest conclusion is $latest_conclusion; ignoring stale event"
    return 0
  fi
  labels="$(issue_labels "$issue")" || return 1
  if issue_open "$issue"; then :; else
    rc=$?
    [ "$rc" -eq 1 ] && {
      echo "Issue #$issue is closed; ignoring CI failure"
      return 0
    }
    return 1
  fi
  if printf '%s\n' "$labels" | grep -Fxq "$BLOCKED_LABEL"; then
    echo "Issue #$issue is already blocked; leaving it for a human"; return 0
  fi
  existing="$(pr_bot_comments "$pr")" || return 1
  marker="<!-- agent-orchestrator:ci-failure-sha=$sha -->"
  if recover_ci_handoff_metadata "$pr" "$issue" "$sha"; then
    recovered=true
    existing="$(pr_bot_comments "$pr")" || return 1
  else
    recovery_rc=$?
    [ "$recovery_rc" -eq 1 ] || return 1
    recovered=false
  fi
  if printf '%s\n' "$existing" | grep -Fq "$marker"; then
    task_state="$(agent_task_state "$issue")" || return 1
    if [ "$task_state" = "completed" ]; then
      block_for_human "$pr" "$issue" \
        "CI still fails for $sha after the completed coding response. Please inspect the failure and decide how to proceed." \
        "<!-- agent-orchestrator:ci-no-progress-sha=$sha -->" || return 1
    else
      if [ "$recovered" = true ]; then set_state "$issue" "$WORKING_LABEL" || return 1; fi
      echo "CI result for $sha already handed off; no duplicate action"
    fi
    return 0
  fi
  task_state="$(agent_task_state "$issue")" || {
    echo "Unable to read the latest Copilot task; not routing CI failure" >&2
    return 1
  }
  case "$task_state" in
    queued|in_progress)
      local deferred_marker="<!-- agent-orchestrator:ci-failure-deferred-sha=$sha -->"
      if ! printf '%s\n' "$existing" | grep -Fq "$deferred_marker"; then
        GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body \
          "CI failure for $sha will be reconciled after the current Copilot task completes. $deferred_marker" >/dev/null || return 1
      fi
      echo "CI failure for $sha deferred until the current coding task completes"
      return 0 ;;
    completed|failed|cancelled) ;;
    *)
      echo "Copilot task has unknown state '$task_state'; not routing CI failure" >&2
      return 1 ;;
  esac
  if [ "$conclusion" != "failure" ] && [ "$conclusion" != "timed_out" ]; then
    GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$(printf 'Agent orchestrator: CI for %s concluded "%s" (%s). This cannot be handled automatically; re-run CI or decide how to proceed.\n\n%s' \
      "$sha" "$conclusion" "$run_url" "$marker")" >/dev/null || return 1
    echo "CI $conclusion for $sha -> human attention"
    set_state "$issue" "$BLOCKED_LABEL" || return 1
    return 0
  fi
  attempts="$(ci_fix_attempt_count "$existing")"
  max_attempts="${MAX_CI_FIX_ATTEMPTS:-3}"
  if [ "$attempts" -ge "$max_attempts" ]; then
    block_for_human "$pr" "$issue" \
      "CI repair has failed $attempts consecutive times without a green run. Please inspect the technical failure before automation continues." \
      "<!-- agent-orchestrator:ci-retry-limit=$attempts -->" || return 1
    return 0
  fi
  task_id="$(agent_task_id "$issue")" || return 1
  task_comments="$(gh issue view "$issue" --repo "$REPO" --json comments)" || return 1
  task_comment="$(jq -cer --arg task "$task_id" \
    '[.comments[] | select(.author.login == "github-actions")
      | select((.body | split("agent-orchestrator:task-id=")[1] // "" | split(" ")[0]) == $task)]
     | last // empty' <<< "$task_comments")" || return 1
  feedback_signature="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*feedback=([A-Za-z0-9]+).*/\1/p')"
  round_task="$(jq -r '.body' <<< "$task_comment" | sed -nE 's/.*round-task=([^ ]*).*/\1/p')"
  if [ -n "$feedback_signature" ] && [ -z "$round_task" ]; then round_task="$task_id"; fi
  next_attempt=$((attempts + 1))
  review_id="ci-$sha-$next_attempt"
  prompt="$(printf 'CI failed for commit %s: %s\nPlease investigate the failing build or tests on this PR branch, fix the cause, run all tests, and push the fix. Do not merge.' \
    "$sha" "$run_url")"
  if [ -n "$feedback_signature" ]; then
    prompt="$prompt

This is a technical CI repair within review feedback round $feedback_signature. Do not treat the repair as resolving or resetting that review feedback; preserve the required finding-linked response evidence."
  fi
  task_id="$(start_pr_fix_task "$pr" "$issue" "$sha" "$prompt" "$review_id" "$feedback_signature" "$round_task")" || {
    rc=$?
    [ "$rc" -eq 3 ] && return 0
    return "$rc"
  }
  GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh pr comment "$pr" --repo "$REPO" --body "$(printf 'Started Copilot cloud agent task %s to fix CI for commit %s: %s\n\n%s\n\n%s' \
    "$task_id" "$sha" "$run_url" "$prompt" "$marker <!-- agent-orchestrator:ci-fix-task-sha=$sha --> <!-- agent-orchestrator:ci-fix-attempt=$next_attempt -->")" >/dev/null || return 1
  echo "Started Copilot task $task_id for CI failure on $sha"
  set_state "$issue" "$WORKING_LABEL" || return 1
}

reconcile_open_copilot_prs() {
  local prs copilot_prs pr number branch sha issue conclusion request_rc rc
  prs="$(gh pr list --repo "$REPO" --state open --limit 100 --json number,headRefName,headRefOid)" || return 1
  copilot_prs="$(jq -c '[.[] | select((.headRefName // "") | startswith("copilot/"))]' <<< "$prs")" || return 1
  while IFS= read -r pr; do
    [ -n "$pr" ] || continue
    number="$(jq -er '.number | tostring' <<< "$pr")" || return 1
    branch="$(jq -er '.headRefName | select(type == "string" and length > 0)' <<< "$pr")" || return 1
    sha="$(jq -er '.headRefOid | select(type == "string" and length > 0)' <<< "$pr")" || return 1
    issue="$(resolve_issue "$number" "$branch")" || return 1
    if [ -z "$issue" ]; then
      echo "No Issue mapping for PR #$number; skipping it"
      continue
    fi
    reconcile_human_feedback_events "$number" "$issue" || return 1
    if process_human_feedback "$number" "$issue"; then
      :
    else
      rc=$?
      [ "$rc" -eq 3 ] || return "$rc"
    fi
    conclusion="$(ci_conclusion "$sha")" || return 1
    case "$conclusion" in
      failure|timed_out|cancelled|startup_failure)
        handle_ci_failure "$number" "$issue" "$sha" "$conclusion" "https://github.com/$REPO/actions" || return 1
        ;;
    esac
    request_rc=0
    request_review_if_eligible "$number" "$issue" "$sha" || request_rc=$?
    if [ "$request_rc" -ne 0 ] && [ "$request_rc" -ne 1 ]; then return "$request_rc"; fi
    process_review "$number" "$issue" "$sha" || return 1
  done < <(jq -c '.[]' <<< "$copilot_prs")
}
