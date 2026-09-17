# IssueAgent --- V1 Implementation Specification

**Status:** Implementation-ready scope\
**Target runtime:** .NET 10\
**Supported deployments:** Docker, Docker Compose, Kubernetes via Helm\
**CI:** GitHub Actions\
**Registry:** GHCR

## 1. Goal

IssueAgent is an autonomous issue-to-code workflow service for
explicitly configured GitHub and GitLab repositories. It polls for open
issues assigned to a configured identity, uses Oh My Pi (OMP) to inspect
the codebase and maintain an implementation plan, waits for explicit
human approval, then optionally implements the plan, pushes an agent
branch, creates a draft PR/MR, and supports repeated review/revision
cycles.

GitHub/GitLab are the durable workflow source of truth. V1 has **no
application database**.

Key principles:

-   assignment alone triggers planning;
-   human approval is required before implementation;
-   human interaction is required for material decisions/ambiguity;
-   OMP reasons, edits, tests and commits;
-   IssueAgent owns provider API mutations, Git credentials, pushes and
    PR/MR creation;
-   OMP does not receive provider/SSH/Kubernetes credentials unless
    explicitly configured as an OMP execution secret;
-   workflows are restart-safe and conservatively reconciled;
-   plans and implementation notes always capture important decisions
    and rationale;
-   provider-specific behavior stays behind abstractions.

## 2. Deployment/runtime architecture

The application itself has **zero Kubernetes dependencies**. The same
image supports:

-   plain `docker run`;
-   Docker Compose;
-   Kubernetes **via Helm only**.

Do not ship raw Kubernetes manifests.

Ship Docker/Compose examples and the Helm chart.

V1 uses exactly one IssueAgent instance. OMP jobs run in the IssueAgent
container with configurable expensive-work concurrency, default **3**.
Poll/API concurrency defaults to **10**. Poll interval defaults to **60
seconds**.

Persistent layout:

``` text
/data/
├── repos/       # bare canonical repositories
├── workflows/   # retained per-issue worktrees + attachments
└── omp/         # persistent OMP sessions
```

OMP Auth Broker is a separate service/container with its own persistent
auth storage.

A future V2 may use Kubernetes worker Jobs/Pods for OMP workloads. V1
must not add Kubernetes API/RBAC/distributed-locking support in
anticipation of this.

## 3. Technology choices

-   .NET 10 Generic Host, DI, standard
    `Microsoft.Extensions.Configuration`, strongly typed Options
    validation.
-   GitHub API: **Octokit.NET** behind `IGitProvider`.
-   GitLab API: internal typed `HttpClient` REST client; do not use
    NGitLab in production.
-   Git: **LibGit2Sharp** behind `IGitRepositoryManager`.
-   LFS: pinned official `git-lfs`.
-   OMP: pinned executable baked into Docker image; structured
    RPC/NDJSON over stdio behind `IOmpClient`.
-   JSON: `System.Text.Json`.
-   Logging: Serilog + Extensions.Hosting + Settings.Configuration +
    Console + Async sink.
-   Observability: OpenTelemetry metrics/traces/HTTP instrumentation.
-   Tests: xUnit v3 + WireMock.Net.

Use managed libraries for normal operations. External processes are
acceptable where required (OMP, git-lfs).

## 4. Provider abstraction

GitHub/GitLab DTOs must not leak into workflows. `IGitProvider` must
expose IssueAgent domain operations for:

-   current identity;
-   assigned issue discovery;
-   issue retrieval;
-   issue comments read/create/update;
-   labels read/add/remove/create;
-   PR/MR find/create/read;
-   PR/MR comments and review threads;
-   issue relationships;
-   attachment discovery/download.

Use a generic internal `MergeRequest` concept; GitHub maps it to Pull
Requests. Labels must work on issues and PR/MRs.

## 5. Identity, repository allow-list and eligibility

Authenticated providers auto-discover current identity by default, with
optional override. Anonymous provider API access is supported, but an
enabled anonymous provider requires explicit identity override because
assignment discovery needs an identity.

The configured identity only needs to be among multiple assignees.
IssueAgent never changes assignees.

Configured repositories are a strict allow-list and are polled
individually.

Assignment alone triggers planning when:

-   issue is open;
-   repository is configured/enabled;
-   configured identity is an assignee;
-   issue **creation time** is on/after `startDate`;
-   existing IssueAgent state does not already make it ineligible.

`startDate` has a global default with optional per-repository override.

