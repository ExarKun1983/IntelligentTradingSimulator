#!/usr/bin/env bash
# Offline tests for .github/scripts/agent-orchestrator.sh. The gh CLI is replaced by a file-backed stub.
# Run: bash .github/scripts/tests/agent-orchestrator.test.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=SCRIPTDIR/../agent-orchestrator.sh
source "$SCRIPT_DIR/../agent-orchestrator.sh"

export REPO="owner/repo" READY_LABEL="agent:ready" WORKING_LABEL="agent:working" AI_REVIEW_LABEL="agent:ai-review" \
  REVIEW_LABEL="agent:review" BLOCKED_LABEL="agent:blocked" COPILOT_REVIEWER="copilot-pull-request-reviewer[bot]" \
  MAX_FIX_ROUNDS=3 REVIEW_WAIT_SECONDS=0 POLL_SECONDS=0 LAUNCH_MATCH_WINDOW_SECONDS=1800 CI_WORKFLOW_FILE=ci.yml
unset CI_CONFIRMED_SHA ACTIONS_TOKEN || true

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
#   threads.json, ci-runs.json, reviews.json
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

json_comments() { jq '{comments: map({body: .})}' "$1"; }

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
      path="$(printf '%s\n' "$@" | grep -E '^repos/|^graphql$' | head -1)"
      case "$path" in
        graphql) cat "$STATE/threads.json" ;;
        repos/*/issues/*/events*) n="${path#repos/*/issues/}"; n="${n%%/*}"; cat "$STATE/issue-$n.events" 2>/dev/null || echo '[]' ;;
        repos/*/issues/*)
          n="${path##*/}"
          case "$(cat "$STATE/issue-$n.kind" 2>/dev/null || echo missing)" in
            issue) printf '{"number":%s}\n' "$n" ;;
            pr) printf '{"number":%s,"pull_request":{}}\n' "$n" ;;
            *) echo "HTTP 404" >&2; return 1 ;;
          esac ;;
        repos/*/actions/workflows/*) cat "$STATE/ci-runs.json" 2>/dev/null || echo '{"workflow_runs":[]}' ;;
        repos/*/reviews) cat "$STATE/reviews.json" 2>/dev/null || echo '[]' ;;
        repos/*/requested_reviewers) if [[ "$*" == *POST* ]]; then echo "request review" >> "$STATE/calls.log"; else echo '{"users":[]}'; fi ;;
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

add_pr() { # number body createdAt [head]
  jq -n --arg b "$2" --arg c "$3" --arg h "${4:-sha-$1}" '{body: $b, createdAt: $c, state: "OPEN", headRefOid: $h}' > "$STATE/pr-$1.json"
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

# --- CI failure routing ------------------------------------------------------------------------------------
setup_ci() {
  reset_state
  add_issue 39 "agent:ai-review"
  add_pr 145 $'<!-- agent-orchestrator:issue=39 -->' "2026-10-12T12:46:22Z" "abc123"
}

setup_ci
handle_ci_failure 145 39 abc123 failure https://ci/run/1 >/dev/null
check "failed CI moves Issue out of ai-review to working" "agent:working" "$(labels_of 39)"
check_contains "failed CI is handed to Copilot" "@copilot CI failed for commit abc123" "$(jq -r '.[]' "$STATE/pr-145.comments")"
check_contains "handoff carries the SHA marker" "agent-orchestrator:ci-failure-sha=abc123" "$(jq -r '.[]' "$STATE/pr-145.comments")"
handle_ci_failure 145 39 abc123 failure https://ci/run/1 >/dev/null
check "no duplicate comment for the same SHA" "1" "$(calls_matching 'pr comment 145')"
check "failed CI never promotes to review" "0" "$(calls_matching 'pr ready')"

setup_ci
handle_ci_failure 145 39 abc123 cancelled https://ci/run/2 >/dev/null
check "cancelled CI blocks for human attention" "agent:blocked" "$(labels_of 39)"
check_contains "cancelled CI explains why" 'concluded "cancelled"' "$(jq -r '.[]' "$STATE/pr-145.comments")"
handle_ci_failure 145 39 abc123 cancelled https://ci/run/2 >/dev/null
check "no duplicate blocked comment" "1" "$(calls_matching 'pr comment 145')"

setup_ci
for sha in s1 s2 s3; do
  append_json "$STATE/pr-145.comments" "$(printf '@copilot CI failed for commit %s: url\n\n<!-- agent-orchestrator:ci-failure-sha=%s -->' "$sha" "$sha")"
done
handle_ci_failure 145 39 abc123 failure https://ci/run/3 >/dev/null
check "CI fix rounds are capped" "agent:blocked" "$(labels_of 39)"
check_contains "cap is explained" "stopped after 3 automatic CI fix rounds" "$(jq -r '.[]' "$STATE/pr-145.comments")"

setup_ci
for sha in c1 c2 c3; do
  append_json "$STATE/pr-145.comments" "$(printf 'Agent orchestrator: CI for %s concluded "cancelled".\n\n<!-- agent-orchestrator:ci-failure-sha=%s -->' "$sha" "$sha")"
done
handle_ci_failure 145 39 abc123 failure https://ci/run/3 >/dev/null
check "cancelled-CI notices do not use up fix rounds" "agent:working" "$(labels_of 39)"

setup_ci
handle_ci_failure 145 39 old-sha failure https://ci/run/4 >/dev/null
check "outdated CI result is ignored" "agent:ai-review" "$(labels_of 39)"
check "outdated CI result posts nothing" "0" "$(calls_matching 'pr comment')"

setup_ci
printf 'agent:blocked\n' > "$STATE/issue-39.labels"
handle_ci_failure 145 39 abc123 failure https://ci/run/5 >/dev/null
check "already blocked Issue is left for a human" "agent:blocked 0" "$(labels_of 39) $(calls_matching 'pr comment')"

# --- review promotion requires green CI --------------------------------------------------------------------
setup_review() {
  setup_ci
  echo '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[]}}}}}' > "$STATE/threads.json"
}

setup_review
echo '{"workflow_runs":[{"created_at":"2026-10-12T13:00:00Z","conclusion":"failure"}]}' > "$STATE/ci-runs.json"
process_review 145 39 abc123 >/dev/null
check "clean review with failed CI is not promoted" "agent:ai-review 0" "$(labels_of 39) $(calls_matching 'pr ready')"

setup_review
echo '{"workflow_runs":[{"created_at":"2026-10-12T13:00:00Z","conclusion":"failure"},{"created_at":"2026-10-12T13:10:00Z","conclusion":"success"}]}' > "$STATE/ci-runs.json"
process_review 145 39 abc123 >/dev/null
check "clean review with green CI is promoted" "agent:review 1" "$(labels_of 39) $(calls_matching 'pr ready')"

setup_review
CI_CONFIRMED_SHA=abc123 process_review 145 39 abc123 >/dev/null
check "green CI from the workflow_run event promotes" "agent:review" "$(labels_of 39)"

setup_review
echo '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[{"isResolved":false,"comments":{"nodes":[{"body":"Bug","path":"a.cs","line":3,"author":{"login":"copilot-pull-request-reviewer"}}]}}]}}}}}' > "$STATE/threads.json"
process_review 145 39 abc123 >/dev/null
check "review findings are still handed to Copilot" "agent:working" "$(labels_of 39)"
check_contains "findings handoff lists the finding" "a.cs:3 Bug" "$(jq -r '.[]' "$STATE/pr-145.comments")"

rm -rf "$STATE"
echo "$passes passed, $failures failed"
[ "$failures" -eq 0 ]
