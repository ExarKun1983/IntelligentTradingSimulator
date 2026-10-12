# shellcheck shell=bash
# Shared helpers for .github/workflows/agent-orchestrator.yml.
# Source this file from a job step; do not execute it directly. Every job sources the same file so that
# Issue/PR mapping and state transitions behave identically everywhere.
#
# Required environment: REPO, READY_LABEL, WORKING_LABEL, AI_REVIEW_LABEL, REVIEW_LABEL, BLOCKED_LABEL,
# COPILOT_REVIEWER, MAX_FIX_ROUNDS, REVIEW_WAIT_SECONDS, POLL_SECONDS, and GH_TOKEN for the gh CLI.
# Optional environment:
#   LAUNCH_MATCH_WINDOW_SECONDS  Max delay between an agent launch and its PR creation (default 1800).
#   CI_WORKFLOW_FILE             Workflow file whose result gates human review (default ci.yml).
#   ACTIONS_TOKEN                Token used to read workflow runs (defaults to GH_TOKEN).
#   CI_CONFIRMED_SHA             Head SHA already known to have green CI (set by the workflow_run job).

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
  number="$(gh api "repos/$REPO/issues/$1" --jq 'if .pull_request == null then .number else empty end' 2>/dev/null || true)"
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
    echo "PR body references #$refs, which is not an Issue in $REPO" >&2
  fi
}

# Open Issues that carry any agent state label, one number per line.
agent_issue_numbers() {
  jq -r --arg w "$WORKING_LABEL" --arg a "$AI_REVIEW_LABEL" --arg r "$REVIEW_LABEL" --arg b "$BLOCKED_LABEL" \
    '.[] | select(any(.labels[]?.name; . == $w or . == $a or . == $r or . == $b)) | .number' <<< "$1"
}

issue_comment_bodies() {
  gh issue view "$1" --repo "$REPO" --json comments --jq '.comments[]?.body // empty'
}

issue_events() {
  gh api --paginate "repos/$REPO/issues/$1/events?per_page=100" 2>/dev/null | jq -s 'add // []' 2>/dev/null || true
}

issue_from_mapping_comment() {
  local pr="$1" open_json="$2" candidate comments
  for candidate in $(agent_issue_numbers "$open_json"); do
    comments="$(issue_comment_bodies "$candidate")"
    if printf '%s\n' "$comments" | grep -Eq "agent-orchestrator:pr=${pr}([^0-9]|$)|Mapped automatically to PR #${pr}([^0-9]|$)"; then
      printf '%s' "$candidate"; return 0
    fi
  done
}

# Succeeds when the Issue was (re)launched via agent:ready -> agent:working shortly before the PR was created.
launched_before() {
  local issue="$1" created="$2" events
  events="$(issue_events "$issue")"
  [ -n "$events" ] || return 1
  jq -e --arg r "$READY_LABEL" --arg w "$WORKING_LABEL" --arg created "$created" \
      --argjson window "${LAUNCH_MATCH_WINDOW_SECONDS:-1800}" '
    ([.[] | select(.event == "labeled" and .label.name == $r) | .created_at | fromdateiso8601] | max) as $ready
    | ($created | fromdateiso8601) as $pr
    | $ready != null
      and any(.[]; .event == "labeled" and .label.name == $w and (.created_at | fromdateiso8601) >= $ready)
      and $pr >= $ready and ($pr - $ready) <= $window' <<< "$events" >/dev/null 2>&1
}

# Succeeds when no other PR mapped to the Issue is still open or newer than this PR.
no_other_live_pr() {
  local issue="$1" pr="$2" mapped other state
  mapped="$(issue_comment_bodies "$issue" | grep -oE 'agent-orchestrator:pr=[0-9]+' | sed 's/.*=//' | sort -un || true)"
  for other in $mapped; do
    if [ "$other" = "$pr" ]; then continue; fi
    if [ "$other" -gt "$pr" ]; then return 1; fi
    state="$(gh pr view "$other" --repo "$REPO" --json state --jq '.state' 2>/dev/null || true)"
    if [ "$state" != "CLOSED" ] && [ "$state" != "MERGED" ]; then return 1; fi
  done
}