## 6. Polling and scheduling

Defaults:

``` yaml
pollInterval: 60s
concurrency:
  agent: 3
  polling: 10
```

Requirements:

-   polling continues while OMP jobs run;
-   control/reconciliation work does not consume OMP slots;
-   human-triggered work (`replan`, `implement`, `revise`, `continue`)
    outranks new planning;
-   round-robin fairness across repositories within a priority class;
-   FIFO within each repository;
-   paginate all eligible issues; no discovery cap;
-   honor `Retry-After` and provider rate-limit/reset information;
-   proactively back off discovery where reliable rate-limit data
    warrants it.

## 7. Configuration inheritance

Providers define default GitHub `owner` / GitLab `namespace`;
repositories inherit and may override. Derive normal clone URLs
automatically, with explicit URL override as an escape hatch.

Repositories are enabled by default. `enabled: false` means no polling,
reconciliation or cleanup. Repositories removed from config become
unmanaged/orphaned local data and are never automatically deleted.

Configuration is immutable for process lifetime. Container restart is
required for config/secret changes. Helm should use ConfigMap/Secret
checksum annotations to trigger rollouts.

## 8. Secrets and Git authentication

Every secret supports exactly one source:

``` yaml
token:
  env: GITHUB_TOKEN
```

or:

``` yaml
token:
  file: /run/secrets/github-token
```

Apply to provider/Git tokens, SSH keys/passphrases, Telegram token,
Slack webhook and explicit OMP execution secrets. Resolve once at
startup; rotation requires restart.

API auth and Git transport auth are independent. Git modes:

-   `provider-token` --- reuse provider API token over HTTPS;
-   `token` --- separate HTTPS token;
-   `ssh`;
-   `anonymous`.

Never embed credentials in Git URLs; use LibGit2Sharp callbacks.

Full mode may be configured with anonymous/read-only Git. Do not reject
solely because a future push may fail; handle it when encountered.

## 9. TLS and SSH trust

TLS modes:

-   `system` (default);
-   system + additional CA files;
-   `pinned` with one/more SHA-256 certificate fingerprints;
-   explicit `none`.

Additional CAs must be loaded in-process for .NET HTTP and libgit2,
augmenting system trust without root/system-store modification.
Centralize TLS semantics.

SSH V1 does not implement full OpenSSH `known_hosts`. Supported:

``` yaml
ssh:
  hostVerification: pinned
  fingerprints:
    - sha256:...
```

or explicit `none`.

SSH selected without explicit host verification is a **fatal
configuration error**.

## 10. Git workspace and branches

Canonical repos are persistent **bare** repos under
`/data/repos/{repository-id}`, cloned lazily on first workflow use.

Each issue gets:

``` text
/data/workflows/{workflowId}/
├── worktree/
└── attachments/
```

One retained worktree follows the entire workflow. OMP starts with the
worktree as current directory. Attachments stay outside it to prevent
accidental commits.

Target branch defaults to provider default branch with optional repo
override. Planning records the exact immutable target commit SHA.

Default branch:

``` text
agent/issue-{number}-{slug}
```

Derive safe slug normally. For long/unsuitable titles, ask OMP for a
concise meaningful slug during planning, then sanitize/validate it.

Git author and committer default to provider identity, with
global/per-repo override. Same identity for both. No commit signing. No
fork-based workflow.

## 11. Hooks, attributes, submodules and LFS

Repository Git hooks must **never execute**. Presence is Debug-only.
Tests must prove sentinel hooks do not run.

Honor committed `.gitattributes` and `.gitmodules`. Do not intentionally
inherit ambient host/global Git config.

Support recursive submodules:

-   same configured provider host → appropriate parent/provider
    credentials;
-   another configured provider host → that provider's
    credentials/trust;
-   unknown host → never forward credentials; anonymous may be
    attempted;
-   auth required but unavailable → human intervention.

Include pinned official `git-lfs`. Never rely on
`git lfs install`/hooks.

Explicitly materialize LFS content. Publishing LFS changes:

1.  identify/upload required LFS objects;
2.  verify success;
3.  push Git ref.

Failed LFS upload means publication failed. Orphaned LFS objects are
acceptable if subsequent Git push fails. Fail preflight when LFS is
required but unavailable. No automatic LFS pruning in V1.

## 12. OMP Auth Broker and configuration

Use OMP Auth Broker as a separate service with persistent auth state.
Under Helm it is a separate Deployment + ClusterIP + PVC. Under
Compose/Docker it is a separate service/container.

