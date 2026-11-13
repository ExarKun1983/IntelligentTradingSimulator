#!/usr/bin/env bash
set -euo pipefail

readonly REPO="ExarKun1983/IntelligentTradingSimulator"
readonly EXPECTED_LOGIN="ExarKun1983"
readonly ISSUE_NUMBER="261"
readonly ENVIRONMENT="agent-orchestration"
readonly CONFIRMATION="APPLY-REVIEW-E2E-261"

usage() {
  cat <<'EOF'
Usage: bash .github/scripts/configure-review-e2e-261-vars.sh [--dry-run|--apply]

Prepare repository variables for the one-time Issue #261 state bootstrap.
Defaults to --dry-run. --apply requires typing APPLY-REVIEW-E2E-261.
This script does not dispatch workflows, add labels, set CI variables, or change secrets.
EOF
}

fail() {
  printf 'Stopped: %s\n' "$1" >&2
  exit 1
}

mode="dry-run"
case "${1:---dry-run}" in
  --dry-run) mode="dry-run" ;;
  --apply) mode="apply" ;;
  --help|-h) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac
if [ "$#" -gt 1 ]; then usage >&2; exit 2; fi

command -v gh >/dev/null || fail "GitHub CLI (gh) is required."
command -v jq >/dev/null || fail "jq is required."
gh auth status --hostname github.com --active >/dev/null ||
  fail "Authenticate gh with a user account that can read and write repository Actions variables."

user_json="$(gh api user)" || fail "Could not read the authenticated GitHub user."
login="$(jq -er '.login' <<< "$user_json")" || fail "GitHub user response is invalid."
actor_id="$(jq -er '.id | tostring' <<< "$user_json")" || fail "GitHub user ID is invalid."
[[ "$login" == "$EXPECTED_LOGIN" ]] || fail "Authenticate as $EXPECTED_LOGIN, not $login."
[[ "$actor_id" =~ ^[1-9][0-9]*$ ]] || fail "GitHub user ID is invalid."

repo_json="$(gh api "repos/$REPO")" || fail "Could not read repository metadata."
[[ "$(jq -r '.full_name' <<< "$repo_json")" == "$REPO" ]] || fail "Repository identity mismatch."
[[ "$(jq -r '.default_branch' <<< "$repo_json")" == "master" ]] || fail "Expected master as the default branch."

definition_sha="$(gh api "repos/$REPO/commits/master" --jq '.sha')" ||
  fail "Could not resolve the current master commit."
[[ "$definition_sha" =~ ^[0-9a-f]{40}$ ]] || fail "Master did not resolve to a lowercase commit SHA."

issue_json="$(gh api "repos/$REPO/issues/$ISSUE_NUMBER")" || fail "Could not read Issue #$ISSUE_NUMBER."
[[ "$(jq -r '.state' <<< "$issue_json")" == "open" ]] || fail "Issue #$ISSUE_NUMBER must be open."
jq -e 'has("pull_request") | not' <<< "$issue_json" >/dev/null ||
  fail "Issue #$ISSUE_NUMBER resolved to a pull request."
jq -e --arg label "agent:ready" 'any(.labels[]?; .name == $label)' <<< "$issue_json" >/dev/null &&
  fail "Remove agent:ready before preparing the bootstrap variables."

repo_vars="$(gh variable list --repo "$REPO" --json name,value)" ||
  fail "Could not read repository variables; no changes were made."
environment_vars="$(gh variable list --repo "$REPO" --env "$ENVIRONMENT" --json name,value)" ||
  fail "Could not inspect environment-scoped variables; no changes were made."

protected_names='["ORCHESTRATION_ACTIVATION_APPROVED","ORCHESTRATION_ACTOR_ID","ORCHESTRATION_DEFINITION_SHA","ORCHESTRATION_TEST_ISSUE","ORCHESTRATION_RETIRED_TEST_ISSUE","ORCHESTRATION_TEST_MAX_REPAIR_TASKS","ORCHESTRATION_REVIEW_E2E_BOOTSTRAP_APPROVED"]'
jq -e --argjson names "$protected_names" \
  'any(.[]; .name as $name | $names | index($name) != null)' \
  <<< "$environment_vars" >/dev/null &&
  fail "Remove environment-scoped overrides for the Issue #261 bootstrap variables first."