# Replacement PRs: an Issue in any agent state (including agent:blocked after a closed-unmerged PR) that was
# relaunched shortly before this PR was opened and has no other live PR. The match must be unambiguous.
issue_from_relaunch() {
  local pr="$1" created="$2" open_json="$3" candidate matches=()
  if [ -z "$created" ]; then return 0; fi
  for candidate in $(agent_issue_numbers "$open_json"); do
    if launched_before "$candidate" "$created" && no_other_live_pr "$candidate" "$pr"; then
      matches+=("$candidate")
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
  pr_json="$(gh pr view "$pr" --repo "$REPO" --json body,createdAt)"
  body="$(jq -r '.body // ""' <<< "$pr_json")"
  created="$(jq -r '.createdAt // ""' <<< "$pr_json")"
  issue="$(issue_from_marker "$body")"
  if [ -z "$issue" ]; then issue="$(issue_from_branch "$branch")"; fi
  open_json="$(gh issue list --repo "$REPO" --state open --limit 200 --json number,labels)"
  if [ -z "$issue" ]; then issue="$(issue_from_mapping_comment "$pr" "$open_json")"; fi
  if [ -z "$issue" ]; then issue="$(issue_from_body_reference "$body")"; fi
  if [ -z "$issue" ]; then issue="$(issue_from_relaunch "$pr" "$created" "$open_json")"; fi
  if [ -z "$issue" ]; then
    active="$(jq -r --arg w "$WORKING_LABEL" --arg a "$AI_REVIEW_LABEL" --arg r "$REVIEW_LABEL" \
        '[.[] | select(any(.labels[]?.name; . == $w or . == $a or . == $r)) | .number] | if length == 1 then .[0] else empty end' <<< "$open_json")"
    if [ -n "$active" ]; then echo "Mapped PR #$pr to the only active Issue #$active" >&2; issue="$active"; fi
  fi
  if [ -n "$issue" ]; then
    if ! printf '%s\n' "$body" | grep -Eq "agent-orchestrator:issue=${issue}([^0-9]|$)"; then
      gh pr edit "$pr" --repo "$REPO" --body "$(printf '%s\n\n<!-- agent-orchestrator:issue=%s -->\nCloses #%s\n' "$body" "$issue" "$issue")" >/dev/null
    fi
    comments="$(issue_comment_bodies "$issue")"
    if ! printf '%s\n' "$comments" | grep -Eq "agent-orchestrator:pr=${pr}([^0-9]|$)"; then
      gh issue comment "$issue" --repo "$REPO" --body "$(printf '<!-- agent-orchestrator:pr=%s -->\nMapped automatically to PR #%s.' "$pr" "$pr")" >/dev/null
    fi
  fi
  printf '%s' "$issue"
}

issue_labels() {
  gh issue view "$1" --repo "$REPO" --json labels --jq '.labels[]?.name // empty'
}

set_state() {
  local issue="$1" target="$2" labels label
  labels="$(issue_labels "$issue")"
  for label in "$READY_LABEL" "$WORKING_LABEL" "$AI_REVIEW_LABEL" "$REVIEW_LABEL" "$BLOCKED_LABEL"; do
    if [ "$label" != "$target" ] && printf '%s\n' "$labels" | grep -Fxq "$label"; then
      gh issue edit "$issue" --repo "$REPO" --remove-label "$label" >/dev/null
    fi
  done
  if [ -n "$target" ] && ! printf '%s\n' "$labels" | grep -Fxq "$target"; then
    gh issue edit "$issue" --repo "$REPO" --add-label "$target" >/dev/null
  fi
}

# Succeeds when the Issue moved on after the PR closed: its latest mapping points at a different PR, or it was
# relaunched (agent:ready) after the PR closed. A late close event must then not re-block the Issue.
issue_superseded() {
  local issue="$1" pr="$2" closed_at="$3" latest events
  latest="$(issue_comment_bodies "$issue" | grep -oE 'agent-orchestrator:pr=[0-9]+' | sed 's/.*=//' | tail -1 || true)"
  if [ -n "$latest" ] && [ "$latest" != "$pr" ]; then return 0; fi
  if [ -z "$closed_at" ]; then return 1; fi
  events="$(issue_events "$issue")"
  [ -n "$events" ] || return 1
  jq -e --arg r "$READY_LABEL" --arg closed "$closed_at" \
    'any(.[]; .event == "labeled" and .label.name == $r and (.created_at | fromdateiso8601) >= ($closed | fromdateiso8601))' \
    <<< "$events" >/dev/null 2>&1
}

