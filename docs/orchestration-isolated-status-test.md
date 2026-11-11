# Isolated required-status recovery test

Status: implementation was merged through PR #241 at
`b3df1399a222d695f80c71f97c01b2d2b10f378a`. The isolated base, definition tag,
environment and protection rules have been provisioned, with Issue #242 as
the fixture. Isolated state has not been bootstrapped and the live test has
not been authorized or run. Activation/bootstrap remain false; the diagnostic
described below is a separate proposed workflow, not live verification.

The test exercises a real coding task, exact-head managed CI, publication of
`Agent managed CI: success`, and command-only pause/replay. It must not reuse
the production state, Issue #227/#237, PR #232/#240, or the existing protected
CI tag. Passing this test does not authorize recovery of #237.

## Current evidence and blockers

- The reviewed recovery change is commit
  `91891bc768846d7c5b74eba1646f1a59d7273f3b`.
- Before the isolated profile was added, the orchestration regression suite
  passed 726 tests, including
  published-success pause, missing downstream approvals, lost responses,
  cancellation, lookup-only replay, publisher/head rejection, and CAS conflicts.
- Current offline validation passes the full solution suite: 2,163 passed,
  one skipped, zero failed. No live workflow was run.
- Read-only inspection found zero commit statuses on the held test head
  `f52d2a10b237dff7c29cfcac3151a387a40b0396`. Its green Actions run is not
  evidence of required-status delivery or success/error ordering.
- Historical workflow logs show `Statuses: write`; that does not prove a future
  token's effective permissions or publication identity.
- The maintainer's branch-protection screenshot shows a rule targeting `master`,
  one required approval, no selected required checks, and bypass prevention
  unchecked. This is not a verified production required-context configuration;
  other rulesets and the saved configuration still need maintainer inspection.
- Both existing execution workflows remain disabled. Their variables, tags,
  definition pins, pending invocations, and production state must not change.

## Proposed resources and permissions

Use the same repository, with a separate test namespace and no automatic
triggers. The table records the original resource plan; provisioning does not
authorize the remaining state bootstrap, task or draft PR creation.

| Resource | Purpose |
| --- | --- |
| Fresh Issue | One documentation-only coding request; not #227 or #237. |
| `agent-status-revocation-base-v1` | Dedicated PR base; never merge it into `master`. |
| Fresh draft PR | Created by the real coding task against the dedicated base; never merge. |
| `refs/heads/agent-state-status-revocation-v1` | Separate durable CAS state, explicitly bootstrapped once. |
| `refs/tags/agent-status-revocation-definition-v1` | Immutable reviewed driver and CI definition. |
| `agent-status-revocation-test.yml` | Manual, staged driver with no schedule/event wakes. |
| `agent-status-revocation-ci.yml` | Exact-SHA CI; manual dispatch by the staged driver only. |
| `agent-status-revocation-test` environment | Separate protected approvals/secrets and restricted refs; workflow-gate `vars` must be repository-scoped so job conditions can evaluate them. |
| Exact-base protection rule | Require `Agent managed CI`, independent human approval, and no bypass. |

Protection applies only to the dedicated base, not `master`. Do not attest
`RequiredContextVerified` until the maintainer verifies the saved test-base
rule. If that cannot be established, stop before green publication; do not
weaken the production publisher gate or set a false attestation.

The driver needs the existing separate task-user and workflow credentials.
Task creation uses the verified user identity. Status publication uses the
authenticated workflow publisher, verified again in each invocation. Grant only
the API capabilities needed by each job: the driver requires `actions: write`
to dispatch the isolated CI workflow; the CI job needs contents read, no task
credential, no state writes, and no status writes. Never execute tested-head
code in the driver job containing mutation credentials.

Same-repository workflow credentials are not inherently restricted to one Git
ref. Namespace isolation must therefore be enforced by the implementation and
reviewed API guards, not claimed as a GitHub token permission boundary.

## Implemented offline profile

- A separate CLI mode, settings manifest, state ref and guarded state store;
  production defaults and `agent-state` remain unchanged. Bootstrap is an
  explicit manual operation and refuses an already initialized state ref.
- Dedicated staged driver and exact-head CI workflows, each gated by a
  separate environment and exact workflow definition SHA. The driver accepts
  only the configured human actor and exact Issue; live permission checks are
  repeated by the CLI. Because both workflows share one immutable tag, their
  configured definition SHAs must match; the driver also resolves that tag
  before bootstrap or execution and rejects a moved tag.
