# Feature-Parity Execution Plan

This plan sequences the remaining work in `plans/feature-parity.md`. The authoritative behavior remains `plans/issue-agent.md`. Mark an item `[x]` only after its implementation and named verification gate pass.

## Completion rule

A block is complete only when:

1. behavior is wired through the production host rather than only exposed by an interface or unit;
2. GitHub and GitLab receive equivalent provider-neutral behavior where applicable;
3. restart, cancellation, ambiguity, retry, and credential-isolation paths are exercised;
4. focused tests and the full affected validation suite pass;
5. the corresponding entries in both this plan and `plans/feature-parity.md` are marked `[x]`.

## Block 1 — Runtime orchestration and recovery

This is the next implementation block. It removes the current direct `poll → execute inline` path and establishes the durable production control plane.

### Scheduler contract

- [x] Split repository polling from expensive workflow execution. Polling discovers and classifies candidates; workers execute admitted work independently so polling continues while OMP runs.
- [x] Introduce a provider-neutral candidate type containing provider, repository, issue number, command priority, discovery order, and reconciliation metadata.
- [x] Classify existing canonical workflows and command labels before admission. Human commands (`cancel`, `continue`, `replan`, `implement`, `revise`) outrank new planning.
- [x] Integrate `FairWorkAdmission` into the production scheduler. Preserve FIFO within each repository and round-robin repositories within each priority class.
- [x] Add queued/in-flight deduplication keyed by provider/repository/issue. A later poll must not enqueue or start a duplicate attempt.
- [x] Bound only OMP/workflow execution with `Concurrency.Agent`; keep polling/reconciliation bounded separately by `Concurrency.Polling`.
- [x] Drain all provider pages with no discovery cap and admit every eligible issue.
- [x] Isolate one issue/repository failure without stopping other repositories or future polls.

### Durable reconciliation

- [x] Replace the current canonical-comment-only shortcut with a reconciliation service that reads labels, canonical YAML, PR/MR state, remote branch, retained worktree/head/status, and OMP session state.
- [x] Invoke `ReconciliationDecider` from the production path before resuming an existing workflow.
- [x] Resume automatically only for a consistent waiting state and accepted command. A persisted `working` state after restart must wait for human intervention.
- [x] On ambiguous labels or corrupt state, preserve local data, set waiting state with the precise reason, update the canonical comment, and notify.
- [x] Reconcile push-success/PR-timeout by finding an existing branch/PR/MR before creating another resource.
- [x] Detect merged and closed-without-merge PR/MRs and run the appropriate done/cancelled local cleanup without deleting remote branches.
- [x] Periodically clean only confidently completed/cancelled workspaces; retain waiting, failed, review, and uncertain workspaces.

### Shutdown and cancellation

- [x] Stop admission and new polling immediately on shutdown.
- [x] Allow active work to drain for `ShutdownGracePeriod`; request bounded OMP cancellation only after that grace expires.
- [x] Track each active OMP session so explicit `agent:cmd:cancel` can cancel the correct in-flight run and reconcile durable state before cleanup.
- [x] Dispose OMP processes, HTTP clients, and cancellation sources deterministically.

### Block 1 tests and gates

- [x] Add deterministic scheduler tests for command priority, FIFO, repository round-robin, concurrency separation, deduplication, and failure isolation.
- [x] Add restart tests for safe waiting state, interrupted working state, ambiguous labels, corrupt canonical YAML, remote history rewrite, merged PR/MR, and closed PR/MR.
- [x] Add cancellation tests for grace-period completion, grace expiry followed by OMP cancellation, and explicit cancel command.
- [x] Run `dotnet test tests/IssueAgent.Host.Tests/IssueAgent.Host.Tests.csproj --configuration Release`.
- [x] Run workflow, context, provider, Git, OMP, notification, and observability affected suites.
- [x] Run `dotnet test IssueAgent.slnx --configuration Release`.

## Block 2 — Configuration, providers, context, and attachments

- [x] Add one effective-configuration resolver covering global/provider/repository inheritance, clone URL derivation, overrides, start dates, bot-comment policy, related depth, attachment limits, workflow mode, instructions, Git trust, and OMP execution settings.
- [x] Validate fatal global prerequisites before readiness and isolate individual provider/repository connectivity failures.
- [x] Complete GitHub/GitLab contract parity for pagination, rate limits, redirects, cancellation, malformed responses, common status codes, labels, PR/MR review threads, relationships, and attachments.
- [x] Add one shared provider contract suite executed against both implementations.
- [x] Ensure every human conversation surface reaches `AgentContext`: issue body/comments, PR/MR body/comments, review threads, and related issues.
- [x] Preserve attachment provenance, enforce per-file/total limits, omit oversized files without failing, and never send provider credentials to external hosts.
- [x] Re-read comments and attachments arriving during planning before publishing.
- [x] Discard accidental planning worktree changes and emit a structured log and metric.
- [x] Implement conservative canonical-state corruption recovery and human escalation.