variable_value() {
  jq -r --arg name "$1" '[.[] | select(.name == $name) | .value] | if length == 1 then .[0] else "" end' \
    <<< "$repo_vars"
}

activation="$(variable_value ORCHESTRATION_ACTIVATION_APPROVED)"
[[ "$activation" != "true" ]] || fail "Activation is already true; this script will not disable an active workflow."
[[ -z "$activation" || "$activation" == "false" ]] ||
  fail "ORCHESTRATION_ACTIVATION_APPROVED must be false or unset."

configured_actor="$(variable_value ORCHESTRATION_ACTOR_ID)"
[[ -z "$configured_actor" || "$configured_actor" == "$actor_id" ]] ||
  fail "ORCHESTRATION_ACTOR_ID does not match the authenticated operator; it was not changed."

declare -a set_names=()
declare -a set_values=()
plan_set() {
  local name="$1" value="$2"
  if [ "$(variable_value "$name")" != "$value" ]; then
    set_names+=("$name")
    set_values+=("$value")
  fi
}

plan_set ORCHESTRATION_ACTIVATION_APPROVED false
plan_set ORCHESTRATION_ACTOR_ID "$actor_id"
plan_set ORCHESTRATION_DEFINITION_SHA "$definition_sha"
plan_set ORCHESTRATION_TEST_ISSUE "$ISSUE_NUMBER"
plan_set ORCHESTRATION_TEST_MAX_REPAIR_TASKS 1
plan_set ORCHESTRATION_REVIEW_E2E_BOOTSTRAP_APPROVED true

retired_issue="$(variable_value ORCHESTRATION_RETIRED_TEST_ISSUE)"
printf 'Repository: %s\nIssue: #%s (open, without agent:ready)\nMaster definition: %s\n' \
  "$REPO" "$ISSUE_NUMBER" "$definition_sha"
if [ "${#set_names[@]}" -eq 0 ] && [ -z "$retired_issue" ]; then
  printf 'All bootstrap variables already match; nothing to change.\n'
  exit 0
fi
for index in "${!set_names[@]}"; do
  printf 'Set %s=%s\n' "${set_names[$index]}" "${set_values[$index]}"
done
if [ -n "$retired_issue" ]; then
  printf 'Delete ORCHESTRATION_RETIRED_TEST_ISSUE\n'
fi
printf 'CI variables, secrets, labels, workflows, and activation=true will not be changed.\n'

if [ "$mode" == "dry-run" ]; then
  printf 'Dry run only. Re-run with --apply to write these repository variables.\n'
  exit 0
fi

read -r -p "Type $CONFIRMATION to continue: " confirmation
[[ "$confirmation" == "$CONFIRMATION" ]] || fail "Confirmation did not match; no changes were made."

for index in "${!set_names[@]}"; do
  gh variable set "${set_names[$index]}" --repo "$REPO" --body "${set_values[$index]}" ||
    fail "Could not set ${set_names[$index]}; inspect current variables before rerunning."
done
if [ -n "$retired_issue" ]; then
  gh variable delete ORCHESTRATION_RETIRED_TEST_ISSUE --repo "$REPO" ||
    fail "Could not delete ORCHESTRATION_RETIRED_TEST_ISSUE; inspect current variables before rerunning."
fi

verified_vars="$(gh variable list --repo "$REPO" --json name,value)" ||
  fail "Writes finished, but variables could not be re-read for verification."
repo_vars="$verified_vars"
for index in "${!set_names[@]}"; do
  actual="$(variable_value "${set_names[$index]}")"
  [[ "$actual" == "${set_values[$index]}" ]] ||
    fail "Verification failed for ${set_names[$index]}; inspect repository variables."
done
[[ -z "$(variable_value ORCHESTRATION_RETIRED_TEST_ISSUE)" ]] ||
  fail "ORCHESTRATION_RETIRED_TEST_ISSUE remains set; inspect repository variables."
printf 'Bootstrap variables updated and verified. No workflow was dispatched.\n'
