# IssueAgent Agent Guide

## Start here

`plans/issue-agent.md` is the authoritative source for product architecture, behavior, scope, and acceptance requirements. Read the sections relevant to a change before designing or editing code. This file defines how agents work in the repository; it does not replace or summarize the specification.

When this guide, derived documentation, code, or tests disagree with the specification, follow the specification and fix the stale artifact in the same change. Do not change the specification merely to match an implementation. A deliberate requirement change must be made explicitly in the specification first.

## Working method

1. Translate the relevant specification sections into concrete acceptance criteria.
2. Inspect the existing implementation, tests, and repository conventions before choosing a design.
3. Implement the smallest coherent production slice that satisfies those criteria. Do not add speculative V2 infrastructure or unrequested compatibility paths.
4. Keep provider, Git, OMP, workflow, notification, and deployment boundaries aligned with the abstractions and ownership defined by the specification.
5. Preserve rationale for material decisions in the appropriate plan, implementation result, code comment, or user-facing documentation. Comments should explain constraints and reasons, not restate code.
6. Verify the changed behavior through the real boundary when practical, then run the focused automated checks that protect it.

If the specification leaves a material product, security, or data-recovery decision unresolved, do not silently make it permanent. Surface the ambiguity and the safest concrete options. For ordinary implementation details, follow existing patterns and choose the least complex design that preserves the specified behavior.

## Cross-cutting review gates

Use these specification sections as review checklists rather than duplicating their requirements here:

- Provider-neutral domain boundaries: §4.
- Secrets, trust, process isolation, and attachment safety: §§8–15 and §29.
- Workflow transitions, human gates, recovery, and idempotency: §§17–27.
- Deployment, observability, packaging, and release behavior: §§30–35.
- Required test coverage and security cases: §36.
- V1 exclusions and the governing system invariant: §§37–38.

Treat restart safety, conservative reconciliation, credential isolation, and provider parity as system properties. A local happy-path implementation is incomplete if it violates them.

## Engineering expectations

- Prefer explicit domain types and narrow interfaces over provider-shaped or transport-shaped workflow code.
- Keep side effects at owned boundaries. Workflow logic should be deterministic and testable without live providers or paid model calls.
- Model state transitions and retry/idempotency decisions explicitly; do not infer durable state from a single local artifact.
- Fail closed where the specification defines a trust boundary. Never log, serialize into prompts, or forward credentials beyond their configured destination.
- Use managed libraries for normal operations and external processes only where the specification requires them.
- Avoid parallel conventions, duplicate abstractions, compatibility shims, and dormant extension points. Perform clean migrations of all callers when contracts change.
- Do not hand-edit generated output. Change its source and regenerate it using the checked-in tooling.

## Build and validation

Use the repository's checked-in solution, scripts, lock files, and CI workflows as the source for exact commands. Do not invent commands from the intended technology stack when scaffolding is absent.

For each change:

- Add or update tests only where they defend observable behavior, boundaries, state transitions, recovery, or a plausible regression.
- For a bug, reproduce the failure before fixing it and verify that the same scenario passes afterward.
- Run the narrowest relevant checks while iterating, then all affected build, analyzer, test, packaging, or deployment checks before completion.
- Exercise deployment artifacts with their native validators when they change (for example Docker/Compose or Helm tooling).
- Report exact commands and outcomes. Distinguish new failures from verified pre-existing failures; never claim a check that was not run.

The comprehensive test matrix in §36 of the specification is mandatory project scope, but individual tests must still protect meaningful behavior rather than mirror implementation details.

## Documentation ownership

- `plans/issue-agent.md`: authoritative architecture and product requirements.
- `AGENTS.md`: provider-agnostic repository working instructions.
- Provider-specific instruction files: references to this file only, plus truly provider-specific loading syntax if required.
- Future skills: reusable, task-focused procedures; they must reference the specification instead of copying product requirements.

Keep architectural decisions in the specification. Update this guide only for stable repository-wide working conventions, canonical commands, or navigation that agents need on most tasks.

## Completion standard

A change is complete only when its relevant specification criteria work end to end, all affected callers and artifacts are migrated, focused verification passes, and user-facing or operator-facing documentation is updated where behavior changed. No placeholders, silent scope reductions, or deferred correctness work.