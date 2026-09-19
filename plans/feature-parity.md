# IssueAgent Feature-Parity Remediation

Current goal: implement every gap identified by the full review against `plans/issue-agent.md`, verify each acceptance boundary, and mark each item complete here only after executable evidence exists.

Status values: `[ ]` pending, `[~]` in progress, `[x]` complete. This checklist is subordinate to `plans/issue-agent.md`; it does not weaken or replace any requirement.

## Runtime orchestration
- [x] Implement production dependency-injection graph for providers, Git, OMP, context, workflows, notifications, observability, and scheduler.
- [x] Implement durable polling/scheduling loop with per-repository pagination, assignment eligibility, start dates, fairness, FIFO, priority commands, and bounded concurrency.
- [x] Wire provider identity discovery/override, repository allow-list, repository inheritance, and disabled/orphan handling.
- [x] Wire workflow execution for initial planning, replanning, implementation, revision, continuation, and cancellation.
- [x] Implement startup reconciliation and periodic reconciliation from provider truth and canonical comments.
- [x] Implement active-work registry and graceful cancellation/drain during shutdown.
- [x] Implement cleanup and retention behavior for worktrees, attachments, sessions, and unmanaged repositories.

## Configuration and startup safety

- [x] Complete effective configuration resolution and immutable process-lifetime configuration behavior.
- [x] Implement startup validation for workspace, OMP, provider identity, repository access, target branches, and SSH host-verification policy.
- [x] Ensure readiness is marked only after global startup validation and worker initialization.
- [x] Wire ConfigMap/Secret configuration and rollout checksums across Helm.
- [x] Register configurable OMP Auth Broker connectivity and runtime retry behavior.

## Provider parity

- [x] Complete GitHub/GitLab provider parity for pagination, rate limits, redirects, cancellation, malformed responses, common errors, labels, review threads, relationships, and attachments.
- [x] Add provider contract coverage for every required §36 scenario against both providers.
- [x] Ensure provider DTOs never leak into workflow APIs.

## Context and attachments

- [x] Wire all conversation surfaces and related-issue context into AgentContext.
- [x] Complete attachment download limits, omission reporting, provenance, persistence, and trusted-host credential policy.
- [x] Ensure planning reconciles comments/attachments arriving during OMP planning before publishing.
- [x] Ensure accidental planning worktree changes are discarded, logged, and metered.
- [x] Implement conservative corrupted canonical-state recovery and human escalation.

## Git, LFS, and trust boundaries

- [x] Complete cancellation propagation for Git and LFS operations.
- [x] Complete additional-CA handling for managed HTTP and LibGit2Sharp transports.
- [x] Complete direct-submodule (non-recursive) credential routing and cross-host credential isolation.
- [x] Prove workspace/path/symlink containment and malicious branch/issue-name safety.
- [x] Pin git-lfs to an explicit official version in the runtime image.

## OMP and workflow correctness

- [x] Wire one resumable OMP session across planning, implementation, and revisions.
- [x] Wire repository instructions, supplemental instructions, timeouts, and safe OMP environment configuration.
- [x] Complete material-deviation, conflict, push, PR/MR timeout, and restart recovery paths.
- [x] Complete canonical comment/label idempotency and command-consumption behavior through the host.
- [x] Complete notification routing and independent sink failure handling through live workflows.

## Observability

- [x] Make trace sampling configurable with the specified 100% default.
- [x] Emit required poll, provider, workflow, OMP, Git, LFS, notification, storage, and active-operation metrics.
- [x] Add explicit spans for reconciliation and all required operation boundaries.
- [x] Complete secret-safe structured logging and redaction tests for normal and exception paths.
- [x] Complete PrometheusRule alerts for down, stalled polling, provider, OMP, Git/LFS, workflow, and long-running operations.
- [x] Complete configurable discovery labels and useful Grafana dashboard panels.

## Deployment and release

- [x] Make the Dockerfile cache-optimized with locked restore and BuildKit caches.
- [x] Complete Compose examples for Telegram/Slack and OTLP, plus plain Docker run documentation.
- [x] Complete Helm ConfigMap/Secret/OTLP support, validation, and exact image/chart version behavior.
- [x] Publish matching Helm OCI charts from release tags.
- [x] Publish `latest` only from successful `main` builds and stable SemVer images from release tags.
- [~] Add CI stages for format/analyzers, production container, Compose smoke, kube schema, promtool, and Grafana validation. No distinct E2E CI stage exists; `ci.yml`'s "Unit and provider-contract tests" step runs the full solution (including E2E-style host tests) but is not a separately named E2E stage.

## Verification

- [x] Add deterministic scheduler/orchestration tests for priority, fairness, FIFO, restart, cancellation, and idempotency.
- [x] Add complete assignment-to-merge E2E coverage for GitHub and GitLab with fake OMP and real temporary Git/LFS where applicable.
- [x] Add crash/restart and boundary-failure injection coverage.
- [x] Add scripted NDJSON RPC startup/framing/session/cancellation integration coverage against this
      project's own best-effort protocol implementation (`tests/IssueAgent.Omp.Tests/fake_omp_server.py`);
      **not** verified against the real pinned OMP binary's actual wire protocol — see
      `OmpProcessClient.cs` for the documented residual risk.
- [x] Run the complete affected solution, packaging, deployment, and security validation commands.

## Progress evidence

- Locked restore passes: `dotnet restore IssueAgent.slnx --locked-mode`.
- Formatting and analyzers pass: `dotnet format IssueAgent.slnx --no-restore --verify-no-changes` and the Release build.
- Full solution tests pass: `dotnet test IssueAgent.slnx --configuration Release --no-restore` (252 passed, 0 failed, across 13 test projects).
- The dual-provider lifecycle harness covers plan, replan, implement, revise, merge, and canonical/PR idempotency for GitHub and GitLab repository identities.
- The production amd64 image builds with pinned OMP and Git LFS checksums and serves `/health/live` under a read-only root filesystem with a writable workspace volume.
- The default Compose stack builds, starts, and serves `/health/live`; its rendered configuration keeps provider credentials in mounted secret files.
- Helm lint and representative rendering pass with generated Secret, OTLP, monitoring, dashboard, and Auth Broker resources.
- Kubeconform accepts the rendered manifests; promtool reports success for all 7 rendered alert rules; the Grafana dashboard parses as JSON.
