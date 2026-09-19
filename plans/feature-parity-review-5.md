# IssueAgent feature-parity and invariant review — Round 5

Date: 2026-09-19

## Verdict

The fourth-pass commits improved coverage but are still not release-ready. The fifth audit found deployment-blocking OMP configuration/container defects, workflow recovery regressions, provider edge cases, and session/cancellation correctness gaps. The requested Sonnet reviewer remained rate-limited; this pass used the available Codex fallback reviewer.

## Findings

### High

1. **Escalated corrupt comments become undiscoverable** — §§17, 25–26. Corruption warnings are appended after the locator, but locator recognition now requires the marker to be the final text; the next poll can classify the issue as new and create a second workflow. Preserve or robustly recognize the unique locator.
2. **MR revise commands bypass reconciliation blockers** — §§19, 21, 25. A revise command can execute while dirty worktree, divergent remote, or label/state disagreement is still unresolved. Only cancellation/recovery commands may bypass a blocker.
3. **Remote-history recovery can rerun initial implementation** — §§23, 25–26. Review/revision phases are routed into `ImplementationWorkflow` after remote adoption, replaying the original plan instead of resuming review state.
4. **Replan OMP failure dereferences a missing result** — §§26–27. Failed/cancelled replan outcomes have null `Completed`, causing an exception after labels move to working without durable failure state.
5. **Anonymous provider identity override triggers authenticated `/user` lookup** — §§5, 10. Tokenless anonymous providers with explicit identity override still attempt provider identity discovery and fail before workflow execution.
6. **OMP config path points `PI_CONFIG_FILES` at a directory** — §§12, 34. The pinned runtime expects file paths and fails before emitting its ready frame in Helm/Compose/Docker.
7. **Bundled Auth Broker is not wired into IssueAgent** — §§12, 34. Broker-enabled manifests create a Service but do not inject its internal URL into the IssueAgent pod.
8. **Read-only production image cannot initialize OMP native state** — §§12, 32–36. The pinned image attempts to create native state under the read-only home filesystem and fails before RPC startup.
9. **Auth Broker deployment contract is a placeholder/non-runnable interface** — §§12, 33–35. Compose/Helm use an unpublished placeholder image and unsupported broker environment instead of a verified `omp auth-broker serve` contract.
10. **Pinned OMP session path is not persisted/resumed correctly** — §§12–13. A UUID/session path can silently create a fresh session, losing accumulated context after restart.
11. **Late same-session prompt failures are dropped** — §§12–13, 27. Events arriving after the prompt waiter completes are not surfaced as workflow failure.
12. **Configured OMP timeout is ignored** — §§12, 27. Runtime process operations do not consistently honor the configured timeout.
13. **OMP runs from `/data` rather than the retained workflow worktree** — §§13, 20–23. Model commands and commits can target the wrong filesystem context.
14. **Pre-prompt cancellation latch can be cleared** — §§24, 27. Cancellation arriving before prompt dispatch can be reset, allowing work to start after cancellation.
15. **GitHub attachment host registry is incomplete** — §§4, 15. Normal github.com user-attachment and githubusercontent asset hosts are not trusted in production wiring, causing valid attachments to be skipped or fetched with incorrect trust.
16. **GitHub comment update cancellation remains tokenless** — §§24, 27. Cancellation only stops the waiter while PATCH can complete later.
17. **GitHub label addition is incorrectly non-idempotent** — §§4, 27. A transient 5xx aborts an idempotent state transition unlike GitLab parity.
18. **Clone URL query/fragment credential rejection is incomplete** — §8. [Addressed in fourth remediation; retain regression coverage.] Fifth review confirmed this path now rejects credential-bearing query/fragment forms.

### Medium

19. Add inherited per-repository issue-closing configuration and pass it into PR/MR body generation (§21).
20. Pass review-thread `IsResolved` into `HumanComment`/revision prompt, not only the before/after digest (§21).
21. Independently detect conflicting PR/MR command labels and escalate ambiguity (§19, 21).
22. Make stale LibGit2Sharp worktree registrations idempotently repairable during recreation (§20, 26).
23. Enforce TLS Web Server Authentication EKU for additional CA verification (§9, 36).
24. GitLab provider identity fallback currently uses a GitHub noreply format (§10).
25. Configured OMP roles remain inert unless the supported configuration file/overlay contract is wired end-to-end (§12, 34).

## Verified

- Clean tree before audit and chunked remediation commits present.
- Prior full suite: 311 tests passed, 0 failed; optional real OMP smoke skipped without the binary.
- Focused fifth-pass checks reported: Context 40/40, Workflow 53/53, Host 15/15, OMP 16/16 plus one optional skip, real local OMP smoke 1/1, Observability 24, Git 36, Configuration 22, and provider suites 21/29/33.
- Helm lint/template, Compose config, kubeconform, deployment security, TLS/SSH, hook isolation, DNS-rebinding, metric, and redaction checks passed in the fallback audits.
- The fourth-pass anonymous identity, Telegram validation, clone URL, retry/backoff, pagination, and merged trust fixes remain present where rechecked.

## Residual uncertainty

The exact pinned production image was not available for a full read-only-container launch; the OMP config/native-state findings were established from the pinned runtime contract and available executable probes. Live provider timing and broker integration remain untested. The next remediation must prioritize the OMP/deployment contract before further workflow polish.