## Block 3 — Git, LFS, trust, OMP, and workflow recovery

- [x] Propagate cancellation through LibGit2Sharp boundaries and every Git/LFS child process.
- [x] Complete system-plus-additional-CA and pinned-certificate handling for managed HTTP and LibGit2Sharp.
- [x] Route direct-submodule (non-recursive) credentials by trusted host; unknown hosts receive no credentials.
- [x] Enforce workspace containment, symlink safety, branch-name safety, and hook non-execution.
- [x] Pin the official `git-lfs` version and verify its release checksum for amd64/arm64.
- [x] Keep one resumable OMP session through plan, replan, implement, conflict resolution, and revise.
- [x] Wire repository-native and supplemental instructions, configured timeout, role selection, broker settings, and explicit execution secrets.
- [x] Complete material-deviation, stale-input, conflict, push, PR/MR timeout, and external-history-rewrite recovery.
- [x] Prove canonical comment, branch, and PR/MR idempotency across retries and restarts.
- [x] Add scripted-subprocess OMP RPC startup, framing, session resume, timeout, and cancellation
      integration tests against this project's own protocol implementation (not the real pinned OMP
      binary — see `OmpProcessClient.cs` for the documented residual risk).

## Block 4 — Observability, deployment, and release

- [x] Decorate production provider, Git, LFS, OMP, workflow, notification, and reconciliation boundaries with metrics and spans.
- [x] Add active-operation age and storage/workspace signals with bounded labels.
- [x] Add log/exception-path secret-redaction tests for provider tokens, authenticated URLs, SSH keys, broker tokens, and child environments.
- [x] Complete configurable Prometheus/Grafana discovery labels and validate every alert expression and dashboard JSON.
- [x] Finish generated/existing Helm ConfigMaps and Secrets, OTLP values, checksums, exact chart/image versions, and representative renders.
- [x] Pin every external runtime tool, including `git-lfs`, and require locked NuGet restore.
- [x] Add CI gates for provider contracts, Git/LFS integration, scripted-subprocess OMP RPC framing (not
      the real pinned OMP binary — see `OmpProcessClient.cs` for the documented residual risk), host/E2E,
      production container smoke, Compose smoke, kubeconform, promtool, and Grafana validation.
- [x] Keep stable tags publishing exact SemVer image/chart pairs and `main` publishing only `latest`.

## Block 5 — End-to-end acceptance and closeout

- [x] Build a deterministic host harness using WireMock GitHub/GitLab, real temporary Git repositories, LibGit2Sharp, real git-lfs where applicable, and fake OMP.
- [x] Exercise assignment → plan → repeated replan → implementation → draft PR/MR → repeated revise → merge for both providers.
- [x] Inject failure/restart after each durable checkpoint, provider 5xx/rate-limit, OMP crash, push-success/PR-timeout, new input mid-operation, submodule/LFS failure, and corrupt metadata.
- [x] Prove reruns never duplicate canonical comments, branches, or PR/MRs.
- [x] Run format, analyzers, build, full tests, Docker checks, production container smoke, Compose smoke, Helm lint/templates/schema, promtool, and Grafana JSON validation.
- [x] Remove obsolete scaffolding and update `plans/feature-parity.md` and this plan to `[x]` only from current command evidence.

## Immediate file ownership for Block 1

- `src/IssueAgent.Host/PollingScheduler.cs`: discovery only; no inline expensive work.
- `src/IssueAgent.Host/FairWorkQueue.cs`: production priority/fairness admission and deduplication contract.
- `src/IssueAgent.Host/WorkflowDispatcher.cs`: one admitted item → reconcile → execute exactly one safe action.
- `src/IssueAgent.Host/ActiveWorkRegistry.cs`: active attempt/session tracking, grace, and cancellation.
- `src/IssueAgent.Host/Worker.cs`: startup, scheduler lifetime, shutdown ordering.
- `src/IssueAgent.Workflow/ReconciliationDecider.cs`: pure recovery decision rules only.
- `tests/IssueAgent.Host.Tests`: scheduler, restart, shutdown, and idempotency behavior.
- `tests/IssueAgent.Workflow.Tests`: pure reconciliation and state-transition behavior.

## Block 1 implementation order

1. Define candidate, deduplication, and active-attempt contracts.
2. Refactor polling into discovery/classification without OMP execution.
3. Integrate fair admission and independent worker drain.
4. Add production reconciliation service and command dispatch.
5. Correct shutdown grace/cancellation ordering.
6. Add focused scheduler/restart/cancellation tests.
7. Run affected suites, then the full solution.
8. Mark only proven Block 1 checklist items complete.
