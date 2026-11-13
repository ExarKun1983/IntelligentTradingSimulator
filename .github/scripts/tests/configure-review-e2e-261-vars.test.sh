#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SCRIPT="$ROOT/.github/scripts/configure-review-e2e-261-vars.sh"
TMP="$(mktemp -d)"
trap 'rm -rf -- "$TMP"' EXIT

mkdir "$TMP/bin"
cat > "$TMP/bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

case "$1 ${2:-}" in
  "auth status") exit 0 ;;
  "api user") printf '{"login":"ExarKun1983","id":6195946}\n' ;;
  "api repos/ExarKun1983/IntelligentTradingSimulator")
    printf '{"full_name":"ExarKun1983/IntelligentTradingSimulator","default_branch":"master"}\n' ;;
  "api repos/ExarKun1983/IntelligentTradingSimulator/commits/master")
    printf '%s\n' 'e41fbfe85a0e20acecd957fcfcaee67e4f900b17' ;;
  "api repos/ExarKun1983/IntelligentTradingSimulator/issues/261")
    printf '{"state":"open","labels":[]}\n' ;;
  "variable list")
    if [[ " $* " == *" --env "* ]]; then cat "$GH_TEST_ENV_STATE"; else cat "$GH_TEST_STATE"; fi ;;
  "variable set")
    name="$3"; value=""
    while [ "$#" -gt 0 ]; do
      if [ "$1" = "--body" ]; then value="$2"; shift 2; else shift; fi
    done
    jq --arg name "$name" --arg value "$value" \
      'map(select(.name != $name)) + [{name:$name,value:$value}]' "$GH_TEST_STATE" > "$GH_TEST_STATE.tmp"
    mv "$GH_TEST_STATE.tmp" "$GH_TEST_STATE"
    printf 'set %s\n' "$name" >> "$GH_TEST_CALLS" ;;
  "variable delete")
    name="$3"
    jq --arg name "$name" 'map(select(.name != $name))' "$GH_TEST_STATE" > "$GH_TEST_STATE.tmp"
    mv "$GH_TEST_STATE.tmp" "$GH_TEST_STATE"
    printf 'delete %s\n' "$name" >> "$GH_TEST_CALLS" ;;
  *) echo "Unexpected gh invocation: $*" >&2; exit 90 ;;
esac
EOF
chmod +x "$TMP/bin/gh"

export PATH="$TMP/bin:$PATH"
export GH_TEST_STATE="$TMP/variables.json"
export GH_TEST_ENV_STATE="$TMP/environment-variables.json"
export GH_TEST_CALLS="$TMP/calls.log"
printf '[]\n' > "$GH_TEST_ENV_STATE"
cat > "$GH_TEST_STATE" <<'EOF'
[
  {"name":"ORCHESTRATION_ACTIVATION_APPROVED","value":"false"},
  {"name":"ORCHESTRATION_ACTOR_ID","value":"6195946"},
  {"name":"ORCHESTRATION_TEST_ISSUE","value":"237"},
  {"name":"ORCHESTRATION_RETIRED_TEST_ISSUE","value":"227"}
]
EOF
: > "$GH_TEST_CALLS"

dry_run_output="$(bash "$SCRIPT" --dry-run)"
grep -Fq 'Dry run only.' <<< "$dry_run_output"
grep -Fq 'Set ORCHESTRATION_TEST_ISSUE=261' <<< "$dry_run_output"
! grep -q . "$GH_TEST_CALLS"

printf '%s\n' '[{"name":"ORCHESTRATION_ACTIVATION_APPROVED","value":"true"}]' > "$GH_TEST_STATE"
if bash "$SCRIPT" --apply </dev/null >"$TMP/active.out" 2>"$TMP/active.err"; then
  echo "Expected active activation to be rejected." >&2
  exit 1
fi
grep -Fq 'Activation is already true' "$TMP/active.err"
! grep -q . "$GH_TEST_CALLS"

cat > "$GH_TEST_STATE" <<'EOF'
[
  {"name":"ORCHESTRATION_ACTIVATION_APPROVED","value":"false"},
  {"name":"ORCHESTRATION_ACTOR_ID","value":"6195946"}
]
EOF
printf '[{"name":"ORCHESTRATION_TEST_ISSUE","value":"237"}]\n' > "$GH_TEST_ENV_STATE"
if bash "$SCRIPT" --dry-run >"$TMP/override.out" 2>"$TMP/override.err"; then
  echo "Expected environment-scoped overrides to be rejected." >&2
  exit 1
fi
grep -Fq 'environment-scoped overrides' "$TMP/override.err"
! grep -q . "$GH_TEST_CALLS"
printf '[]\n' > "$GH_TEST_ENV_STATE"

cat > "$GH_TEST_STATE" <<'EOF'
[
  {"name":"ORCHESTRATION_ACTIVATION_APPROVED","value":"false"},
  {"name":"ORCHESTRATION_ACTOR_ID","value":"6195946"},
  {"name":"ORCHESTRATION_TEST_ISSUE","value":"237"},
  {"name":"ORCHESTRATION_RETIRED_TEST_ISSUE","value":"227"}
]
EOF
if ! printf '%s\n' 'APPLY-REVIEW-E2E-261' | bash "$SCRIPT" --apply >"$TMP/apply.out"; then
  cat "$GH_TEST_STATE" >&2
  cat "$TMP/apply.out" >&2
  exit 1
fi
grep -Fq 'updated and verified. No workflow was dispatched.' "$TMP/apply.out"
jq -e 'any(.[]; .name == "ORCHESTRATION_TEST_ISSUE" and .value == "261")' "$GH_TEST_STATE" >/dev/null
jq -e 'any(.[]; .name == "ORCHESTRATION_REVIEW_E2E_BOOTSTRAP_APPROVED" and .value == "true")' "$GH_TEST_STATE" >/dev/null
! jq -e 'any(.[]; .name == "ORCHESTRATION_RETIRED_TEST_ISSUE")' "$GH_TEST_STATE" >/dev/null
! grep -Eqi 'dispatch|label' "$GH_TEST_CALLS"

echo "configure-review-e2e-261-vars tests passed"