This preserves OAuth-backed OpenAI Codex/Anthropic subscription
authentication.

Document initial login using `kubectl port-forward` for Helm and
localhost-only exposure for Docker/Compose. Existing OAuth may be
migrated where OMP supports it.

OMP config is mounted read-only. Under Helm use generated/existing
ConfigMap; under Docker/Compose use bind mount/config.

OMP mutable sessions live `/data/omp`.

IssueAgent selects semantic roles:

``` yaml
omp:
  roles:
    planning: plan
    implementation: task
    revision: task
    conflictResolution: task
```

Actual models live in OMP config. Honor OMP-native repository
instructions such as `AGENTS.md`, plus optional global/per-repo
supplemental instructions.

OMP version is pinned/baked into the IssueAgent image; Helm does not
separately select it.

## 13. OMP RPC/session/security

Use OMP RPC over stdio/NDJSON. `IOmpClient` owns process lifecycle, RPC
framing, role selection, structured events, session create/resume,
cancellation and errors.

One OMP session follows an issue through planning, replanning,
implementation, revision and conflict resolution. Capture session ID
early and persist remotely.

No timeout by default; configurable.

OMP receives an explicit allow-listed environment, not the entire
IssueAgent environment. Allow required OMP/Auth Broker settings,
PATH/HOME/temp, `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`, TZ/LANG/LC\_\*
and configured execution variables.

Never expose provider/Git/SSH/Kubernetes credentials unless explicitly
configured as OMP execution secrets.

OMP/build/test commands have normal outbound network access.

Support global/per-repo OMP execution environment. Secret values use the
normal env/file secret source. Configuring an OMP execution secret
explicitly means the user trusts OMP/repository tooling with it.

## 14. Context model

Build a structured internal context bundle rather than ad-hoc prompts.
It should contain:

``` text
AgentContext
├── PrimaryIssue
│   ├── title/description/labels
│   ├── human comments
│   └── attachments
├── RelatedIssues
│   └── relationships + issue context
├── CurrentPlan
│   ├── revision
│   ├── plan text
│   └── decisions/rationale
├── PullOrMergeRequest
│   ├── description
│   ├── comments
│   ├── review threads
│   └── attachments
└── WorkflowState
```

Prompt builders for planning, implementation, revision and conflict
resolution select the relevant portions.

### Related issues

Follow/read provider-native relationships for additional context where
supported, normalizing concepts such as:

-   related;
-   blocks;
-   blocked-by;
-   parent;
-   child;
-   duplicate/duplicated-by.

Related issues are **read-only context**. Never mutate their
labels/comments/state merely because they are related.

Traverse relationships to **one hop by default**, configurable globally
with per-repo override. Detect cycles. Read related issue title,
description, relevant human comments, labels and attachments subject to
normal attachment limits/security.

## 15. Attachments

Support attachments from **all human conversation surfaces**:

-   issue description;
-   issue comments;
-   PR/MR description;
-   PR/MR general comments;
-   inline review comments/threads;
-   related issues followed for context.

Download provider-native attachments and obvious direct-file HTTP/HTTPS
links. Do not crawl ordinary webpages.

Provider credentials may only be sent to trusted provider-owned
attachment endpoints. External hosts receive no provider
credentials/cookies.

Defaults, configurable globally + repo override:

-   25 MB per attachment;
-   100 MB total per primary workflow context.

Allow all file types; sanitize filenames/path traversal. Do not
auto-extract archives. Images are normal attachments.

Retain provenance metadata so OMP knows which issue/comment/review
thread each attachment came from.

Oversized attachments are omitted without failing the workflow; tell OMP
about omissions.

Attachments persist until workflow cleanup.

No automatic secret redaction of user-provided issue/comment/attachment
content. IssueAgent must never inject its own credentials into that
context.

## 16. Planning contract

Planning receives:

-   primary issue title/description;
-   ordinary labels;
-   all human comments chronologically;
-   relevant attachments;
-   related/blocked/etc issue context;
-   repository-native OMP instructions;
-   configured supplemental instructions.

Bot comments are ignored by default, configurable globally with per-repo
override.

OMP may run commands while planning but must not intentionally modify
repository files. Any accidental changes are discarded and
logged/metric'd.

If new comments/attachments arrive while planning, re-read/reconcile
them with OMP **before publishing the plan**.

Plans should be thorough but not artificially hard-truncated.

### Required plan content

