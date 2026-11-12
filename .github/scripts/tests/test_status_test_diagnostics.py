import contextlib
import copy
import datetime
import io
import json
import os
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest.mock import patch
import urllib.error


WORKFLOW = Path(__file__).resolve().parents[2] / "workflows/agent-status-test-diagnostics.yml"
SOURCE = textwrap.dedent(WORKFLOW.read_text(encoding="utf-8").split("        run: |\n", 1)[1])
SHA = "c752c1b221527f6b8dacdea81ab0c49c6d88c4ed"
REF = "refs/tags/agent-status-revocation-definition-v2"
REPO = "repos/ExarKun1983/IntelligentTradingSimulator"


class StatusTestDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.environment = {
            "WORKFLOW_TOKEN": "offline-workflow-token",
            "ISSUE": "242",
            "ACTOR_ID": "6195946",
            "DRIVER_ID": "414090147",
            "CI_ID": "414090145",
            "DRIVER_SHA": SHA,
            "CI_SHA": SHA,
            "DRIVER_REF": REF,
            "CI_REF": REF,
            "STATE_REF": "refs/heads/agent-state-status-revocation-v1",
            "BASE_BRANCH": "agent-status-revocation-base-v1",
            "TEST_ENVIRONMENT": "agent-status-revocation-test",
            "EXPIRES_AT": (
                datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=24)
            ).isoformat(),
        }
        self.responses = {
            "graphql": {"data": {"viewer": {
                "login": "github-actions[bot]", "databaseId": 41898282, "id": "bot-node",
            }}},
            "users/github-actions%5Bbot%5D": {
                "login": "github-actions[bot]", "id": 41898282, "node_id": "bot-node", "type": "Bot",
            },
            REPO: {"id": 1277929203},
            REPO + "/git/ref/tags/agent-status-revocation-definition-v2": {
                "ref": REF, "object": {"type": "commit", "sha": SHA},
            },
            REPO + "/actions/workflows/414090147": {
                "id": 414090147, "path": ".github/workflows/agent-status-revocation-test.yml",
                "state": "active",
            },
            REPO + "/actions/workflows/414090145": {
                "id": 414090145, "path": ".github/workflows/agent-status-revocation-ci.yml",
                "state": "active",
            },
        }
        self.requests = []

    def execute(self):
        def open_request(request, timeout):
            self.assertEqual(30, timeout)
            self.assertEqual("Bearer offline-workflow-token", request.get_header("Authorization"))
            path = request.full_url.removeprefix("https://api.github.com/")
            self.assertIn(path, self.responses)
            self.requests.append(path)
            if path == "graphql":
                self.assertEqual("POST", request.get_method())
                self.assertEqual(
                    {"query": "query { viewer { login databaseId id } }"},
                    json.loads(request.data),
                )
            else:
                self.assertEqual("GET", request.get_method())
                self.assertIsNone(request.data)
            response = self.responses[path]
            if isinstance(response, Exception):
                raise response
            return io.BytesIO(response if isinstance(response, bytes) else json.dumps(response).encode())

        class Opener:
            open = staticmethod(open_request)

        def build_opener(handler):
            self.assertIsNone(handler().redirect_request(None, None, 302, "", {}, "https://example.com"))
            return Opener()

        output = io.StringIO()
        try:
            with tempfile.TemporaryDirectory() as directory:
                summary = Path(directory) / "summary"
                with patch.dict(os.environ, self.environment | {"GITHUB_STEP_SUMMARY": str(summary)}, clear=True), \
                        patch("urllib.request.build_opener", side_effect=build_opener), \
                        contextlib.redirect_stdout(output):
                    exec(compile(SOURCE, str(WORKFLOW), "exec"), {})
                self.assertEqual(output.getvalue(), summary.read_text())
        finally:
            self.assertNotIn("offline-workflow-token", output.getvalue())

    def test_valid_diagnostic_reads_only_allowlisted_metadata_and_identity(self):
        self.execute()
        self.assertEqual(list(self.responses), self.requests)

    def test_configuration_mismatches_fail(self):
        for key in self.environment:
            if key == "WORKFLOW_TOKEN":
                continue
            with self.subTest(key=key):
                original = self.environment[key]
                self.environment[key] = "incorrect"
                try:
                    with self.assertRaisesRegex(SystemExit, "checks failed"):
                        self.execute()
                finally:
                    self.environment[key] = original

    def test_remote_identity_and_definition_mismatches_fail(self):
        original = copy.deepcopy(self.responses)
        mutations = [
            ("graphql", lambda item: item["data"]["viewer"].update(id="different-node")),
            ("users/github-actions%5Bbot%5D", lambda item: item.update(type="User")),
            ("users/github-actions%5Bbot%5D", lambda item: item.update(id=1)),
            ("users/github-actions%5Bbot%5D", lambda item: item.update(login="unexpected")),
            (REPO, lambda item: item.update(id=1)),
            (REPO + "/actions/workflows/414090147", lambda item: item.update(state="disabled_manually")),
            (REPO + "/actions/workflows/414090145", lambda item: item.update(path="unexpected")),
            (REPO + "/git/ref/tags/agent-status-revocation-definition-v2",
             lambda item: item["object"].update(sha="a" * 40)),
            (REPO + "/git/ref/tags/agent-status-revocation-definition-v2",
             lambda item: item["object"].update(type="tag")),
        ]
        for path, mutate in mutations:
            with self.subTest(path=path):
                self.responses = copy.deepcopy(original)
                mutate(self.responses[path])
                with self.assertRaisesRegex(SystemExit, "checks failed"):
                    self.execute()

    def test_invalid_authorization_windows_fail(self):
        now = datetime.datetime.now(datetime.timezone.utc)
        for expiration in (now - datetime.timedelta(hours=1), now + datetime.timedelta(hours=73),
                           now.replace(tzinfo=None)):
            with self.subTest(expiration=expiration):
                self.environment["EXPIRES_AT"] = expiration.isoformat()
                with self.assertRaisesRegex(SystemExit, "checks failed"):
                    self.execute()

    def test_missing_token_stops_before_api_calls(self):
        self.environment["WORKFLOW_TOKEN"] = ""
        with self.assertRaisesRegex(SystemExit, "credential is missing"):
            self.execute()
        self.assertEqual([], self.requests)

    def test_transport_and_invalid_responses_fail_without_raw_bodies(self):
        for response, message in [
            (urllib.error.HTTPError("https://api.github.com/graphql", 403, "private", {}, None), "HTTP status 403"),
            (urllib.error.URLError("private"), "transport error"),
            (b"private", "not valid JSON"),
            (b"[]", "not an object"),
            (b"x" * (2 * 1024 * 1024 + 1), "exceeds capacity"),
            ({"errors": [{"message": "private"}]}, "query returned errors"),
            ({"data": {"viewer": "private"}}, "response is incomplete"),
        ]:
            with self.subTest(message=message):
                self.responses["graphql"] = response
                with self.assertRaisesRegex(SystemExit, message):
                    self.execute()


if __name__ == "__main__":
    unittest.main()
