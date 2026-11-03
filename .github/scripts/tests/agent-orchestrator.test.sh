#!/usr/bin/env bash
# Offline tests for .github/scripts/agent-orchestrator.sh. The gh CLI is replaced by a file-backed stub.
# Run: bash .github/scripts/tests/agent-orchestrator.test.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=SCRIPTDIR/../agent-orchestrator.sh
source "$SCRIPT_DIR/../agent-orchestrator.sh"

export REPO="owner/repo" READY_LABEL="agent:ready" WORKING_LABEL="agent:working" AI_REVIEW_LABEL="agent:ai-review" \
  REVIEW_LABEL="agent:review" BLOCKED_LABEL="agent:blocked" COPILOT_REVIEWER="copilot-pull-request-reviewer[bot]" \
  REVIEW_TIMEOUT_SECONDS=999999999 LAUNCH_MATCH_WINDOW_SECONDS=1800 CI_WORKFLOW_FILE=ci.yml
unset ACTIONS_TOKEN || true

STATE=""
failures=0
passes=0

# --- gh stub -----------------------------------------------------------------------------------------------
# State lives in files because the helpers run inside command substitutions (subshells).
#   pr-N.json            {"body","createdAt","state","headRefOid"}
#   pr-N.comments        JSON array of comment bodies
#   issue-N.labels       one label per line
#   issue-N.comments     JSON array of comment bodies
#   issue-N.kind         "issue" or "pr" (for repos/R/issues/N lookups)
#   issue-N.events       JSON array of issue events
#   threads.json, ci-runs.json, reviews.json, task-ID.json
#   calls.log            mutating calls
gh() {
  local jq_filter="" args=()
  while [ "$#" -gt 0 ]; do
    case "$1" in
      --jq) jq_filter="$2"; shift 2 ;;
      *) args+=("$1"); shift ;;
    esac
  done
  set -- "${args[@]}"
  local out
  out="$(gh_dispatch "$@")" || return 1
  if [ -n "$jq_filter" ]; then jq -r "$jq_filter" <<< "$out"; else printf '%s\n' "$out"; fi
}

json_comments() {
  jq '{comments: map(if type == "object" then . else {body: ., author: {login: "github-actions[bot]"}, createdAt: "2026-10-12T13:30:00Z"} end)}' "$1"
}

append_json() { local f="$1" v="$2"; jq --arg v "$v" '. + [$v]' "$f" > "$f.tmp" && mv "$f.tmp" "$f"; }

