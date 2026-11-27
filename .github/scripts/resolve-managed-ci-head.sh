#!/usr/bin/env bash
set -euo pipefail

fail() { echo "$1" >&2; exit 1; }

command -v gh > /dev/null || fail 'The CI bridge requires gh.'
command -v jq > /dev/null || fail 'The CI bridge requires jq.'
[[ "${GITHUB_REPOSITORY:-}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail 'Invalid repository.'
[[ "${GITHUB_WORKFLOW_REF:-}" == "$GITHUB_REPOSITORY/.github/workflows/ci.yml@refs/heads/master" ]] ||
  fail 'The CI bridge must use the trusted master workflow.'
[[ "${MANAGED_RUN_ID:-}" =~ ^[1-9][0-9]*$ ]] || fail 'Invalid managed CI run ID.'
[[ "${ORCHESTRATION_CI_WORKFLOW_ID:-}" =~ ^[1-9][0-9]*$ ]] || fail 'Invalid managed CI workflow ID.'
[[ "${ORCHESTRATION_CI_REF:-}" == refs/tags/?* ]] || fail 'Managed CI must use an immutable tag.'
git check-ref-format "$ORCHESTRATION_CI_REF" || fail 'Invalid managed CI tag.'
[[ "${ORCHESTRATION_CI_DEFINITION_SHA:-}" =~ ^[0-9a-f]{40}$ ]] || fail 'Invalid managed CI definition SHA.'

definition="$(gh api "repos/$GITHUB_REPOSITORY/commits/$ORCHESTRATION_CI_REF")"
[[ "$(jq -r '.sha' <<< "$definition")" == "$ORCHESTRATION_CI_DEFINITION_SHA" ]] ||
  fail 'The managed CI definition tag moved.'

run="$(gh api "repos/$GITHUB_REPOSITORY/actions/runs/$MANAGED_RUN_ID")"
jq -e --arg id "$MANAGED_RUN_ID" --arg workflow "$ORCHESTRATION_CI_WORKFLOW_ID" \
  --arg repository "$GITHUB_REPOSITORY" --arg sha "$ORCHESTRATION_CI_DEFINITION_SHA" \
  --arg branch "${ORCHESTRATION_CI_REF#refs/tags/}" '
    (.id | tostring) == $id and (.workflow_id | tostring) == $workflow and
    .repository.full_name == $repository and .path == ".github/workflows/agent-managed-ci.yml" and
    .event == "workflow_dispatch" and .head_sha == $sha and .head_branch == $branch and
    .run_attempt == 1 and .status == "completed" and
    (.conclusion == "success" or .conclusion == "failure")
  ' <<< "$run" > /dev/null || fail 'Managed CI run identity or terminal outcome is not trusted.'

jobs="$(gh api "repos/$GITHUB_REPOSITORY/actions/runs/$MANAGED_RUN_ID/jobs?filter=latest&per_page=100")"
jq -e --arg id "$MANAGED_RUN_ID" --arg sha "$ORCHESTRATION_CI_DEFINITION_SHA" '
    .total_count == 1 and (.jobs | length) == 1 and
    (.jobs[0] |
      .id > 0 and (.run_id | tostring) == $id and .run_attempt == 1 and .head_sha == $sha and
      .status == "completed" and (.conclusion == "success" or .conclusion == "failure") and
      (.name | test("^Test [0-9a-f]{40}$")) and
      ([.steps[] | select(.name == "Verify tested SHA")] |
        length == 1 and .[0].status == "completed" and .[0].conclusion == "success"))
  ' <<< "$jobs" > /dev/null || fail 'Managed CI exact-SHA job evidence is missing or ambiguous.'

sha="$(jq -r '.jobs[0].name | ltrimstr("Test ")' <<< "$jobs")"
printf 'sha=%s\n' "$sha" >> "${GITHUB_OUTPUT:?Missing output file}"
