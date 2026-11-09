import contextlib
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest.mock import patch


WORKFLOW = Path(__file__).resolve().parents[1] / "workflows/agent-task-mapping-diagnostics.yml"
SCRIPT = textwrap.dedent(WORKFLOW.read_text().split("        run: |\n", 1)[1])
DATABASE_ID = 5231998762
PRIVATE = "PRIVATE_VALUE"


class MappingDiagnosticTests(unittest.TestCase):
    def run_diagnostic(self, artifact_id=DATABASE_ID, rest_id=DATABASE_ID,
                       node_id=str(DATABASE_ID), global_id=PRIVATE, timeout=None,
                       missing_ids=False):
        task = {
            "id": "e4743d38-6b27-49cd-a272-83b34465a808",
            "repository": {"id": 1277929203},
            "creator": {"id": 6195946},
            "state": "completed",
            "prompt": PRIVATE,
            "artifacts": [{
                "provider": "github", "type": "pull",
                "data": {"id": artifact_id, "global_id": global_id},
            }],
        }
        pr = {
            "id": rest_id, "node_id": global_id, "number": 232, "state": "open",
            "head": {"repo": {"id": 1277929203}, "ref": PRIVATE, "sha": "a" * 40},
            "base": {"repo": {"id": 1277929203}, "ref": "master"},
        }
        node = {
            "__typename": "PullRequest", "fullDatabaseId": node_id, "number": 232,
            "repository": {"nameWithOwner": "ExarKun1983/IntelligentTradingSimulator"},
        }
        if missing_ids:
            del task["artifacts"][0]["data"]["id"]
            del pr["id"]
            del node["fullDatabaseId"]
        calls = []

        class Response:
            def __init__(self, value, route):
                self.value = value
                self.route = route

            def __enter__(self):
                return self

            def __exit__(self, *args):
                pass

            def read(self, limit):
                if self.route == timeout:
                    raise TimeoutError(PRIVATE)
                return json.dumps(self.value).encode()[:limit]

        class Opener:
            def open(self, request, timeout):
                route = (
                    "task" if "/agents/" in request.full_url
                    else "graphql" if request.full_url.endswith("/graphql") else "pr"
                )
                calls.append(route)
                expected_token = "PRIVATE_USER" if route == "task" else "PRIVATE_WORKFLOW"
                assert request.get_header("Authorization") == "Bearer " + expected_token
                if route == "graphql":
                    body = json.loads(request.data)
                    assert request.get_method() == "POST"
                    assert body["query"].startswith("query(")
                    assert "mutation" not in body["query"]
                else:
                    assert request.get_method() == "GET"
                value = {"task": task, "pr": pr, "graphql": {"data": {"node": node}}}[route]
                return Response(copy.deepcopy(value), route)

        with tempfile.TemporaryDirectory() as directory:
            summary_path = Path(directory) / "summary"
            output = io.StringIO()
            with patch.dict(os.environ, {
                "TASK_READ_TOKEN": "PRIVATE_USER",
                "WORKFLOW_READ_TOKEN": "PRIVATE_WORKFLOW",
                "GITHUB_STEP_SUMMARY": str(summary_path),
            }), patch("urllib.request.build_opener", return_value=Opener()), contextlib.redirect_stdout(output):
                try:
                    exec(compile(SCRIPT, str(WORKFLOW), "exec"), {})
                except SystemExit as error:
                    self.assertEqual(timeout, "task")
                    self.assertEqual(str(error), "Task read failed; transport error.")
                    self.assertNotIn(PRIVATE, str(error))
                    return None
            text = output.getvalue() + summary_path.read_text()
            self.assertNotIn("PRIVATE_", text)
            report = json.loads(output.getvalue())
            self.assertTrue(report["task_id_matches"])
            return report["mapping_checks"]

    def test_numeric_and_bigint_string_identities_match(self):
        for value in (DATABASE_ID, str(DATABASE_ID)):
            with self.subTest(value=value):
                checks = self.run_diagnostic(node_id=value)
                self.assertTrue(checks["pull_ids_match_rest"])
                self.assertTrue(checks["graphql_identity_matches"])

    def test_missing_and_invalid_database_ids_never_match(self):
        for value in (None, 0, -1, True, 1.0, "5231998762", 9223372036854775808):
            with self.subTest(value=value):
                checks = self.run_diagnostic(artifact_id=value, rest_id=value, node_id=value)
                self.assertFalse(checks["pull_ids_match_rest"])
                self.assertFalse(checks["graphql_identity_matches"])
        checks = self.run_diagnostic(missing_ids=True)
        self.assertFalse(checks["pull_ids_match_rest"])
        self.assertFalse(checks["graphql_identity_matches"])

    def test_missing_and_invalid_global_ids_never_match(self):
        for value in (None, "", " ", 42, "x" * 257):
            with self.subTest(value=value):
                checks = self.run_diagnostic(global_id=value)
                self.assertFalse(checks["pull_ids_match_rest"])
                self.assertNotIn("graphql_read", checks)

    def test_invalid_graphql_database_ids_never_match(self):
        for value in (None, True, 1.0, 0, -1, "", "None", "0", "-1", "1.0",
                      "9223372036854775808", DATABASE_ID + 1):
            with self.subTest(value=value):
                checks = self.run_diagnostic(node_id=value)
                self.assertTrue(checks["pull_ids_match_rest"])
                self.assertFalse(checks["graphql_identity_matches"])

    def test_response_body_timeouts_preserve_prior_results(self):
        for route, label in (("pr", "live_pr_read"), ("graphql", "graphql_read")):
            with self.subTest(route=route):
                checks = self.run_diagnostic(timeout=route)
                self.assertTrue(checks["task_identity_valid"])
                self.assertEqual(checks[label], {"error": "transport"})
                self.assertNotIn("graphql_identity_matches", checks)
                if route == "graphql":
                    self.assertTrue(checks["live_pr_valid"])
                    self.assertTrue(checks["pull_ids_match_rest"])

    def test_task_body_timeout_has_safe_error(self):
        self.run_diagnostic(timeout="task")


if __name__ == "__main__":
    unittest.main()