- A pinned isolated CI route, draft-PR requirement, single-pass task guard,
  no-repair/no-review reducer mode, effect allowlist, and three-execution cap
  for coding, CI and success-status publication. Pending/failure required-status
  intents remain canonical evidence but are not published by the isolated worker.
  Continuation uses the same allowlists: a held CI failure stops immediately,
  and a completed-pass replay settles after one observation iteration rather
  than polling excluded projections or waiting for disabled review work.
  Running coding tasks and unfinished CI runs still require observation.
- Pause calls the existing command-only status-revocation path, requiring a
  previously saved successful status on the exact head. Replay uses canonical
  state and existing status lookup; it does not repeat a completed POST.
  An overlapping pause performs lookup only for an executing correction. A
  missing receipt leaves that intent executing rather than marking it unknown
  and preventing its owner from saving the publication boundary.
- Each driver invocation writes credential-free success or failure evidence
  for its stage. Status history includes payload, SHA, publisher boundary,
  existing status IDs, prepared/completed times and returned status ID; failed
  stages include a sanitized summary of the last readable canonical state.
  HTTP clients remain available until failure diagnostics finish, including
  after API timeouts; diagnostics only read saved state and never retry a
  prepared mutation. Clients are then disposed even if diagnostics fail.
  The workflow artifact retention is seven days.
- Offline regressions cover settings/manifest constraints, isolated state
  limits, ingress, alternate state refs, CI routing and the worker effect cap.
  These checks do not prove live identities, permissions, protection, or
  GitHub status ordering.

## Remaining before provisioning

1. Complete offline validation, review the entire diff, and merge the
   implementation before creating any resources. Any fixes from this review
   require another full offline pass.
2. Independently inspect the workflow pins, environment approvals, task-user
   entitlement, status publisher identity/effective permissions, state-ref
   protection, and exact-base required-context protection. Keep resource
   creation, secrets, protection changes, enabling and live execution as
   separate approvals.
3. Make the isolated state ref non-deletable under the intended protection
   model and disable `ORCHESTRATION_STATUS_TEST_BOOTSTRAP_APPROVED` immediately
   after the one-time bootstrap. The CLI blocks re-bootstrap when state exists;
   deleting that ref and re-enabling bootstrap could otherwise create a new
   empty state. Missing state during normal execution is always an error.
4. Preserve artifacts and canonical state through the agreed retention window;
   do not export tokens or arbitrary HTTP bodies.
5. Keep the existing independent operator verification and cost-control steps
   below. No local test result authorizes provisioning or a live attempt.

Before any live run, offline regressions must reject cross-scope Issue/PR/base/
SHA/state/workflow/tag identities, production-store access, mutated manifests,
deleted state, repeat launches, extra CI dispatches, repair/review execution,
expired authorization, nonhuman commands, unverified status gates, lost
responses, cancellation, and CAS races. Production tests must remain unchanged
in behavior. New workflow definitions need syntax/shell validation too.

## Provisioning after implementation review

1. Review the complete implementation and immutable definition SHA. Creating
   resources, installing secrets, changing protection, and running work are
   separate approvals; this document authorizes none of them.
2. Create the dedicated base, exact-base protection, protected environment,
   immutable executable tag, fresh Issue, and empty isolated state. Populate
   the manifest with actual IDs. Never copy production lifecycle history.
3. Confirm both production workflows are still disabled and no pending
   production invocation will be approved/re-run. Read and record the
   production state tip as a non-mutating baseline.
4. Independently verify the task user's repository permissions/entitlement,
   workflow publisher identity, effective job permissions, immutable-tag
   protection, and test-base required context. Verify no added AI/Actions
   charges under the existing cost controls; do not infer this from a flag.
5. Obtain approval for exactly one bounded attempt and its time/cost limits.
   Only then authorize the isolated workflow/environment. Do not change
   `ORCHESTRATION_ACTIVATION_APPROVED` or existing CI/test variables.

## Live stages and acceptance criteria

### Non-mutating publisher diagnostic

`agent-status-test-diagnostics.yml` is a separate manual diagnostic on `master`.
It reuses the configured isolated resource names and definition pins; it does not
need a new environment, secret, tag or variable. Merge/review and permission to
dispatch it are separate decisions. It requires the configured human actor,
the same triggering actor (so another collaborator cannot re-run an authorized
invocation), and
explicit `false` values for `ORCHESTRATION_STATUS_TEST_APPROVED`,
`ORCHESTRATION_STATUS_TEST_BOOTSTRAP_APPROVED` and
`ORCHESTRATION_STATUS_TEST_STATUS_PUBLISHER_VERIFIED`.

