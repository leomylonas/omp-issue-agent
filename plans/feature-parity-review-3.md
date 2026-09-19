# IssueAgent feature-parity and invariant review — Round 3

Date: 2026-09-19

## Verdict

The round-3 audit found substantial remaining noncompliance despite the round-2 remediation. The most severe gaps affect the OMP execution contract, durable workflow recovery, cancellation/retry semantics, credential isolation, and canonical-state parsing. The repository builds, and prior focused tests remain green, but those tests do not cover the failure boundaries listed below. This audit used the available Codex reviewer fallback because the requested Sonnet review capacity returned HTTP 429 rate-limit responses.

## Findings

### Critical / High

1. **OMP is launched in the wrong mode and without durable session storage** — §§12–13, 38. `WorkflowDispatcher.PrepareRuntimeAsync` passes no RPC/session arguments; the deployed OMP defaults to text mode and a non-persistent home session path. Planning cannot reliably exchange the expected NDJSON RPC protocol or preserve one session per issue. Pass the pinned CLI's RPC and `/data/omp` session arguments and add a real binary smoke test.
2. **Canonical YAML parsing binds to the first fenced YAML block** — §§17, 26. A plan's YAML example can be parsed as workflow state, causing false corruption. Scope parsing to the generated state details block.
3. **Canonical plan parsing truncates ordinary H2/H3 headings** — §§16–18. `PlanSectionPattern` treats every H2/H3 as a delimiter and can delete approved plan content on checkpoint. Delimit only on exact generated sections.
4. **Initial planning is not checkpointed before local/OMP work** — §§20, 26. A crash after worktree/session creation but before canonical publication is rediscovered as new planning, creating duplicate workflow/session state. Persist `planning/working` first.
5. **Replanning does not reconcile all input before publication** — §16. Edited comments, attachments, title/description changes, and comments arriving during replan can be omitted and later treated as baseline. Snapshot complete input and gate publication on a successful final reconciliation.
6. **Material-deviation continuation resumes without an OMP implementation turn** — §§20, 25. `continue` can push unchanged work and open a review request after accepting a deviation. Run a dedicated implementation/test/commit turn.
7. **Push-success/PR-creation interruption is not recovered** — §§26, 36. Missing PR/MR causes reset and reimplementation even when the remote branch is already correct. Recreate/find the request from the durable branch.
8. **Recovery and cancel commands are blocked by reconciliation** — §§23–25. Dispatcher returns before reading commands when reconciliation is not `ResumeAllowed`; remote-history `continue` and dirty-worktree `cancel` cannot operate.
9. **Fast-forward human commits are treated as history rewrites** — §23. Any unequal heads become `RemoteHistoryRewrite`; legitimate human commits cannot be integrated for revision. Distinguish ancestry from rewrites.
10. **Revision commands are read from the issue instead of the PR/MR** — §21. `agent:cmd:revise` on the review request is never observed.
11. **Failure states are not persisted before label mutation** — §§26–27. Labels/notifications change while canonical state remains implementing/working, so reconciliation can erase failure state. Persist failed/waiting state first.
12. **PR/MR body is always empty** — §21. `FindOrCreateMergeRequestAsync` passes `string.Empty`; no rationale, marker, or provider-native issue reference/closing syntax is generated.
13. **Review feedback can be skipped during revision** — §21. The cursor advances past comments arriving during OMP revision. Re-read immediately before publication and gate on new feedback.
14. **Discovery has a fixed 200-page ceiling** — §6. GitLab discovery aborts after 20,000 issues, contradicting unbounded eligible-issue pagination. Retain repeated-URL protection without a fixed cap.
15. **GitHub secondary-rate-limit delay is ignored** — §§6, 27. The helper waits 100–199 ms instead of honoring `Retry-After`/conservative secondary-limit backoff.
16. **GitHub transient 5xx reads are not retried** — §27. `ExecuteWithRetryAsync` catches rate-limit exceptions only; idempotent reads fail on one 500/502/503.
17. **Octokit cancellation does not cancel the underlying request** — §§17, 25, 27. `WaitAsync` cancels only the caller wait while remote mutations may continue after cancellation.
18. **Mode-specific Git credentials are not startup-validated** — §§8, 31. Token/provider-token/SSH modes can resolve null credentials and fail only during clone/fetch.
19. **Kubernetes service-account tokens are automounted** — §32 and credential-isolation requirements. Set `automountServiceAccountToken: false` on application and broker pods.
20. **OMP configuration mounted by Helm is not forwarded to the child** — §§12, 34. `OMP_CONFIG_DIR` is absent from the allow-listed child environment.
21. **SSH private-key passphrases resolve but always fail at runtime** — §8. `GitSshTransport` throws `NotSupportedException`; either implement narrowly scoped passphrase support or reject it at startup.
22. **Released Helm chart defaults to a placeholder image** — §34. Default repository is `ghcr.io/example/issue-agent`, not the image published by release workflow.
23. **Configured Git identity does not govern OMP commits** — §10. Identity is used for LibGit2Sharp paths but not OMP environment/worktree commits.
24. **OMP shutdown cancellation can exceed the host deadline** — §27. `CancelAsync` receives the full OMP timeout after the grace period, exceeding configured shutdown headroom.

### Medium

