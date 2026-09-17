---
name: review-spec-compliance
description: Review an IssueAgent implementation slice for completeness against the authoritative specification and its acceptance requirements. Use after implementing a feature or when auditing whether a plan or change covers every applicable requirement.
---

# Review specification compliance

Evaluate feature completeness against `plans/issue-agent.md`. This is a requirements audit, not a general code-style review or a replacement for `review-invariants`.

## Define the slice

1. Identify the feature, issue, plan, or changed paths under review.
2. Read `AGENTS.md` and locate every specification section that directly governs the slice.
3. Follow references into adjacent requirements when the slice crosses provider, workflow, security, recovery, testing, deployment, or observability boundaries.
4. Convert normative statements into observable acceptance criteria. Preserve the specification's wording and intent; do not invent substitute requirements.

## Build the compliance matrix

For each acceptance criterion, record:

- specification section and concise requirement;
- implementation evidence by file and symbol;
- test or executable verification evidence;
- status: `satisfied`, `partial`, `missing`, `contradicted`, or `not-applicable`;
- rationale for `not-applicable` or any uncertainty.

A type, interface, stub, configuration key, or passing mock is not sufficient evidence when the requirement describes end-to-end behavior. Verify all configured providers and deployment forms where the specification requires parity.

## Check completeness

Inspect for:

- missing callers, adapters, configuration binding, validation, or inheritance;
- success-only implementations without specified retry, cancellation, ambiguity, or recovery behavior;
- provider behavior leaking into domain workflows;
- stale tests or documentation that describe the old contract;
- required negative, restart, idempotency, and security cases;
- accidental implementation of explicit V1 non-goals;
- placeholders, no-ops, fake fallbacks, or deferred pieces presented as complete.

Run focused checks when they are required to establish compliance. Record exact commands and outcomes; do not infer passing behavior from source inspection when an executable boundary is available.

## Report

Report in this order:

1. Missing, contradicted, and partial requirements, ordered by impact.
2. The compliance matrix.
3. Verified end-to-end scenarios and commands.
4. Not-applicable requirements and rationale.
5. Residual uncertainty and the evidence needed to resolve it.

Each gap must name the relevant specification section, concrete evidence, user-visible or operational consequence, and smallest complete remediation. If the slice is compliant, state that explicitly without omitting the matrix or review coverage.