import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest


WORKFLOW = Path(__file__).resolve().parents[2] / "workflows/agent-gate-diagnostics.yml"
YAML = WORKFLOW.read_text(encoding="utf-8")
SOURCE = textwrap.dedent(YAML.split("        run: |\n", 1)[1])
SHA = "a" * 40
COMPARISONS = dict(re.findall(
    r"^\s+([A-Z_]+_MATCH): \$\{\{ vars\.(ORCHESTRATION_[A-Z_]+) ==",
    YAML, re.MULTILINE,
))


class AgentGateDiagnosticsTests(unittest.TestCase):
    def execute(self, changes=None):
        environment = dict.fromkeys(COMPARISONS, "true")
        environment.update({
            "PATH": os.environ["PATH"],
            "GITHUB_REPOSITORY": "owner/repo",
            "WORKFLOW_ON_MASTER": "true",
            "EVENT_NAME": "workflow_dispatch",
            "EVENT_REF": "refs/heads/master",
            "WORKFLOW_REF": "owner/repo/.github/workflows/agent-gate-diagnostics.yml@refs/heads/master",
            "STATUS_TEST_INACTIVE": "true",
            "CI_REF": "refs/tags/agent-managed-ci-test-237-v1",
            "CI_DEFINITION_SHA": SHA,
            "GH_TOKEN": "offline-token-must-not-be-printed",
            "MOCK_SHA": SHA,
            "MOCK_FAILURE": "false",
        })
        environment.update(changes or {})
        mock = """
        gh() {
          [[ "$*" == 'api repos/owner/repo/git/ref/tags/agent-managed-ci-test-237-v1 --jq select(.object.type == "commit") | .object.sha' ]] || return 99
          [[ "$MOCK_FAILURE" == false ]] || return 1
          printf '%s\\n' "$MOCK_SHA"
        }
        """
        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / "summary"
            environment["GITHUB_STEP_SUMMARY"] = str(summary)
            result = subprocess.run(
                ["bash", "-euo", "pipefail", "-c", textwrap.dedent(mock) + SOURCE],
                env=environment, text=True, capture_output=True, timeout=10,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(result.stdout, summary.read_text(encoding="utf-8"))
        self.assertNotIn("offline-token", result.stdout + result.stderr)
        self.assertNotIn(SHA, result.stdout)
        return result.stdout

    def test_dashboard_covers_main_prerequisites_without_another_configuration_set(self):
        output = self.execute()
        for name in (
            "USER_TOKEN", "ENVIRONMENT", "TASK_ENTITLEMENT", "DEFINITION_VERIFIED",
            "COST", "STATUS_PUBLISHER", "REQUIRED_CONTEXT", "ACTOR", "WORKFLOW",
            "CI_WORKFLOW", "BASE",
        ):
            self.assertIn(name + "_MATCH", COMPARISONS)
        self.assertNotIn("| false |", output)
        self.assertIn("successful job does not approve activation", output)

    def test_each_missing_or_false_attestation_is_visible_not_silently_green(self):
        for comparison, variable in COMPARISONS.items():
            with self.subTest(variable=variable):
                output = self.execute({comparison: "false"})
                rows = [line for line in output.splitlines() if "| " + variable + " ==" in line]
                self.assertEqual(1, len(rows), output)
                self.assertTrue(rows[0].endswith("| false |"), rows[0])

    def test_normal_disabled_activation_is_reported_without_enabling_anything(self):
        output = self.execute({"ACTIVATION_MATCH": "false", "STATUS_TEST_INACTIVE": "false"})
        self.assertIn("| ORCHESTRATION_ACTIVATION_APPROVED == true | false |", output)
        self.assertIn("| Isolated status test activation and bootstrap remain false | false |", output)

    def test_tag_read_failure_and_pin_mismatch_are_distinct_reported_failures(self):
        output = self.execute({"MOCK_FAILURE": "true"})
        self.assertIn("| New CI tag resolves to a commit | false |", output)
        self.assertIn("| ORCHESTRATION_CI_DEFINITION_SHA matches the new tag target | false |", output)
        output = self.execute({"CI_DEFINITION_SHA": "b" * 40})
        self.assertIn("| New CI tag resolves to a commit | true |", output)
        self.assertIn("| ORCHESTRATION_CI_DEFINITION_SHA matches the new tag target | false |", output)


if __name__ == "__main__":
    unittest.main()
