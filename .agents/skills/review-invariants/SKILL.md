---
name: review-invariants
description: Review a design, plan, diff, or implementation against IssueAgent's cross-cutting invariants. Use for architecture reviews, change reviews, and pre-merge safety checks; do not use as a substitute for detailed feature-requirement review.
---

# Review invariants

Perform an evidence-backed review. Review only by default; edit code only when the user explicitly asks for findings to be fixed.

## Ground the review

1. Read `AGENTS.md` and `plans/issue-agent.md`.
2. Establish the review target: supplied design or plan, named files, current change, or implementation area.
3. Identify the specification sections governing the target. The specification is authoritative; do not restate it as a new policy.
4. Inspect enough surrounding code, configuration, tests, and call sites to evaluate behavior across the real boundary. A diff alone is insufficient when unchanged callers or recovery paths affect the result.

## Trace the behavior

For each affected operation, trace:

- inputs and trust boundary;
- domain and provider abstractions crossed;
- durable and local state read or written;
- externally visible side effects and their ordering;
- interruption, retry, cancellation, and restart behavior;
- human decision points;
- credentials or sensitive data available at each boundary;
- verification and observability covering the operation.

## Review gates

Evaluate the target against the applicable cross-cutting requirements, including:

- provider-neutral workflow logic and provider parity;
- remote durable truth, conservative reconciliation, and idempotency;
- safe phase/state/command transitions and required human gates;
- preservation of useful state on ambiguity or failure;
- secret isolation, authenticated-host boundaries, TLS/SSH trust, and log safety;
- publication ordering, retained history, and no destructive remote behavior;
- scheduling, concurrency, fairness, cancellation, and graceful shutdown where relevant;
- bounded metric cardinality and useful structured correlation;
- V1 scope and explicit non-goals;
- tests that exercise behavior, negative paths, and restart/failure boundaries rather than implementation details.

Do not mark a gate satisfied from naming or intent. Require code, configuration, tests, or an executable check that demonstrates it.

## Findings

Report findings first, ordered by severity. Every finding must include:

- severity and concise title;
- violated or endangered specification section;
- exact file/symbol or design statement;
- the concrete failure scenario;
- impact on users, security, or recoverability;
- the smallest safe correction.

Separate verified defects from risks that require runtime evidence. Do not report stylistic preferences as invariant violations.

After findings, include:

- sections and paths reviewed;
- invariants found to be satisfied, with brief evidence;
- checks run and exact outcomes;
- remaining uncertainty or unreviewed boundaries.

If no violations are found, say so explicitly and still state the review coverage and residual risks.