gh_dispatch() {
  local group="$1" verb="${2:-}" n="${3:-}" all="$*"
  case "$group $verb" in
    "pr view")
      local fields
      fields="$(printf '%s\n' "$@" | grep -A1 -- '--json' | tail -1)"
      [ -f "$STATE/pr-$n.json" ] || { echo "no PR $n" >&2; return 1; }
      if [ "$fields" = "comments" ]; then json_comments "$STATE/pr-$n.comments"; else cat "$STATE/pr-$n.json"; fi ;;
    "pr edit")
      local body="${all##*--body }"
      jq --arg b "$body" '.body = $b' "$STATE/pr-$n.json" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-$n.json"
      echo "pr edit $n" >> "$STATE/calls.log" ;;
    "pr comment")
      local body="${all##*--body }"
      append_json "$STATE/pr-$n.comments" "$body"
      echo "pr comment $n" >> "$STATE/calls.log" ;;
    "pr ready") echo "pr ready $n" >> "$STATE/calls.log" ;;
    "issue list")
      local i nums="[]"
      for i in "$STATE"/issue-*.labels; do
        [ -e "$i" ] || continue
        i="${i##*/issue-}"; i="${i%.labels}"
        nums="$(jq --argjson n "$i" --arg labels "$(cat "$STATE/issue-$i.labels")" \
          '. + [{number: $n, labels: ($labels | split("\n") | map(select(. != "")) | map({name: .}))}]' <<< "$nums")"
      done
      printf '%s\n' "$nums" ;;
    "issue view")
      local fields
      fields="$(printf '%s\n' "$@" | grep -A1 -- '--json' | tail -1)"
      if [ "$fields" = "comments" ]; then json_comments "$STATE/issue-$n.comments"
      else jq -R -s '{labels: (split("\n") | map(select(. != "")) | map({name: .}))}' "$STATE/issue-$n.labels"; fi ;;
    "issue comment")
      append_json "$STATE/issue-$n.comments" "${all##*--body }"
      echo "issue comment $n" >> "$STATE/calls.log" ;;
    "issue edit")
      local label
      if [[ "$*" == *--remove-label* ]]; then
        label="${all##*--remove-label }"
        grep -Fxv "$label" "$STATE/issue-$n.labels" > "$STATE/tmp" || true; mv "$STATE/tmp" "$STATE/issue-$n.labels"
      fi
      if [[ "$*" == *--add-label* ]]; then
        label="${all##*--add-label }"
        printf '%s\n' "$label" >> "$STATE/issue-$n.labels"
      fi
      echo "issue edit $n ${*:4}" >> "$STATE/calls.log" ;;
    api*)
      local path
      path="$(printf '%s\n' "$@" | grep -E '^(agents/|repos/|graphql$)' | head -1)"
      case "$path" in
        graphql) cat "$STATE/threads.json" ;;
        agents/repos/*/tasks)
          local next_id="task-$(($(grep -c '^task create' "$STATE/calls.log" || true) + 1))"
          local payload=""
          if [[ "$*" == *"--input -"* ]]; then payload="$(cat)"; fi
          echo "task create $next_id $payload" >> "$STATE/calls.log"
          echo '{"id":"'"$next_id"'","state":"queued"}' > "$STATE/$next_id.json"
          echo '{"id":"'"$next_id"'","state":"queued"}' ;;
        agents/repos/*/tasks/*)
          local task_id="${path##*/}"
          [ -f "$STATE/$task_id.json" ] || { echo "missing task $task_id" >&2; return 1; }
          cat "$STATE/$task_id.json" ;;
        repos/*/issues/*/events*) n="${path#repos/*/issues/}"; n="${n%%/*}"; cat "$STATE/issue-$n.events" 2>/dev/null || echo '[]' ;;
        repos/*/issues/*)
          n="${path##*/}"
          case "$(cat "$STATE/issue-$n.kind" 2>/dev/null || echo missing)" in
            issue) printf '{"number":%s}\n' "$n" ;;
            pr) printf '{"number":%s,"pull_request":{}}\n' "$n" ;;
            *) echo "HTTP 404" >&2; return 1 ;;
          esac ;;
        repos/*/actions/workflows/*) cat "$STATE/ci-runs.json" 2>/dev/null || echo '{"workflow_runs":[]}' ;;
        repos/*/reviews|repos/*/reviews\?*)
          if [ -f "$STATE/reviews-api-failure" ]; then echo "simulated reviews API failure" >&2; return 1; fi
          if [ -f "$STATE/reviews-invalid" ]; then echo '{invalid json'; else cat "$STATE/reviews.json" 2>/dev/null || echo '[]'; fi ;;
        repos/*/requested_reviewers)
          if [[ "$*" == *POST* ]]; then
            echo "request review" >> "$STATE/calls.log"
            echo '{"users":[{"login":"copilot-pull-request-reviewer[bot]"}]}' > "$STATE/requested-reviewers.json"
          else
            cat "$STATE/requested-reviewers.json" 2>/dev/null || echo '{"users":[]}'
          fi ;;
        *) echo "unexpected gh api $*" >&2; return 1 ;;
      esac ;;
    *) echo "unexpected gh $*" >&2; return 1 ;;
  esac
}

# --- fixtures ----------------------------------------------------------------------------------------------
reset_state() {
  [ -z "$STATE" ] || rm -rf "$STATE"
  STATE="$(mktemp -d)"
  : > "$STATE/calls.log"
}

add_pr() { # number body createdAt [head] [branch]
  jq -n --arg b "$2" --arg c "$3" --arg h "${4:-sha-$1}" --arg branch "${5:-copilot/issue-39-test}" \
    '{body: $b, createdAt: $c, state: "OPEN", isDraft: true, headRefOid: $h, headRefName: $branch, baseRefName: "master"}' > "$STATE/pr-$1.json"
  echo '[]' > "$STATE/pr-$1.comments"
  echo pr > "$STATE/issue-$1.kind"
}