The plan must always communicate important decisions and their
rationale. It should normally contain:

-   summary/understanding;
-   proposed changes;
-   affected components/files where known;
-   implementation sequence;
-   **key decisions and rationale**;
-   alternatives considered/rejected where material;
-   testing strategy;
-   risks/considerations;
-   assumptions/open questions.

Do not invent meaningless headings merely to satisfy formatting, but
never omit material decision rationale.

## 17. Canonical IssueAgent comment

Each managed issue has **one canonical IssueAgent comment**. Do not
create a new plan comment for every replan.

The comment is created when the workflow starts and updated in place.

It contains:

1.  recognizable IssueAgent header;
2.  current authoritative implementation plan;
3.  later, implementation result/notes and PR/MR link;
4.  a collapsible/readable machine-state section containing YAML;
5.  a small hidden locator marker such as `<!-- issue-agent:state -->`.

Example conceptual layout:

``` markdown
**IssueAgent — managed automatically**

## Implementation plan
...

### Key decisions and rationale
...

## Implementation result
...
<!-- only once implementation exists -->

<details>
<summary>Agent state</summary>

```yaml
version: 1
workflowId: ...
phase: planned
state: waiting
waitingReason: plan-approval
planRevision: 3
session: ...
branch: ...
targetBranch: main
baseCommit: ...
updatedAt: ...
```

```{=html}
</details>
```
```{=html}
<!-- issue-agent:state -->
```

Replanning **edits the plan section in this same comment**. `planRevision` increments.

Human comments remain chronological and provide the discussion/history explaining why the current plan changed.

Implementation does not erase the plan; implementation notes are added/updated in the same canonical comment.

Human edits to this managed comment/YAML are not authoritative control input. If externally corrupted, reconstruct conservatively or require human intervention.

## 18. Iterative planning

Planning is explicitly conversational and may loop indefinitely:

1. IssueAgent publishes/updates plan.
2. Human comments with requested changes.
3. Human adds replan command label.
4. IssueAgent resumes the same OMP session with the current plan plus relevant conversation/history.
5. OMP revises the plan.
6. IssueAgent updates the canonical comment in place.
7. Human may repeat this process any number of times.

Do not impose an arbitrary replan limit.

When replanning, OMP must understand prior feedback and preserve/revise decisions intentionally rather than blindly generating an unrelated new plan.

## 19. Three-dimensional label protocol

Do **not** require exactly one `agent:*` label overall.

Split labels into three dimensions.

### Phase labels

Exactly one phase label for a managed workflow:

- `agent:phase:planning`
- `agent:phase:planned`
- `agent:phase:implementing`
- `agent:phase:review`
- `agent:phase:revising`
- `agent:phase:done`
- `agent:phase:failed`
- `agent:phase:cancelled`

Phase describes the durable workflow milestone/artifact.

### State labels

Exactly one operational state while actively managed:

- `agent:state:working`
- `agent:state:waiting`

`waiting` is orthogonal to phase. Examples:

```text
agent:phase:planned
agent:state:waiting
```

means a plan exists and human input/approval is required.

```text
agent:phase:review
agent:state:waiting
```

means implementation/PR exists and human review is required.

Detailed waiting reason belongs in YAML state, not additional labels.

### Command labels

Human control commands:

-   `agent:cmd:replan`
-   `agent:cmd:implement`
-   `agent:cmd:revise`
-   `agent:cmd:continue`
-   `agent:cmd:cancel`

Commands are transient. IssueAgent consumes/removes them once accepted.

Normally zero or one command label should exist. Multiple conflicting
command labels are ambiguous and must not be guessed; remain/go waiting
and notify.

Phase/state labels are owned by IssueAgent. Humans normally interact via
command labels and comments.

IssueAgent creates missing labels automatically with canonical
descriptions/colors but does not overwrite user-customized metadata of
existing labels.

## 20. Main workflow

### Initial planning

1.  eligible assignment discovered;
2.  create WorkflowId, canonical comment and OMP session early;
3.  set:
    -   `agent:phase:planning`
    -   `agent:state:working`;
4.  resolve exact target SHA;
5.  prepare retained worktree, submodules, LFS and context;
6.  OMP plans;
7.  reconcile any new human input that arrived during planning;
8.  update canonical comment with plan;
9.  set:
    -   `agent:phase:planned`
    -   `agent:state:waiting`;
10. notify human that plan is ready.

### Replanning

Human comments normally, then adds `agent:cmd:replan`.

IssueAgent consumes command, changes to planning/working, resumes same
OMP session with complete relevant planning conversation, revises the
existing plan, increments plan revision, edits canonical comment, then
returns to planned/waiting and notifies.

This loop may repeat indefinitely.

### Implementation approval

Human adds `agent:cmd:implement`.

In `plan-only` mode, consume/reject it, restore planned/waiting, explain
and notify.

In full mode:

1.  consume command;
2.  set implementing/working;
3.  verify/reset retained worktree to clean planned base;
4.  OMP implements/tests/commits;
5.  if intended changes remain uncommitted, give one corrective OMP
    pass;
6.  re-fetch target;
7.  before first publication, rebase if target advanced;
8.  give OMP conflict-resolution pass if required;
9.  upload required LFS objects;
10. IssueAgent pushes;
11. create draft PR/MR;
12. update canonical issue comment with implementation result + link;
13. set review/waiting;
14. notify human.

### Implementation decision/rationale contract

OMP implementation result must include:

-   implementation summary;
-   key changes;
-   **key decisions and rationale**;
-   tests/checks run and results;
-   known/pre-existing failures;
-   deviations from approved plan and rationale;
-   risks/remaining considerations.

Minor implementation-detail deviations are allowed if documented.

A **material architectural/scope deviation** from the approved plan must
not be silently made. Pause for human interaction, explain the
discovery/recommended change and rationale, update state, notify, and
wait for an explicit command.

## 21. PR/MR review and repeated revision

PR/MR title defaults to issue title.

OMP generates the human-facing PR/MR body. IssueAgent adds machine
marker and provider-native issue-closing/reference syntax. Closing
syntax is enabled by default with per-repo disable.

PR/MR is always created **draft**. IssueAgent never automatically
promotes it to ready.

PR/MR review is also an unlimited conversation loop:

1.  human leaves normal general/inline review comments, including
    attachments where relevant;
2.  human adds `agent:cmd:revise` to PR/MR;
3.  IssueAgent consumes command;
4.  set revising/working;
5.  collect all new human review feedback since previous pass, with
    resolved/unresolved state where provider exposes it;
6.  download review-comment attachments;
7.  resume same OMP session;
8.  OMP revises, tests and commits;
9.  IssueAgent pushes;
10. return to review/waiting;
11. notify human.

No special comment syntax is required.

Do not impose a revision-loop limit.

Bot comments are ignored by default according to global/per-repo
bot-comment setting.

Do not monitor CI/check status in V1. Human may paste/comment relevant
CI feedback and request revision.

## 22. New input and stale plans

Title/description edits after planning invalidate the plan.

Ordinary human comments do not automatically invalidate it; the human
uses `agent:cmd:replan` when the comment changes requirements.

Ordinary non-agent label changes are context only.

If implementation is requested while title/description has changed since
the approved plan, do not implement; remain/return planned/waiting,
explain replan is required and notify.

New comments/attachments arriving **during implementation before push**
cause a human gate:

-   pause;
-   keep current worktree/commits;
-   set appropriate phase + waiting;
-   comment/notify;
-   allow human to choose continue, replan or cancel.

During PR review, edits to original issue are surfaced but do not
automatically rewrite the implementation. Human explicitly chooses
revise/replan.

## 23. Published branch history/conflicts

Before first publication, rebase onto latest target is allowed.

After the branch is published, **never force-push**.

During an explicit later revision, if target changes cause conflicts,
merge latest target into the agent branch and let OMP resolve/commit.

Human commits pushed to the agent branch are preserved and OMP works on
top of them.

If external history is force-pushed/reset unexpectedly so local retained
history no longer matches remote:

-   do not overwrite either side;
-   move to waiting;
-   explain and notify;
-   `agent:cmd:continue` may accept remote history as authoritative and
    rebuild/resume local workspace.

## 24. Completion/cancellation

Merge:

-   phase → done;
-   local worktree removed;
-   local agent branch removed;
-   remote branch left alone;
-   issue closure follows provider-native closing syntax.

PR/MR closed without merge:

-   phase → cancelled;
-   local cleanup;
-   remote branch left alone.

`agent:cmd:cancel` is valid during active work. Consume command, request
OMP cancellation, preserve/reconcile state safely, transition cancelled
and clean up when safe.

IssueAgent never deletes remote branches and never changes assignees.

## 25. Human-intervention invariant

Whenever IssueAgent cannot safely progress without a human:

1.  preserve all useful local state;
2.  set `agent:state:waiting`;
3.  retain the most informative phase;
4.  record precise waiting reason in YAML;
5.  post/update a concise human explanation where appropriate;
6.  emit configured workflow notification.

`agent:cmd:continue` is the generic acknowledgement/resume command when
the human has reviewed the blocker and wants the agent to proceed.

Examples include:

-   material plan deviation;
-   new human input during implementation;
-   unresolved conflict;
-   missing submodule/LFS credentials;
-   unexpected remote branch rewrite;
-   ambiguous/corrupt workflow state;
-   protected branch/push restrictions.

## 26. Durable state and recovery

The canonical IssueAgent comment contains human-readable YAML state.
Include at least:

``` yaml
version: 1
workflowId: ...
phase: planned
state: waiting
waitingReason: plan-approval
planRevision: 3
approvedPlanRevision: 3
ompSessionId: ...
branch: ...
targetBranch: main
baseCommit: ...
pullOrMergeRequest: ...
updatedAt: ...
```

Exact schema may grow but must be versioned.

Use a stable WorkflowId for the entire lifecycle and a fresh
CorrelationId for each discrete operation/attempt.

On restart, reconcile:

-   provider labels/state comment;
-   PR/MR marker/state;
-   remote branch;
-   retained worktree;
-   Git status/history;
-   persisted OMP session.

Resume automatically only when state is safe/unambiguous. Otherwise
wait + notify.

Waiting, failed and review worktrees are retained indefinitely by
default.

Periodic cleanup compares local workspaces against live remote state.
Delete only confidently completed/cancelled worktrees/local branches.
Uncertainty means keep.

## 27. Retry and graceful shutdown

One global configurable retry policy.

Default:

-   3 attempts;
-   exponential backoff;
-   jitter;
-   honor explicit provider retry/rate-limit instructions.

After exhausted transient failures, use failed phase where appropriate,
explain and notify.

Notification failures never change workflow success/failure.

Graceful shutdown:

-   immediately stop new polls/jobs;
-   active work gets **15 seconds default**, configurable;
-   then request bounded OMP cancellation;
-   preserve durable/recoverable state;
-   exit.

Helm `terminationGracePeriodSeconds` should default slightly above app
grace (e.g. +10s), overrideable.

## 28. Notifications

Create generic fan-out notification abstraction.

V1 sinks:

-   Telegram Bot API;
-   Slack incoming webhook.

Both may be enabled simultaneously.

Per-event routing can select one or both. If routing is unspecified,
send to **all enabled sinks**.

Notification events include at least:

-   plan ready / awaiting review;
-   implementation/review ready;
-   human action required;
-   plan/implementation/revision failure;
-   cancellation where useful.

Each sink is independent. Failure in one does not prevent another or
block workflow transition. Apply retry policy and emit logs/metrics.

Workflow notifications are distinct from operational alerts.

## 29. Logging

Use Serilog with Console behind Async sink and
`Serilog.Settings.Configuration`.

Configuration must work from config files and normal .NET env overrides.

Use structured logging. Context should include where applicable:

-   Provider;
-   Repository;
-   IssueNumber;
-   PullRequestNumber/MergeRequestNumber;
-   WorkflowId;
-   CorrelationId;
-   OmpSessionId;
-   Operation;
-   Component;
-   OmpEventType.

This must make it easy to query all OMP logs for one issue/workflow in
Loki/Grafana.

Information level: lifecycle/tool metadata.

Debug/Verbose: full OMP prompts/responses/tool/command output.

Never log secrets, Authorization headers, authenticated URLs, SSH keys,
broker tokens or complete child environments. Test secret leakage on
normal and exception paths.

## 30. OpenTelemetry, Prometheus, health and Grafana

Instrument once with OpenTelemetry.

Use OTel Prometheus exporter for `/metrics`; do not maintain separate
prometheus-net instrumentation.

Optional OTLP export for metrics/traces.

Default trace sampling: **100%**, configurable.

Each discrete operation gets its own trace; WorkflowId correlates traces
across days/human review.

Instrument outgoing HttpClient without sensitive data and explicit spans
for:

-   Git fetch/worktree/submodules/push;
-   LFS;
-   OMP plan/implement/revise;
-   reconciliation.

Prometheus labels may include bounded provider/repository. Never use
issue/PR/session/workflow IDs or error messages as metric labels.

Metrics should cover:

-   poll count/errors/duration;
-   last successful poll;
-   issues discovered;
-   plans/errors/duration;
-   implementations/errors/duration;
-   provider requests/errors/duration;
-   OMP requests/errors/duration;
-   Git operations/errors/duration;
-   LFS operations/errors;
-   active operations/age;
-   notification failures;
-   storage/workspace signals where practical.

Endpoints, default `:8080`:

-   `/health`
-   `/health/live`
-   `/health/ready`
-   `/metrics`

Readiness means the worker initialized and can operate; individual
provider/repo outages do not make the pod unready or cause restart
loops.

No endpoint auth V1. Helm exposes ClusterIP only; no Ingress by default.

Ship:

-   ServiceMonitor;
-   PrometheusRule;
-   Grafana dashboard JSON;
-   optional Grafana sidecar ConfigMap.

PrometheusRule should include configurable alerts for:

-   IssueAgent down;
-   polling stalled;
-   repeated poll/provider failures;
-   OMP failures;
-   Git/LFS failures;
-   workflow failures;
-   stuck/long operations.

Existing Alertmanager routing handles operational notifications such as
Telegram. Discovery labels for Prometheus/Grafana sidecars must be
configurable.

## 31. Startup validation

Fatal application-wide errors include:

-   invalid configuration syntax/semantics;
-   SSH auth without explicit host-verification policy;
-   unusable required workspace;
-   missing/incompatible required local tooling/config.

Validate enabled repositories non-destructively at startup:

-   provider authentication/identity where applicable;
-   repository existence/access;
-   target branch resolution.

Do **not** inspect/infer token scopes or create dummy resources.

An inaccessible individual repository/provider is non-fatal to the whole
process; expose through logs/metrics.

Temporary Auth Broker/model/provider connectivity is a runtime retry
condition, not necessarily fatal installation failure.

## 32. Docker image

Ship cache-optimized multi-stage .NET 10 Dockerfile.

Build ordering:

1.  copy solution/project/NuGet lock files;
2.  restore using BuildKit cache;
3.  copy source;
4.  publish `--no-restore`;
5.  minimal runtime image.

Runtime image contains:

-   IssueAgent;
-   pinned OMP;
-   pinned git-lfs;
-   LibGit2Sharp native requirements;
-   CA support required by runtime.

Support linux/amd64 and linux/arm64.

Run non-root.

Default hardened runtime:

-   `allowPrivilegeEscalation: false` under Kubernetes;
-   drop Linux capabilities;
-   seccomp RuntimeDefault;
-   read-only root filesystem where feasible;
-   explicit writable `/data`/temp mounts.

No Kubernetes service-account token/RBAC.

No default CPU/memory limits. Docker/Compose/Helm users may configure
resource constraints.

## 33. Docker and Docker Compose

Ship documented examples.

Minimal Compose should show:

-   IssueAgent image;
-   persistent `/data`;
-   IssueAgent config;
-   OMP config;
-   env-file/secret sourcing;
-   health/metrics port optionally bound to localhost.

Full Compose should additionally show:

-   OMP Auth Broker;
-   persistent broker storage;
-   localhost-only broker setup exposure where needed;
-   Telegram/Slack examples;
-   OTLP configuration examples.

Plain Docker documentation should provide equivalent `docker run`
examples.

The same application configuration model must work across Docker,
Compose and Helm. Kubernetes ConfigMaps/Secrets/PVCs are deployment
mechanisms, not application concepts.

## 34. Helm

Helm is the **only supported Kubernetes deployment method**.

Chart supports:

-   IssueAgent Deployment, exactly one replica;
-   optional/bundled Auth Broker Deployment + Service;
-   generated/existing IssueAgent ConfigMap;
-   generated/existing OMP ConfigMap;
-   generated/existing Secrets;
-   workspace PVC or `existingClaim`;
-   Auth Broker PVC or `existingClaim`;
-   Service;
-   probes;
-   security contexts;
-   optional resource requests/limits;
-   ServiceMonitor;
-   PrometheusRule;
-   Grafana dashboard ConfigMap;
-   OTel/OTLP values.

Workspace PVC defaults:

-   8 GiB;
-   ReadWriteOnce;
-   configurable size/accessModes/storageClass/existingClaim.

Auth Broker PVC defaults:

-   1 GiB;
-   configurable similarly.

No default CPU/memory limits.

Released chart version exactly matches application SemVer and defaults
to matching exact image tag, but `image.tag` is overrideable.

## 35. CI and releases

GitHub Actions only.

Stable SemVer releases only; no prerelease versions.

Tag `v1.2.3` publishes:

-   multi-arch GHCR image `:1.2.3`;
-   matching Helm OCI chart `1.2.3`.

Do not publish floating `:1` or `:1.2`.

Successful `main` builds publish `:latest`.

`latest` explicitly means latest successful main build, **not latest
stable release**.

Pin external tooling and use locked NuGet restores. Automated
dependency-update PRs run full CI.

CI stages include:

-   format/analyzers/build;
-   unit tests;
-   provider contract tests;
-   Git integration tests;
-   process/integration tests;
-   E2E tests;
-   production container tests;
-   Docker Compose validation/smoke tests;
-   Helm validation.

Do not deploy the Helm chart into a temporary Kubernetes cluster in CI.

Helm checks should include `helm lint`, representative `helm template`
combinations, schema validation/kubeconform where applicable, `promtool`
rule checks and Grafana JSON validation.

## 36. Comprehensive testing requirements

### Unit

Test configuration validation/inheritance, SecretSource, label/state
transitions, canonical comment parsing/updating, plan revisions, context
construction, prompt contracts, branch naming, TLS/SSH fingerprints,
retry decisions and cleanup decisions.

### Provider contract

Run equivalent behavioral tests against GitHubProvider and
GitLabProvider using WireMock.Net.

Cover:

-   issues;
-   comments;
-   comment updates;
-   phase/state/cmd labels;
-   PR/MR labels;
-   PR/MR creation;
-   review threads;
-   related issues;
-   attachments;
-   pagination;
-   auth;
-   rate limits;
-   malformed responses;
-   cancellation;
-   redirects;
-   common error codes.

### Git integration

Use real temporary repos/LibGit2Sharp. Cover bare clones, fetch,
worktrees, branches, commits, push, HTTPS token, SSH, TLS modes, host
pinning, recursive submodules, dirty state, concurrent worktrees and
cleanup.

Install malicious/sentinel hooks and assert they never execute.

### LFS

Use real git-lfs with temporary repos. Test materialization,
modified/new LFS files, explicit LFS upload-before-ref-push behavior and
failures.

### OMP

Normal workflow tests use deterministic fake `IOmpClient`/fake RPC
process. Separate integration tests exercise real OMP RPC
startup/framing/session/cancellation without expensive model usage where
possible.

### End-to-end

Run essentially complete IssueAgent with:

-   WireMock GitHub/GitLab;
-   real temporary Git repos;
-   real LibGit2Sharp;
-   real git-lfs where relevant;
-   deterministic fake OMP.

Test full assignment → plan → repeated replan → implement → draft PR/MR
→ repeated revise → merge lifecycle.

Inject failures at every boundary, including crash/restart after
checkpoints, push succeeded/PR creation timed out, OMP crash, provider
5xx/rate-limit, submodule/LFS failures, new comments mid-operation and
corrupted state metadata.

Idempotency requirement: rerunning/restarting must not duplicate
canonical comments, branches or PR/MRs.

### Security

Test:

-   provider/SSH/Kubernetes secrets absent from OMP environment;
-   secret redaction in logs/exceptions;
-   cross-host submodule/LFS credential leakage prevention;
-   TLS/SSH fail-closed behavior;
-   missing SSH host policy fatal;
-   path traversal/symlink/attachment filename safety;
-   malicious issue/branch names;
-   Git hooks never execute.

## 37. Explicit V1 non-goals

-   distributed Kubernetes worker pods/Jobs;
-   multiple IssueAgent replicas/distributed locking;
-   database/Hangfire;
-   raw Kubernetes manifests;
-   fork-based PR/MR workflow;
-   signed commits;
-   Git hook execution;
-   full OpenSSH known_hosts parsing;
-   automatic CI-status monitoring/fix loops;
-   automatic archive extraction;
-   automatic LFS pruning;
-   automatic remote branch deletion;
-   automatic draft-to-ready transition;
-   application-managed backups.

## 38. Core invariant

GitHub/GitLab hold durable workflow truth. The retained worktree and OMP
session provide resumable execution state. IssueAgent performs
privileged mutations; OMP performs reasoning/code work. The current plan
is maintained in one canonical IssueAgent comment, human discussion
remains chronological, and repeated planning/review loops are
first-class. Any ambiguous or materially consequential decision that
cannot be safely automated pauses for explicit human interaction rather
than being guessed.
