#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export GITHUB_REPOSITORY=owner/repo
export GITHUB_WORKFLOW_REF=owner/repo/.github/workflows/ci.yml@refs/heads/master
export MANAGED_RUN_ID=123 ORCHESTRATION_CI_WORKFLOW_ID=456
export ORCHESTRATION_CI_REF=refs/tags/managed-v1
export ORCHESTRATION_CI_DEFINITION_SHA=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export TESTED_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
output="$(mktemp)"
trap 'rm -f "$output"' EXIT
passes=0

gh() {
  [[ "$1" == api && "$#" == 2 ]] || return 1
  case "$2" in
    repos/owner/repo/commits/refs/tags/managed-v1) printf '%s\n' "$DEFINITION_JSON" ;;
    repos/owner/repo/actions/runs/123) printf '%s\n' "$RUN_JSON" ;;
    'repos/owner/repo/actions/runs/123/jobs?filter=latest&per_page=100') printf '%s\n' "$JOBS_JSON" ;;
    *) echo 'Unexpected API request.' >&2; return 1 ;;
  esac
}
export -f gh

reset() {
  export DEFINITION_JSON='{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}'
  export RUN_JSON='{"id":123,"workflow_id":456,"repository":{"full_name":"owner/repo"},"path":".github/workflows/agent-managed-ci.yml","event":"workflow_dispatch","head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","head_branch":"managed-v1","run_attempt":1,"status":"completed","conclusion":"success"}'
  export JOBS_JSON='{"total_count":1,"jobs":[{"id":789,"run_id":123,"run_attempt":1,"head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","name":"Test bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","status":"completed","conclusion":"success","steps":[{"name":"Verify tested SHA","status":"completed","conclusion":"success"}]}]}'
  : > "$output"
}

accept() {
  GITHUB_OUTPUT="$output" bash "$SCRIPT_DIR/../resolve-managed-ci-head.sh"
  [[ "$(cat "$output")" == "sha=$TESTED_SHA" ]] || { echo 'Wrong tested SHA.' >&2; exit 1; }
  passes=$((passes + 1))
}

reject() {
  if GITHUB_OUTPUT="$output" bash "$SCRIPT_DIR/../resolve-managed-ci-head.sh" > /dev/null 2>&1; then
    echo "Unsafe bridge input accepted: $1" >&2
    exit 1
  fi
  [[ ! -s "$output" ]] || { echo 'Rejected input produced a tested SHA.' >&2; exit 1; }
  passes=$((passes + 1))
}

reset
accept
reset
RUN_JSON="$(jq '.conclusion = "failure"' <<< "$RUN_JSON")"
JOBS_JSON="$(jq '.jobs[0].conclusion = "failure"' <<< "$JOBS_JSON")"
accept

for mutation in \
  '.id = 124' \
  '.workflow_id = 457' \
  '.repository.full_name = "other/repo"' \
  '.path = ".github/workflows/ci.yml"' \
  '.event = "push"' \
  '.head_sha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"' \
  '.head_branch = "master"' \
  '.run_attempt = 2' \
  '.status = "in_progress"' \
  '.conclusion = "cancelled"'; do
  reset
  RUN_JSON="$(jq "$mutation" <<< "$RUN_JSON")"
  reject "$mutation"
done

for mutation in \
  '.total_count = 2' \
  '.jobs = []' \
  '.jobs += .jobs' \
  '.jobs[0].id = 0' \
  '.jobs[0].run_id = 124' \
  '.jobs[0].run_attempt = 2' \
  '.jobs[0].head_sha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"' \
  '.jobs[0].name = "Test master"' \
  '.jobs[0].status = "in_progress"' \
  '.jobs[0].conclusion = "skipped"' \
  '.jobs[0].steps = []' \
  '.jobs[0].steps += .jobs[0].steps' \
  '.jobs[0].steps[0].status = "in_progress"' \
  '.jobs[0].steps[0].conclusion = "failure"'; do
  reset
  JOBS_JSON="$(jq "$mutation" <<< "$JOBS_JSON")"
  reject "$mutation"
done

reset
DEFINITION_JSON='{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}'
reject 'Moved definition tag'
reset
DEFINITION_JSON='not-json'
reject 'Malformed API response'
reset
GITHUB_WORKFLOW_REF=owner/repo/.github/workflows/ci.yml@refs/heads/untrusted reject 'Untrusted bridge definition'
reset
ORCHESTRATION_CI_REF=refs/heads/master reject 'Mutable managed CI route'
reset
MANAGED_RUN_ID='123/other' reject 'Invalid run ID'
reset
ORCHESTRATION_CI_WORKFLOW_ID='' reject 'Missing workflow ID'
reset
ORCHESTRATION_CI_DEFINITION_SHA='' reject 'Missing definition SHA'

publisher="$(awk '
  /- name: Publish independent CI result/ { publisher = 1 }
  publisher && /^        run: \|/ { body = 1; next }
  body { sub(/^          /, ""); print }
' "$SCRIPT_DIR/../../workflows/ci.yml")"
[[ -n "$publisher" ]] || { echo 'Missing CI result publisher.' >&2; exit 1; }
export RUN_URL=https://github.com/owner/repo/actions/runs/999
export STATUS_OUTPUT="$output"
gh() {
  [[ "${PUBLISH_FAILURE:-}" != true ]] || return 1
  [[ "$1 $2 $3 $4 $5" == "api --method POST repos/owner/repo/statuses/$TESTED_SHA --silent" ]] || return 1
  shift 5
  local state="" context="" url=""
  while [[ "$#" -gt 0 ]]; do
    [[ "$1" == -f ]] || return 1
    case "$2" in
      state=*) state="${2#state=}" ;;
      context=*) context="${2#context=}" ;;
      target_url=*) url="${2#target_url=}" ;;
      description=*) ;;
      *) return 1 ;;
    esac
    shift 2
  done
  [[ "$context" == 'Build and test' && "$url" == "$RUN_URL" ]] || return 1
  printf '%s\n' "$state" > "$STATUS_OUTPUT"
}
export -f gh
for pair in success:success failure:failure cancelled:error skipped:error; do
  : > "$output"
  CI_RESULT="${pair%%:*}" bash -c "$publisher"
  [[ "$(cat "$output")" == "${pair#*:}" ]] || { echo 'Wrong published CI result.' >&2; exit 1; }
  passes=$((passes + 1))
done
: > "$output"
if CI_RESULT=unknown bash -c "$publisher" > /dev/null 2>&1 || [[ -s "$output" ]]; then
  echo 'Unexpected CI result published a status.' >&2
  exit 1
fi
passes=$((passes + 1))
if CI_RESULT=success PUBLISH_FAILURE=true bash -c "$publisher" > /dev/null 2>&1; then
  echo 'Failed status publication reported success.' >&2
  exit 1
fi
passes=$((passes + 1))
printf 'Passed %s managed CI bridge tests.\n' "$passes"