close_pr() { jq '.state = "CLOSED"' "$STATE/pr-$1.json" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-$1.json"; }

add_issue() { # number [labels...]
  local n="$1"; shift
  : > "$STATE/issue-$n.labels"
  for l in "$@"; do printf '%s\n' "$l" >> "$STATE/issue-$n.labels"; done
  echo '[]' > "$STATE/issue-$n.comments"
  echo issue > "$STATE/issue-$n.kind"
  echo '[]' > "$STATE/issue-$n.events"
}

add_task() {
  jq -n --arg id "$1" --arg state "$2" --arg updated "${3:-2026-10-12T13:15:00Z}" \
    '{id: $id, state: $state, updated_at: $updated}' > "$STATE/$1.json"
}

add_closed_issue() { echo issue > "$STATE/issue-$1.kind"; }

issue_comment_fixture() { append_json "$STATE/issue-$1.comments" "$2"; }

label_event() { # issue label created_at
  jq --arg l "$2" --arg t "$3" '. + [{event: "labeled", label: {name: $l}, created_at: $t}]' "$STATE/issue-$1.events" > "$STATE/tmp" \
    && mv "$STATE/tmp" "$STATE/issue-$1.events"
}

labels_of() { sort "$STATE/issue-$1.labels" | paste -sd, -; }

# --- assertions --------------------------------------------------------------------------------------------
check() { # name expected actual
  if [ "$2" = "$3" ]; then passes=$((passes + 1)); echo "ok   - $1"
  else failures=$((failures + 1)); echo "FAIL - $1: expected [$2], got [$3]"; fi
}

check_contains() { # name needle haystack
  if [[ "$3" == *"$2"* ]]; then passes=$((passes + 1)); echo "ok   - $1"
  else failures=$((failures + 1)); echo "FAIL - $1: [$2] not found in [$3]"; fi
}

calls_matching() { grep -c -- "$1" "$STATE/calls.log" || true; }

# --- task prompt -------------------------------------------------------------------------------------------
prompt="$(build_task_prompt 39 "Add exit reason tracking" "Issue body" master)"
check_contains "prompt keeps the orchestrator task wording" "Implement GitHub Issue #39: Add exit reason tracking" "$prompt"
check_contains "prompt requires the machine-readable marker" "<!-- agent-orchestrator:issue=39 -->" "$prompt"
check_contains "prompt requires the closing reference" "Closes #39" "$prompt"
check "marker in prompt maps back to the Issue" "39" "$(issue_from_marker "$prompt")"

# --- body references ---------------------------------------------------------------------------------------
reset_state
add_issue 39; add_issue 40; add_issue 12
add_pr 141 "x" "2026-10-12T10:00:00Z"
check "task wording maps" "39" "$(issue_from_body_reference "Implement GitHub Issue #39: Add exit reasons" 2>/dev/null)"
check "Closes maps" "39" "$(issue_from_body_reference $'Summary\n\nCloses #39' 2>/dev/null)"
check "Fixes maps" "40" "$(issue_from_body_reference "fixes #40." 2>/dev/null)"
check "Resolves: maps" "40" "$(issue_from_body_reference "Resolves: #40" 2>/dev/null)"
check "incidental mention ignored" "" "$(issue_from_body_reference "Follow-up to #12 and see #40" 2>/dev/null)"
check "closing reference wins over incidental mention" "39" "$(issue_from_body_reference $'Builds on #12.\nCloses #39' 2>/dev/null)"
check "same Issue referenced twice maps" "39" "$(issue_from_body_reference $'Implement GitHub Issue #39\nCloses #39' 2>/dev/null)"
check "several Issues are ambiguous" "" "$(issue_from_body_reference "Closes #39, fixes #40" 2>/dev/null)"
check "pull request numbers are rejected" "" "$(issue_from_body_reference "Closes #141" 2>/dev/null)"
check "unknown numbers are rejected" "" "$(issue_from_body_reference "Closes #999" 2>/dev/null)"
check "keyword inside a word ignored" "" "$(issue_from_body_reference "Unfixes #39" 2>/dev/null)"
check "cross-repository reference ignored" "" "$(issue_from_body_reference "Closes other/repo#39" 2>/dev/null)"

# --- resolve_issue precedence ------------------------------------------------------------------------------
reset_state
add_issue 39 "agent:working"; add_issue 40 "agent:working"
add_pr 150 $'Closes #40\n<!-- agent-orchestrator:issue=39 -->' "2026-10-12T10:00:00Z"
check "marker wins over body reference" "39" "$(resolve_issue 150 copilot/issue-40-x 2>/dev/null)"

reset_state
add_issue 39; add_issue 40
add_pr 151 "Closes #40" "2026-10-12T10:00:00Z"
check "branch wins over body reference" "39" "$(resolve_issue 151 copilot/issue-39-x 2>/dev/null)"
check_contains "mapping persisted as marker in PR body" "<!-- agent-orchestrator:issue=39 -->" "$(jq -r .body "$STATE/pr-151.json")"
check_contains "mapping comment posted on the Issue" "agent-orchestrator:pr=151" "$(jq -r '.[]' "$STATE/issue-39.comments")"

reset_state
add_issue 39; add_issue 40 "agent:working"; add_issue 41 "agent:working"
add_pr 152 "Implement GitHub Issue #39: Track exits" "2026-10-12T10:00:00Z"
check "body reference maps without branch number or single active Issue" "39" "$(resolve_issue 152 copilot/featuretrack-exit-reason 2>/dev/null)"

reset_state
add_issue 39 "agent:working"; add_issue 40 "agent:working"
add_pr 153 "Unrelated text mentioning #39" "2026-10-12T10:00:00Z"
check "no clear mapping stays unmapped" "" "$(resolve_issue 153 copilot/some-feature 2>/dev/null)"
check "nothing persisted when unmapped" "0" "$(calls_matching .)"

reset_state
add_issue 39 "agent:working"
add_pr 154 "" "2026-10-12T10:00:00Z"
check "single active Issue fallback preserved" "39" "$(resolve_issue 154 copilot/some-feature 2>/dev/null)"

# --- observed #39 scenario: PR #144 closed unmerged, replacement #145 without marker -------------------------
setup_issue_39() {
  reset_state
  add_issue 39 "agent:blocked"
  add_issue 50
  label_event 39 "agent:ready" "2026-10-12T11:41:51Z"
  label_event 39 "agent:working" "2026-10-12T11:41:55Z"
  label_event 39 "agent:blocked" "2026-10-12T12:45:45Z"
  label_event 39 "agent:ready" "2026-10-12T12:46:06Z"
  label_event 39 "agent:working" "2026-10-12T12:46:16Z"
  label_event 39 "agent:blocked" "2026-10-12T12:46:25Z"
  issue_comment_fixture 39 $'<!-- agent-orchestrator:pr=144 -->\nMapped automatically to PR #144.'
  add_pr 144 $'Track exits\n\n<!-- agent-orchestrator:issue=39 -->\nCloses #39' "2026-10-12T11:42:02Z"
  close_pr 144
  add_pr 145 "Record why positions were sold so trade history supports exit analytics." "2026-10-12T12:46:22Z"
}

setup_issue_39
check "replacement PR maps to the relaunched blocked Issue" "39" "$(resolve_issue 145 copilot/featuretrack-exit-reason 2>/dev/null)"
check_contains "replacement PR receives the explicit marker" "<!-- agent-orchestrator:issue=39 -->" "$(jq -r .body "$STATE/pr-145.json")"
check_contains "replacement mapping recorded on the Issue" "agent-orchestrator:pr=145" "$(jq -r '.[]' "$STATE/issue-39.comments")"
check "resolution is stable once the marker exists" "39" "$(resolve_issue 145 copilot/featuretrack-exit-reason 2>/dev/null)"
check "marker and mapping comment are written once" "1 1" "$(calls_matching 'pr edit 145') $(calls_matching 'issue comment 39')"

setup_issue_39
echo '[]' > "$STATE/issue-39.events"
label_event 39 "agent:ready" "2026-10-12T12:46:06Z"
label_event 39 "agent:working" "2026-10-12T12:46:23Z"
check "working event after PR creation is not a replacement match" "" "$(resolve_issue 145 copilot/featuretrack-exit-reason 2>/dev/null)"

setup_issue_39
jq '.createdAt = "2026-10-12T14:00:00Z"' "$STATE/pr-145.json" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-145.json"
check "PR opened long after the relaunch is not guessed" "" "$(resolve_issue 145 copilot/featuretrack-exit-reason 2>/dev/null)"

setup_issue_39
jq '.state = "OPEN"' "$STATE/pr-144.json" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-144.json"
check "Issue with another open PR is not a replacement target" "" "$(resolve_issue 145 copilot/featuretrack-exit-reason 2>/dev/null)"

setup_issue_39
echo '[]' > "$STATE/issue-39.events"
label_event 39 "agent:ready" "2026-10-12T12:46:06Z"
label_event 39 "agent:blocked" "2026-10-12T12:46:08Z"
check "ready label without an agent launch is not a replacement target" "" "$(resolve_issue 145 copilot/featuretrack-exit-reason 2>/dev/null)"

setup_issue_39
add_issue 60 "agent:blocked"
label_event 60 "agent:ready" "2026-10-12T12:46:00Z"
label_event 60 "agent:working" "2026-10-12T12:46:10Z"
check "two relaunched Issues are ambiguous" "" "$(resolve_issue 145 copilot/featuretrack-exit-reason 2>/dev/null)"

# --- terminal-state: late close of the replaced PR must not re-block a relaunched Issue ---------------------
setup_issue_39
if issue_superseded 39 144 "2026-10-12T12:45:39Z"; then r=yes; else r=no; fi
check "closed PR superseded by relaunch" "yes" "$r"
if issue_superseded 39 144 "2026-10-12T12:50:00Z"; then r=yes; else r=no; fi
check "closed PR without later relaunch still blocks" "no" "$r"
issue_comment_fixture 39 $'<!-- agent-orchestrator:pr=145 -->\nMapped automatically to PR #145.'
if issue_superseded 39 144 "2026-10-12T12:50:00Z"; then r=yes; else r=no; fi
check "closed PR superseded by newer mapping" "yes" "$r"

# --- start-agent relaunch clears stale blocked state -------------------------------------------------------
reset_state
add_issue 39 "agent:blocked" "agent:ready"
set_state 39 "$WORKING_LABEL"
check "relaunch leaves only agent:working" "agent:working" "$(labels_of 39)"

# --- CI failure routing and lifecycle coalescing ------------------------------------------------------------
setup_ci() {
  reset_state
  add_issue 39 "agent:working"
  add_pr 145 $'<!-- agent-orchestrator:issue=39 -->' "2026-10-12T12:46:22Z" "abc123"
  add_task task-initial completed
  record_agent_task 39 task-initial
  echo '{"workflow_runs":[{"created_at":"2026-10-12T13:10:00Z","conclusion":"failure"}]}' > "$STATE/ci-runs.json"
}

setup_review() {
  setup_ci
  echo '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[]}}}}}' > "$STATE/threads.json"
  echo '{"workflow_runs":[{"created_at":"2026-10-12T13:10:00Z","conclusion":"success"}]}' > "$STATE/ci-runs.json"
  echo '[{"id":1,"user":{"login":"copilot-pull-request-reviewer[bot]"},"commit_id":"abc123","submitted_at":"2026-10-12T13:20:00Z","state":"APPROVED","body":"Approved."}]' > "$STATE/reviews.json"
}

setup_ci
handle_ci_failure 145 39 abc123 failure https://ci/run/1 >/dev/null
check "failed CI moves Issue to working" "agent:working" "$(labels_of 39)"
check_contains "failed CI starts a tracked Copilot task" "Started Copilot cloud agent task" "$(jq -r '.[]' "$STATE/pr-145.comments")"
check_contains "fix task targets the existing PR branch" '"head_ref": "copilot/issue-39-test"' "$(cat "$STATE/calls.log")"
check "duplicate failed CI does not launch a second task" "1" "$(grep -c '^task create' "$STATE/calls.log")"

setup_ci
echo '{"workflow_runs":[{"created_at":"2026-10-12T13:10:00Z","conclusion":"cancelled"}]}' > "$STATE/ci-runs.json"
handle_ci_failure 145 39 abc123 cancelled https://ci/run/2 >/dev/null
check "cancelled CI blocks for human attention" "agent:blocked" "$(labels_of 39)"
check_contains "cancelled CI explains why" 'concluded "cancelled"' "$(jq -r '.[]' "$STATE/pr-145.comments")"

setup_ci
handle_ci_failure 145 39 abc123 failure https://ci/run/3 >/dev/null
add_task task-1 completed "2026-10-12T13:25:00Z"
handle_ci_failure 145 39 abc123 failure https://ci/run/3 >/dev/null
check "failed CI after a completed fix escalates as no progress" "agent:blocked" "$(labels_of 39)"
check_contains "CI no-progress comment explains escalation" "still fails for abc123" "$(jq -r '.[]' "$STATE/pr-145.comments")"

setup_ci
for i in 1 2 3 4; do
  sha="sha$i"
  jq --arg sha "$sha" '.headRefOid = $sha' "$STATE/pr-145.json" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-145.json"
  handle_ci_failure 145 39 "$sha" failure "https://ci/run/$i" >/dev/null
  add_task "task-$i" completed "2026-10-12T13:2${i}:00Z"
done
check "CI fix flow has no fixed three-round cap" "4" "$(grep -c '^task create' "$STATE/calls.log")"

setup_ci
echo '{"workflow_runs":[{"created_at":"2026-10-12T13:20:00Z","conclusion":"success"}]}' > "$STATE/ci-runs.json"
handle_ci_failure 145 39 abc123 failure https://ci/stale-same-sha >/dev/null
check "delayed failed CI for a now-green SHA is ignored" "0 0" "$(grep -c '^task create' "$STATE/calls.log" || true) $(calls_matching 'pr comment')"

# --- review request waits for both lifecycle signals without polling ----------------------------------------
setup_review
add_task task-initial in_progress
echo '[]' > "$STATE/reviews.json"
if request_review_if_eligible 145 39 abc123 >/dev/null; then r=yes; else r=no; fi
check "successful intermediate CI does not request review during coding" "no" "$r"
check "intermediate CI causes no request or comment" "0 0" "$(calls_matching 'request review') $(calls_matching 'pr comment 145')"
add_task task-initial completed
request_review_if_eligible 145 39 abc123 >/dev/null
check "completion after CI is reconciled on the next event" "1" "$(calls_matching 'request review')"
request_review_if_eligible 145 39 abc123 >/dev/null
check "unchanged reconciliation does not duplicate review requests" "1" "$(calls_matching 'request review')"
check "unchanged reconciliation does not duplicate request comments" "1" "$(calls_matching 'pr comment 145')"

setup_review
add_task task-initial in_progress
echo '{"workflow_runs":[{"created_at":"2026-10-12T13:00:00Z","conclusion":"failure"}]}' > "$STATE/ci-runs.json"
echo '[]' > "$STATE/reviews.json"
if request_review_if_eligible 145 39 abc123 >/dev/null; then r=yes; else r=no; fi
check "manual reconciliation requires green CI" "no" "$r"
check "manual ineligible reconciliation has zero side effects" "0 0" "$(calls_matching 'request review') $(calls_matching 'pr comment 145')"

setup_review
echo '[]' > "$STATE/reviews.json"
request_review_if_eligible 145 39 abc123 >/dev/null
check "review event backup path uses same eligible gate" "1" "$(calls_matching 'request review')"

# --- task completion, current head, and review classification ------------------------------------------------
setup_review
task_id="$(start_agent_task "Add a feature" master "" true)"
record_agent_task 39 "$task_id"
add_task "$task_id" completed
check_contains "initial task ID is recorded for completion checks" "agent-orchestrator:task-id=task-1" "$(jq -r '.[]' "$STATE/issue-39.comments")"
check "completed task state is read from the Agent Tasks API" "completed" "$(agent_task_state 39)"
check_contains "initial API task requests a PR" '"create_pull_request": true' "$(cat "$STATE/calls.log")"

setup_review
echo '[]' > "$STATE/issue-39.comments"
if review_eligible 145 39 abc123 >/dev/null; then r=yes; else r=no; fi
check "missing task completion metadata fails closed" "no" "$r"

setup_review
echo '[]' > "$STATE/issue-39.comments"
jq '. + [{body: "<!-- agent-orchestrator:task-id=attacker-task -->", author: {login: "attacker"}}]' \
  "$STATE/issue-39.comments" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/issue-39.comments"
if review_eligible 145 39 abc123 >/dev/null; then r=yes; else r=no; fi
check "user-authored task markers cannot satisfy completion" "no" "$r"

setup_review
jq '.headRefOid = "newsha"' "$STATE/pr-145.json" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-145.json"
process_review 145 39 abc123 >/dev/null
check "stale review cannot promote a newer head" "agent:working 0" "$(labels_of 39) $(calls_matching 'pr ready')"

setup_review
close_pr 145
process_review 145 39 abc123 >/dev/null
check "closed PR review is ignored" "agent:working 0" "$(labels_of 39) $(calls_matching 'pr ready')"

review_fixture() { # body [sha] [submitted_at] [state] -- appends Copilot review summary
  local f="$STATE/reviews.json" id
  [ -f "$f" ] || echo '[]' > "$f"
  id="$(($(jq 'length' "$f") + 1))"
  jq --argjson id "$id" --arg b "$1" --arg c "${2:-abc123}" --arg t "${3:-2026-10-12T13:20:00Z}" --arg s "${4:-APPROVED}" \
    '. + [{id: $id, user: {login: "copilot-pull-request-reviewer[bot]"}, commit_id: $c, submitted_at: $t, state: $s, body: $b}]' \
    "$f" > "$f.tmp" && mv "$f.tmp" "$f"
  rm -f "$STATE/requested-reviewers.json"
}
pr_comments() { jq -r '.[] | if type == "object" then .body else . end' "$STATE/pr-145.comments"; }
set_thread() {
  jq -n --arg body "$1" --arg sha "${2:-abc123}" \
    '{data:{repository:{pullRequest:{reviewThreads:{nodes:[{isResolved:false,comments:{nodes:[{body:$body,path:"a.cs",line:3,commit:{oid:$sha},author:{login:"copilot-pull-request-reviewer"}}]}}]}}}}}' \
    > "$STATE/threads.json"
}

setup_review
process_review 145 39 abc123 >/dev/null
check "explicit approval promotes completed green head" "agent:review 1" "$(labels_of 39) $(calls_matching 'pr ready')"
process_review 145 39 abc123 >/dev/null
check "replayed approval event has no duplicate side effect" "1 2" "$(calls_matching 'pr ready') $(calls_matching 'issue comment 39')"

setup_review
set_thread "Advisory: consider renaming this helper." abc123
process_review 145 39 abc123 >/dev/null
check "explicit approval treats inline suggestion as advisory" "agent:review 0" "$(labels_of 39) $(grep -c '^task create' "$STATE/calls.log" || true)"

setup_review
review_fixture "Changes recommended but no explicit outcome." abc123 "2026-10-12T13:20:00Z" COMMENTED
process_review 145 39 abc123 >/dev/null
check "generic summary is ambiguous and escalates" "agent:blocked 0 0" "$(labels_of 39) $(grep -c '^task create' "$STATE/calls.log" || true) $(calls_matching 'pr ready')"
check_contains "ambiguous result explains human decision" "not an explicit acceptance" "$(pr_comments)"

setup_review
review_fixture "Blocking findings:\n- CI failure is incorrectly ignored." abc123 "2026-10-12T13:20:00Z" CHANGES_REQUESTED
process_review 145 39 abc123 >/dev/null
check "explicit blocking summary starts one tracked fix task" "agent:working 1" "$(labels_of 39) $(grep -c '^task create' "$STATE/calls.log")"
check_contains "summary handoff records review round" "review-feedback=2" "$(pr_comments)"

setup_review
review_fixture "Changes recommended" abc123 "2026-10-12T13:20:00Z" CHANGES_REQUESTED
process_review 145 39 abc123 >/dev/null
check "generic changes-recommended phrase is not an actionable blocker" "agent:blocked 0" "$(labels_of 39) $(grep -c '^task create' "$STATE/calls.log" || true)"

setup_review
review_fixture "Approved with conflicting Blocking findings: remove prior fix." abc123 "2026-10-12T13:20:00Z" APPROVED
process_review 145 39 abc123 >/dev/null
check "contradictory review outcome escalates" "agent:blocked 0" "$(labels_of 39) $(calls_matching 'pr ready')"

# --- inline blocker repetition, substantive rebuttal, and uncapped progress ----------------------------------
setup_review
review_fixture "Changes requested" abc123 "2026-10-12T13:20:00Z" CHANGES_REQUESTED
set_thread "Fix the null dereference." abc123
process_review 145 39 abc123 >/dev/null
check "current-SHA blocking finding starts a task" "agent:working 1" "$(labels_of 39) $(grep -c '^task create' "$STATE/calls.log")"
check_contains "blocking finding is preserved in handoff" "a.cs:3 Fix the null dereference." "$(pr_comments)"
process_review 145 39 abc123 >/dev/null
check "same review event does not launch a duplicate task" "1" "$(grep -c '^task create' "$STATE/calls.log")"

setup_review
review_fixture "Changes requested" abc123 "2026-10-12T13:20:00Z" CHANGES_REQUESTED
set_thread "Fix the null dereference." abc123
process_review 145 39 abc123 >/dev/null
add_task task-1 completed "2026-10-12T13:25:00Z"
review_fixture "Still failing" abc123 "2026-10-12T13:30:00Z" CHANGES_REQUESTED
process_review 145 39 abc123 >/dev/null
check "repeated blocker without progress pauses for human" "agent:blocked 1" "$(labels_of 39) $(grep -c '^task create' "$STATE/calls.log")"
check_contains "no-progress escalation names the repeated feedback" "without a new evidence-based rebuttal" "$(pr_comments)"
process_review 145 39 abc123 >/dev/null
check "blocked replay does not restart the coder" "1" "$(grep -c '^task create' "$STATE/calls.log")"

setup_review
review_fixture "Changes requested" abc123 "2026-10-12T13:20:00Z" CHANGES_REQUESTED
set_thread "Fix the null dereference." abc123
process_review 145 39 abc123 >/dev/null
add_task task-1 completed "2026-10-12T13:25:00Z"
feedback="$(feedback_signature '- a.cs:3 Fix the null dereference.')"
jq -n --arg marker "<!-- agent-orchestrator:response-feedback=$feedback -->" \
  '{id:99,body:($marker + "\nEvidence: The null guard is covered by test X and the relevant path now returns safely."),author:{login:"Copilot"},createdAt:"2026-10-12T13:26:00Z"}' \
  > "$STATE/copilot-response.json"
jq --slurpfile response "$STATE/copilot-response.json" '. + [$response[0]]' "$STATE/pr-145.comments" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-145.comments"
review_fixture "Changes requested" abc123 "2026-10-12T13:30:00Z" CHANGES_REQUESTED
process_review 145 39 abc123 >/dev/null
check "substantive evidence-based rebuttal gets reviewed on same SHA" "2" "$(grep -c '^task create' "$STATE/calls.log")"
check_contains "response round is consumed exactly once" "response-used=99" "$(pr_comments)"
add_task task-2 completed "2026-10-12T13:35:00Z"
review_fixture "Still failing" abc123 "2026-10-12T13:40:00Z" CHANGES_REQUESTED
process_review 145 39 abc123 >/dev/null
check "same rebuttal cannot sustain a no-progress loop" "agent:blocked 2" "$(labels_of 39) $(grep -c '^task create' "$STATE/calls.log")"

setup_review
for i in 1 2 3 4 5; do
  review_minute=$((19 + i * 2))
  task_minute=$((20 + i * 2))
  printf -v review_time '2026-10-12T13:%02d:00Z' "$review_minute"
  printf -v task_time '2026-10-12T13:%02d:00Z' "$task_minute"
  review_fixture "Blocking finding $i" abc123 "$review_time" CHANGES_REQUESTED
  set_thread "Distinct defect $i." abc123
  process_review 145 39 abc123 >/dev/null
  add_task "task-$i" completed "$task_time"
done
check "productive feedback supports more than three rounds" "5" "$(grep -c '^task create' "$STATE/calls.log")"
check "productive rounds do not trigger arbitrary escalation" "agent:working" "$(labels_of 39)"

# --- stale review/CI, task completion in either order, and timeout safeguards --------------------------------
setup_review
jq '.headRefOid = "newsha"' "$STATE/pr-145.json" > "$STATE/tmp" && mv "$STATE/tmp" "$STATE/pr-145.json"
process_review 145 39 abc123 >/dev/null
check "delayed review for earlier SHA is ignored" "agent:working 0" "$(labels_of 39) $(calls_matching 'pr ready')"

setup_review
handle_ci_failure 145 39 old-sha failure https://ci/stale >/dev/null
check "delayed CI for earlier SHA is ignored" "0 0" "$(grep -c '^task create' "$STATE/calls.log" || true) $(calls_matching 'pr comment')"

setup_review
echo '{"workflow_runs":[{"created_at":"2026-10-12T13:00:00Z","conclusion":"failure"}]}' > "$STATE/ci-runs.json"
echo '[]' > "$STATE/reviews.json"
if request_review_if_eligible 145 39 abc123 >/dev/null; then r=yes; else r=no; fi
check "CI-before-completion remains pending without external actions" "no" "$r"
check "failed CI causes no review request" "0" "$(calls_matching 'request review')"
echo '{"workflow_runs":[{"created_at":"2026-10-12T13:10:00Z","conclusion":"success"}]}' > "$STATE/ci-runs.json"
request_review_if_eligible 145 39 abc123 >/dev/null
check "later green CI unlocks completed task" "1" "$(calls_matching 'request review')"

setup_review
echo '[]' > "$STATE/reviews.json"
request_review_if_eligible 145 39 abc123 >/dev/null
REVIEW_TIMEOUT_SECONDS=0 request_review_if_eligible 145 39 abc123 >/dev/null
check "timed-out review is escalated visibly" "agent:blocked 2" "$(labels_of 39) $(calls_matching 'pr comment 145')"
check_contains "review timeout asks for a human decision" "did not arrive before the review timeout" "$(pr_comments)"

setup_review
add_task task-initial queued
record_agent_task 39 task-initial abc123
if start_pr_fix_task 145 39 abc123 "duplicate" >/dev/null; then r=launched; else r=blocked; fi
check "active coding session suppresses a duplicate launch" "blocked 0" "$r $(grep -c '^task create' "$STATE/calls.log" || true)"

rm -rf "$STATE"
echo "$passes passed, $failures failed"
[ "$failures" -eq 0 ]