copilot_reviewed() {
  local reviews
  reviews="$(gh api -H 'Accept: application/vnd.github+json' "repos/$REPO/pulls/$1/reviews")"
  jq -e --arg sha "$2" 'any(.[]?; (.user.login | startswith("copilot-pull-request-reviewer")) and .commit_id == $sha)' <<< "$reviews" >/dev/null
}

request_copilot_review() {
  local pr="$1" sha="$2" requested
  if copilot_reviewed "$pr" "$sha"; then echo "Copilot already reviewed $sha"; return 0; fi
  requested="$(gh api -H 'Accept: application/vnd.github+json' "repos/$REPO/pulls/$pr/requested_reviewers")"
  if jq -e 'any(.users[]?; .login | startswith("copilot-pull-request-reviewer"))' <<< "$requested" >/dev/null; then
    echo "Copilot review already pending"; return 0
  fi
  gh api --silent --method POST -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/pulls/$pr/requested_reviewers" -f "reviewers[]=$COPILOT_REVIEWER"
  echo "Copilot review requested for PR #$pr"
}

wait_for_review() {
  # 0 = review for sha exists, 1 = timeout, 2 = PR head moved on (newer commit will be handled by its own CI run)
  local pr="$1" sha="$2" deadline head
  deadline=$(( $(date +%s) + REVIEW_WAIT_SECONDS ))
  while :; do
    if copilot_reviewed "$pr" "$sha"; then echo "Copilot review for $sha is available"; return 0; fi
    head="$(gh pr view "$pr" --repo "$REPO" --json headRefOid --jq '.headRefOid')"
    if [ "$head" != "$sha" ]; then echo "PR head moved to $head; leaving it to the next CI run"; return 2; fi
    if [ "$(date +%s)" -ge "$deadline" ]; then echo "Copilot review did not arrive in time"; return 1; fi
    sleep "$POLL_SECONDS"
  done
}

# Succeeds only when the latest CI run for the SHA concluded successfully.
ci_green() {
  local sha="$1" conclusion
  if [ -n "${CI_CONFIRMED_SHA:-}" ] && [ "$CI_CONFIRMED_SHA" = "$sha" ]; then return 0; fi
  conclusion="$(GH_TOKEN="${ACTIONS_TOKEN:-${GH_TOKEN:-}}" gh api -H 'Accept: application/vnd.github+json' \
    "repos/$REPO/actions/workflows/${CI_WORKFLOW_FILE:-ci.yml}/runs?head_sha=$sha&per_page=20" \
    --jq '[.workflow_runs[]?] | sort_by(.created_at) | last | .conclusion // empty' 2>/dev/null || true)"
  [ "$conclusion" = "success" ]
}

