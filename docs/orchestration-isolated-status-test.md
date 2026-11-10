# Isolated required-status recovery test proposal

Status: design for review, not an executable test or approval to deploy/run.

The test exercises a real coding task, exact-head managed CI, publication of
`Agent managed CI: success`, and command-only pause/replay. It must not reuse
the production state, Issue #227/#237, PR #232/#240, or the existing protected
CI tag. Passing this test does not authorize recovery of #237.

## Current evidence and blockers

- The reviewed recovery change is commit
  `91891bc768846d7c5b74eba1646f1a59d7273f3b`.
- The existing orchestration regression suite passes 726 tests, including
  published-success pause, missing downstream approvals, lost responses,
  cancellation, lookup-only replay, publisher/head rejection, and CAS conflicts.
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
triggers. Proposed names below are subject to maintainer review; none have
been created.

| Resource | Purpose |
| --- | --- |
| Fresh Issue | One documentation-only coding request; not #227 or #237. |
| `agent-status-revocation-base-v1` | Dedicated PR base; never merge it into `master`. |
| Fresh draft PR | Created by the real coding task against the dedicated base; never merge. |
| `refs/heads/agent-state-status-revocation-v1` | Separate durable CAS state, explicitly bootstrapped once. |
| `refs/tags/agent-status-revocation-definition-v1` | Immutable reviewed driver and CI definition. |
| `agent-status-revocation-test.yml` | Manual, staged driver with no schedule/event wakes. |
| `agent-status-revocation-ci.yml` | Exact-SHA CI; manual dispatch by the staged driver only. |
| `agent-status-revocation-test` environment | Separate protected approvals, secrets, variables, and restricted refs. |
| Exact-base protection rule | Require `Agent managed CI`, independent human approval, and no bypass. |

Protection applies only to the dedicated base, not `master`. Do not attest
`RequiredContextVerified` until the maintainer verifies the saved test-base
rule. If that cannot be established, stop before green publication; do not
weaken the production publisher gate or set a false attestation.

The driver needs the existing separate task-user and workflow credentials.
Task creation uses the verified user identity. Status publication uses the
authenticated workflow publisher, verified again in each invocation. Grant only
the API capabilities needed by each job; the CI job needs contents read, no task
credential, no state writes, and no status writes. Never execute tested-head
code in the driver job containing mutation credentials.

Same-repository workflow credentials are not inherently restricted to one Git
ref. Namespace isolation must therefore be enforced by the implementation and
reviewed API guards, not claimed as a GitHub token permission boundary.

## Implementation required before provisioning

These are explicit changes to implement and review, not switches already
available in the current CLI:

1. Add a dedicated test entry point/profile, disabled by default, requiring a
   reviewed manifest containing repository ID, Issue, dedicated base, state
   ref, immutable definition/CI tag and SHA, workflow IDs, environment identity,
   expiration, and explicit task/CI/cost/status approvals. Reject production
   identities, missing values, expired approvals, and manifest changes on replay.
2. Introduce explicit isolated-ref support without broadening the production
   default. `GitHubGitTransport.RefPath` currently only permits `agent-state`;
   passing a different reference to `GitStateStore` alone is not sufficient.
   The test transport must reject every production-ref read/write and never
   fall back to `agent-state`. State initialization is a separate operator
   action; a missing/deleted ref after initialization is an error.
3. Add a pinned test CI route. The CI and required-status adapters currently
   verify `.github/workflows/agent-managed-ci.yml`; the new route must persist
   and validate the isolated workflow identity end-to-end, including dispatch,
   receipt recovery, run/job evidence, and success publication. Preserve the
   production default and reject mismatched or mutable definitions.
4. Verify manual command ingress against the isolated driver workflow ID,
   path, pinned definition, repository, environment, human actor, and live
   write/maintain/admin permission. Do not impersonate an `issues` notification,
   substitute a trusted source run, or fabricate an authorized Ready event.
5. Add staged effect selection that allows one coding launch, then one CI
   dispatch, then status publication. After green CI, stop at `REVIEW_PENDING`
   and prioritize the eligible success status without executing the queued
   review intent. Never invoke the unrestricted worker and rely on a throwing
   review adapter as a safety barrier: that would create ambiguous work and
   invalidate green eligibility.
6. Enforce one Issue, one coding task/round, zero repair tasks, one CI dispatch,
   zero review requests, zero merges, and a reviewed expiration/time budget.
   Persist limits across invocations and replay. Existing
   `TEST_MAX_REPAIR_TASKS=1` is not a substitute for this zero-repair policy.
7. Reuse real task mapping/terminal/candidate-SHA evidence and the existing
   exact-head CI and status adapters. Do not seed a completed task, green CI,
   dispatch receipt, review acceptance, or saved digest from a fixture.
8. Reuse command-only pause/revocation and lookup-only replay. Record status
   creation boundaries, receipts, and outcomes in the isolated CAS store.
   A failed or uncertain stage must retain its evidence and stop.
9. Keep an append-only, credential-free result artifact with manifest digest,
   state revisions, IDs, SHA, publisher, and ordering observations. Save
   diagnostic state even when a stage fails; do not export tokens or arbitrary
   HTTP bodies. Bound artifact retention and preserve canonical evidence.

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

GitHub statuses and canonical-state CAS are not transactional. A late older
success may arrive after an error; IDs/timestamps and repeated independent
reads establish observed ordering, not a proof of permanent or atomic
revocation. Preserve the hold regardless of receipt delivery, and require
independent exact-SHA/context verification before any later reactivation.

Completion means the full acceptance matrix is supported by saved live
evidence. Local regressions, green Actions alone, or a direct REST status probe
must not be reported as a successful end-to-end recovery test. Production
deployment and recovery of #237 remain separate decisions.
