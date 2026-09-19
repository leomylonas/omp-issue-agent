# IssueAgent feature-parity and invariant review — Round 4

Date: 2026-09-19

## Verdict

The chunked remediation commits are well-structured and focused tests remain green, but the implementation is not release-ready. A fourth adversarial pass found runtime-blocking OMP protocol/configuration defects, workflow recovery gaps, provider retry/cancellation issues, and two regressions introduced by the remediation itself. The most important findings were verified against the pinned/runtime paths rather than inferred from test names.

The requested Sonnet reviewer was still unavailable due account rate limiting, so the available Codex reviewer fallback performed this pass.

## Findings

### High

1. **OMP launch flag and RPC protocol are incompatible with the pinned binary** — §§12–13. `WorkflowDispatcher` launches `--rpc`, while the pinned OMP accepts `--mode rpc`; the host also sends JSON-RPC `method` frames while the binary expects typed `prompt/new_session/switch_session/abort` commands. Real planning cannot create/resume sessions. Correct the exact pinned CLI and wire protocol, then add a real-binary startup/framing/resume/cancel smoke test.
2. **Mounted OMP configuration uses an unsupported environment variable** — §§12, 34. `OMP_CONFIG_DIR` is not consumed by the pinned binary; use its supported `PI_CONFIG_FILES`, `PI_CODING_AGENT_DIR`, or `--config` contract and allow-list the exact setting.
3. **PR/MR revise commands are still dropped when the request has no phase labels** — §§19, 21. Parsing the request labels through full workflow-state analysis yields `MissingPhaseLabel`, so correctly placed `agent:cmd:revise` is ignored. Parse command labels independently and admit the work through the bounded pool.
4. **Remote-history continue does not rebuild/adopt authoritative remote history** — §§23, 25, 26. Continue bypasses reconciliation but resumes implementation against the old planned base, risking non-fast-forward overwrite. Add explicit remote-history recovery.
5. **Safe fast-forward ancestry is detected but not applied to the worktree** — §23. Human commits on the published branch are not incorporated before revision.
6. **Shutdown admission is not stopped before drain** — §27. Queued work can start during the grace window. Stop/discard admission before draining.
7. **Checkpointed planning cannot recover if worktree creation was interrupted** — §§20, 26. Continue assumes the retained worktree exists instead of recreating it idempotently.
8. **Plan hash is computed from a later issue fetch rather than the reconciled snapshot** — §22. An edit between OMP planning and hash fetch can make stale input appear current.
9. **GitHub secondary-rate-limit fallback is only one second** — §§6, 27. Missing-header secondary limits require conservative minute-scale backoff with bounded exponential growth.
10. **GitHub mutation cancellation still cancels only the waiter** — §§24, 27. Tokenless high-level mutations may complete remotely after cancellation reports success.
11. **Clone URL query/fragment credentials are accepted** — §8. Validation checks URI user info but retains query credentials such as `access_token=...`.

### Medium

12. Restore Telegram exactly-one-token-source and non-empty `ChatId` startup validation; the TLS validation change replaced it (§§8, 28, 31).
13. Default Git identity must come from authenticated provider identity, not only `IdentityOverride` or the localhost fallback (§10).
14. GitHub idempotent comment updates must use the retryable path; only resource-creating writes should be non-idempotent (§§4, 27).
15. Revision feedback baseline is captured after context construction; comments arriving in that interval can be omitted (§21).
16. Review-thread `IsResolved` state is omitted from feedback digests (§21).
17. Canonical generated sections still need unique markers; valid plans containing Agent-state examples or reserved headings can be misparsed (§17).
18. Provider-native issue closing must honor an inherited per-repository disable setting (§21).
19. `continue` advertised for corrupt state remains blocked by the unconditional `Corrupt` return (§§25–26).
20. Duplicate canonical locator ambiguity is logged but not durably escalated/notified (§§17, 25–26).
21. Missing canonical comment between classification and dispatch can still dereference null (§§25–26).
22. Plain Docker documentation lost its persistent `/data` volume while retaining `--read-only`; startup cannot create required directories (§§32–33).
23. OMP session/process behavior is still unverified against a real pinned binary in the checked-in tests (§§12–13, 36).

## Verified after remediation

- Provider/configuration focused suites: Configuration 18, Domain 3, Providers 21, GitHub 28, GitLab 33 passed.
- Context/Workflow/Host focused suites: Context 39, Workflow 52, Host 15 passed.
- Observability/Git/Notifications/Security focused paths passed, including Git ancestry, DNS-rebinding address forwarding, cancellation metric exclusion, terminal OMP error metrics, and deployment artifact checks.
- Helm lint, full/default/external-resource templates, strict kubeconform, Compose config, and deployment security tests passed.
- Chunked commits are present and logically separated:
  - `c9eb8c7` provider/configuration remediation
  - `7f67353` context/workflow remediation
  - `75383ed` host/observability/Git remediation
  - `0a87ffd` deployment/security remediation
  - `ae3af5a` audit report and model routing

## Residual uncertainty

No real provider timing, broker, or pinned OMP process integration was available for all paths. The OMP protocol findings are based on the pinned binary contract and current host framing; they require a real-binary smoke test before release. The Sonnet review provider remained rate-limited, so this report records the available Codex fallback explicitly.

## Remediation status

All fourth-pass findings were actioned in four additional commits:

- `84cdf80` — typed OMP RPC/session protocol, supported runtime configuration, and real-binary smoke coverage.
- `742a7b7` — workflow recovery, command routing, canonical markers, shutdown admission, input/review snapshots, and remote-history handling.
- `02fad60` — provider-derived identity, Telegram validation, clone URL hardening, GitHub retry/backoff/cancellation, and provider tests.
- `0796e02` — Docker persistence/config documentation and deployment regression coverage.

Final verification:
- `dotnet test IssueAgent.slnx --configuration Release --no-build --no-restore --nologo` — passed: 311 tests, 0 failures, 1 optional OMP smoke skipped without the binary.
- `dotnet build IssueAgent.slnx --configuration Release --no-restore -nodeReuse:false -m:1` — passed, 0 warnings/errors.
- `OMP_TEST_BINARY=/home/leo/.local/bin/omp dotnet test tests/IssueAgent.Omp.Tests/IssueAgent.Omp.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~RealPinnedBinary` — passed, 1/1.
- `helm lint deploy/helm/issue-agent` — passed.
- `helm template issue-agent deploy/helm/issue-agent` — passed.
- `docker compose -f deploy/docker-compose.yml config` — passed.
- `git diff --check` — passed before commits.

The worktree is clean. The real local OMP v18.2.3 smoke exercised typed startup, session creation/state, and abort behavior.