25. Clamp out-of-range rate-limit reset timestamps before `FromUnixTimeSeconds`; otherwise server-controlled values can throw before the delay cap (§§6, 27).
26. Apply provider-derived identity instead of `IssueAgent <issue-agent@localhost>` when no override exists (§10).
27. Exclude normal `OperationCanceledException` from dispatcher plan/implementation error counters (§30).
28. Count terminal OMP error events in `issueagent.omp.errors`, not only transport exceptions (§30).
29. Measure provider requests at HTTP/page boundaries rather than each buffered enumerable item (§30).
30. Preserve dispatch correlation fields across child-span logs; current enrichment reads only current-activity tags (§29–30).
31. Attach generated workflow ID to initial-planning traces (§30).
32. Validate `Notifications.Tls` pinned fingerprints/CA paths at startup (§9, 31).
33. Compare GitHub/GitLab assignee identities case-insensitively (§5).
34. Use canonical owner/name plus project identity for GitLab relationship cycle detection (§14).
35. Add an explicit generated OMP ConfigMap path in Helm; current application ConfigMap is not OMP configuration (§12, 34).
36. Existing-resource checksum fallback hashes only the resource name during offline/GitOps rendering, so config/secret updates may not roll pods (§7).
37. Pin the git-lfs version/artifact in CI rather than using mutable apt repositories (§32, 35).
38. Compose does not expose the broker's localhost-only initial setup surface (§12, 33).
39. Reject credentials embedded in provider/clone URLs (§8, 13).
40. Deduplicate inline reconciliation against already admitted in-flight work (§6, 26).
41. Normal cancellation can still trigger failure metrics through dispatcher generic catches (§27, 30).
42. Notification TLS validation gap is present in both configuration audit paths and needs one shared validator (§9, 31).
43. Missing `planInputHash` is treated as fresh instead of corrupt/replan-required (§22).
44. Duplicate canonical locators escape reconciliation corruption handling and can repeat forever (§17, 25).
45. Provider-side failure and cancellation paths need explicit contract tests for all affected operations (§27).

## Verified satisfied

- Round-2 Octokit primary REST client TLS wiring is present.
- Telegram URI construction uses a leading slash and handles colon-bearing bot tokens.
- GitLab cross-project IDs are API-consumable for direct fetches.
- Shared attachment byte/count budgeting and redirect rejection are present.
- DNS-rebinding mitigation now passes validated address sets through a connection callback; existing Git/GitHub/GitLab attachment tests cover the supplied-address and mismatch cases.
- Repository ID forwarding is present across the audited Git manager paths.
- Helm alert/dashboard metric names, compose secret-file mapping, CI permissions, and digest-pinned Docker syntax fixes are present.
- Existing focused test suites and solution build were green before this read-only audit.

## Checks

- `dotnet build IssueAgent.slnx --configuration Release --no-restore -nodeReuse:false -m:1` — succeeded, 0 warnings, 0 errors.
- Prior focused suites from the remediation pass: 13 projects, 286 tests, 0 failures.
- Fallback deployment review: `helm lint`, `helm template`, and `docker compose config` completed successfully, but rendered output exposed the placeholder image, absent generated OMP config, and missing service-account opt-out.
- Sonnet review dispatches were attempted twice and rejected before execution with HTTP 429 (`retry-after-ms=13081000`).

## Residual uncertainty

The audit is source- and targeted-check based. No real pinned OMP binary RPC smoke was available, and no Docker image build was run. The findings above should be fixed in severity order, beginning with OMP launch/session protocol and durable workflow checkpoints, then credential/TLS validation and provider retry/cancellation behavior.

## Remediation status

The findings were actioned in four ownership batches:

- Providers/configuration: pagination loop protection, bounded retry parsing, idempotent Octokit retries, cancellation where Octokit exposes a token, credential/TLS startup validation, URL credential rejection, provider assignee casing, and GitLab identity handling.
- Context/workflow: canonical parser boundaries, durable planning checkpoints, complete input reconciliation, deviation continuation, branch/PR recovery, MR command routing/body generation, review cursors, plan-hash validation, failure persistence, and ancestry-aware reconciliation.
- Host/observability/Git: RPC/session launch arguments, OMP configuration and Git identity environment forwarding, bounded shutdown, cancellation metrics, OMP error metrics, provider-boundary request metrics, ancestor log enrichment, reconciliation deduplication, command-aware recovery, duplicate-locator handling, and Git ancestry support.
- Deployment/security: released image defaults, generated/external OMP ConfigMap support, read-only mounts, service-account token opt-out, deterministic external checksum inputs, pinned git-lfs installation, localhost broker setup, and deployment artifact tests.

Post-remediation verification:

- `dotnet build IssueAgent.slnx --configuration Release --no-restore -nodeReuse:false -m:1` — succeeded, 0 warnings, 0 errors.
- `dotnet test IssueAgent.slnx --configuration Release --no-build --no-restore --nologo` — 13 projects, 301 tests passed, 0 failed.
- `helm lint deploy/helm/issue-agent` — passed.
- `helm template issue-agent deploy/helm/issue-agent` — passed.
- `docker compose -f deploy/docker-compose.yml config` — passed.
- `git diff --check` — passed.
- Deployment artifact security tests — 4 passed.

One boundary remains intentionally constrained by the Octokit API: high-level paginated calls do not expose cancellation tokens; token propagation was added everywhere the underlying `Connection` API supports it. A real pinned-OMP binary RPC smoke remains unavailable in this environment.