process_review() {
  # Decide after Copilot reviewed the current head: hand findings to the coding agent, or promote to human review.
  local pr="$1" issue="$2" sha="$3" threads open_threads count findings marker existing rounds
  # shellcheck disable=SC2016 # GraphQL variables, not shell expansions.
  threads="$(gh api graphql \
    -f query='query($owner:String!,$repo:String!,$number:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$number){reviewThreads(first:100){nodes{isResolved comments(first:20){nodes{body path line author{login}}}}}}}}' \
    -F owner="${REPO%%/*}" -F repo="${REPO#*/}" -F number="$pr")"
  open_threads='[.data.repository.pullRequest.reviewThreads.nodes[]? | select(.isResolved == false) | select(any(.comments.nodes[]?; .author.login | startswith("copilot-pull-request-reviewer")))]'
  count="$(jq "$open_threads | length" <<< "$threads")"
  if [ "$count" -eq 0 ]; then
    if ! ci_green "$sha"; then
      echo "No unresolved Copilot findings, but CI for $sha is not green; not promoting to human review"
      return 0
    fi
    echo "No unresolved Copilot findings; CI is green -> human review"
    gh pr ready "$pr" --repo "$REPO" >/dev/null 2>&1 || true
    set_state "$issue" "$REVIEW_LABEL"
    return 0
  fi
  existing="$(gh pr view "$pr" --repo "$REPO" --json comments --jq '.comments[]?.body // empty')"
  marker="<!-- agent-orchestrator:handoff-sha=$sha -->"
  if printf '%s\n' "$existing" | grep -Fq "$marker"; then
    echo "Findings for $sha already handed off"; set_state "$issue" "$WORKING_LABEL"; return 0
  fi
  rounds="$(printf '%s\n' "$existing" | grep -c 'agent-orchestrator:handoff-sha=' || true)"
  if [ "$rounds" -ge "$MAX_FIX_ROUNDS" ]; then
    gh pr comment "$pr" --repo "$REPO" --body "Agent orchestrator stopped after $rounds automatic fix rounds. $count Copilot finding(s) remain unresolved and need a human decision." >/dev/null
    set_state "$issue" "$BLOCKED_LABEL"
    return 0
  fi
  findings="$(jq -r "$open_threads | map(.comments.nodes[0] | \"- \(.path // \"general\"):\(.line // \"\") \(.body)\") | join(\"\n\")" <<< "$threads")"
  gh pr comment "$pr" --repo "$REPO" --body "$(printf '%s\n\n%s\n\n%s' \
    '@copilot Please address all unresolved Copilot code review findings below on this PR branch. Update tests and documentation as needed, run all tests, push the fix, and resolve the addressed threads. Do not merge.' \
    "$findings" "$marker")" >/dev/null
  echo "Handed off $count finding(s) to Copilot (round $((rounds + 1)) of $MAX_FIX_ROUNDS)"
  set_state "$issue" "$WORKING_LABEL"
}

handle_ci_failure() {
  # Failed CI is never a review result: hand it back to Copilot (agent:working) or ask a human (agent:blocked).
  local pr="$1" issue="$2" sha="$3" conclusion="$4" run_url="$5" head labels existing marker rounds
  head="$(gh pr view "$pr" --repo "$REPO" --json headRefOid --jq '.headRefOid')"
  if [ "$head" != "$sha" ]; then echo "PR head moved to $head; CI result for $sha is outdated"; return 0; fi
  labels="$(issue_labels "$issue")"
  if printf '%s\n' "$labels" | grep -Fxq "$BLOCKED_LABEL"; then
    echo "Issue #$issue is already blocked; leaving it for a human"; return 0
  fi
  existing="$(gh pr view "$pr" --repo "$REPO" --json comments --jq '.comments[]?.body // empty')"
  marker="<!-- agent-orchestrator:ci-failure-sha=$sha -->"
  if printf '%s\n' "$existing" | grep -Fq "$marker"; then
    echo "CI result for $sha already handed off"; set_state "$issue" "$WORKING_LABEL"; return 0
  fi
  if [ "$conclusion" != "failure" ] && [ "$conclusion" != "timed_out" ]; then
    gh pr comment "$pr" --repo "$REPO" --body "$(printf 'Agent orchestrator: CI for %s concluded "%s" (%s). This cannot be handled automatically; re-run CI or decide how to proceed.\n\n%s' \
      "$sha" "$conclusion" "$run_url" "$marker")" >/dev/null
    echo "CI $conclusion for $sha -> human attention"
    set_state "$issue" "$BLOCKED_LABEL"
    return 0
  fi
  rounds="$(printf '%s\n' "$existing" | grep -c 'agent-orchestrator:ci-failure-sha=' || true)"
  if [ "$rounds" -ge "$MAX_FIX_ROUNDS" ]; then
    gh pr comment "$pr" --repo "$REPO" --body "$(printf 'Agent orchestrator stopped after %s automatic CI fix rounds. CI still fails for %s (%s) and needs a human decision.\n\n%s' \
      "$rounds" "$sha" "$run_url" "$marker")" >/dev/null
    set_state "$issue" "$BLOCKED_LABEL"
    return 0
  fi
  gh pr comment "$pr" --repo "$REPO" --body "$(printf '@copilot CI failed for commit %s: %s\nPlease investigate the failing build or tests on this PR branch, fix the cause, run all tests, and push the fix. Do not merge.\n\n%s' \
    "$sha" "$run_url" "$marker")" >/dev/null
  echo "Handed off CI failure for $sha to Copilot (round $((rounds + 1)) of $MAX_FIX_ROUNDS)"
  set_state "$issue" "$WORKING_LABEL"
}