The job reads repository/workflow/tag metadata and queries the workflow token's
GraphQL viewer, comparing its numeric and node identities to `github-actions[bot]`.
Only boolean comparisons are reported. It has no checkout, user credential or
environment access and never dispatches CI/tasks, writes statuses/state, or
updates configuration. Its token is limited to contents read and Actions read;
it has no `statuses` permission and cannot write statuses. This diagnostic
therefore verifies identity/configuration only, not status-write permission.

A green result proves the reported comparisons for that diagnostic invocation,
not the future driver's permissions, user-token identity, Agent tasks entitlement,
protection configuration or absence of charges. Existing `*_VERIFIED` variables
are operator attestations, not measurements. Keep activation/bootstrap false.
Do not set `ORCHESTRATION_STATUS_TEST_STATUS_PUBLISHER_VERIFIED` based on this
diagnostic. Before any separately approved live dispatch, review the driver's
repository-wide `statuses: write` boundary and protected-environment gate, then
inspect the effective `GITHUB_TOKEN Permissions` in that exact approved run.
This diagnostic cannot establish the future driver's effective grant. Never
publish a probe status or move the immutable definition tag to make this
diagnostic pass.

Run the diagnostic script's offline HTTP/configuration regressions with
`python3 -B -m unittest discover -s .github/scripts/tests -p 'test_status_test_diagnostics.py'`.
They execute the actual inline workflow script with mocked HTTP responses;
they never connect to GitHub or require a token.

| Stage | Action | Required evidence before continuing |
| --- | --- | --- |
| Coding | Dispatch one authorized documentation-only task. | One canonical task ID and remote ID; one draft PR on the dedicated base; real terminal success and candidate/final-SHA evidence; no repairs. |
| CI | Dispatch once from the immutable isolated tag for the completed task SHA. | One recorded dispatch boundary/run ID; attempt 1; correct repository/workflow/definition; exact-SHA checkout; completed restore/build/test steps and green result. |
| Success | Publish through the real status adapter at eligible `REVIEW_PENDING`. | Real CI/task proofs; matching live PR head; expected publisher ID/node/type; one durable receipt and visible `Agent managed CI: success` on that SHA; zero review POSTs. |
| Pause | Manually invoke authorized command-only pause with downstream review/CI approvals absent for this invocation. | Pause durably saved first; exactly one corrective `error` on the same SHA/context; saved boundary/receipt; no worker admission, coding, CI, review, labels, or comments. |
| Replay | Replay the saved pause identity/content through verified ingress. | No additional status POST, task launch, CI dispatch, or review request; pause/hold remain. A new workflow run ID is a new command, not the same inbox replay. |
| Verification | Independently read the exact-head status history and latest context. | Success and error IDs, publisher, payloads, and timestamps match saved receipts; the latest status for this publisher/context is the corrective error. |
| Teardown | Disable the isolated mechanism and archive evidence. | Test PR remains draft/unmerged; preserve state/receipts; production ref unchanged and both existing workflows disabled. No automatic deletions or cleanup of uncertain work. |

The initial test must not deliberately lose a live response or create an
ambiguous earlier success. Failure-injection remains offline unless separately
approved. On a genuine lost response, stop and use lookup-only receipt recovery;
absence from a lookup is not permission to repeat a POST.

CI finishes on a task-created SHA that is not known in advance. Bind that SHA
durably from verified terminal evidence, recheck it before each subsequent
stage, and stop if it changes. Do not accept an arbitrary SHA supplied on a
later dispatch.

## Stop conditions and limits of the result

Any unexpected identity, permission, scope, head, feedback, extra task/run,
ambiguous effect, missing receipt, or unconfirmed error stops the test. Do not
clear admission, reactivate, recreate missing state, or launch a replacement.
Further recovery requires a separate operator decision.

The manifest expiration also bounds bootstrap and the running driver through
cancellation, not only startup validation. Expiration stops further driver
effects and preserves uncertain operations for inspection; it does not cancel
an already launched remote task or CI run, nor roll back an accepted status
POST. Failure diagnostics remain read-only after expiration. An expired
manifest does not authorize a new pause or recovery invocation: a separate
operator decision is required, not an automatic expiry extension or manifest
replacement.

GitHub statuses and canonical-state CAS are not transactional. A late older
success may arrive after an error; IDs/timestamps and repeated independent
reads establish observed ordering, not a proof of permanent or atomic
revocation. Preserve the hold regardless of receipt delivery, and require
independent exact-SHA/context verification before any later reactivation.

Completion means the full acceptance matrix is supported by saved live
evidence. Local regressions, green Actions alone, or a direct REST status probe
must not be reported as a successful end-to-end recovery test. Production
deployment and recovery of #237 remain separate decisions.
