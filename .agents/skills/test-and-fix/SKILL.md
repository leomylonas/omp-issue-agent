---
name: test-and-fix
description: Run the applicable IssueAgent validation suite, diagnose failures, add a focused red regression test for each reproducible code defect, fix the root cause, prove the test turns green, and rerun the affected suite. Use when asked to test, validate, repair CI failures, or make checks pass.
---

# Test and fix

Run validation and repair failures through an explicit red-green cycle. Preserve unrelated user changes and never weaken a valid contract merely to make a check pass.

## Establish the validation surface

1. Read `AGENTS.md`, the relevant parts of `plans/issue-agent.md`, and checked-in CI/build scripts.
2. Derive exact commands from the repository. Do not invent commands from the intended stack.
3. Determine the complete applicable suite: formatting/analyzers, build, unit and integration tests, provider contracts, process/E2E checks, and artifact validators affected by the change.
4. Accept user-reported failures as facts. Local reproduction is for diagnosis and executable regression coverage, not to require the user to prove the report again.
5. Record the initial command, environment assumptions, exit status, and salient failure output.

## Classify each failure

Classify before editing:

- reproducible product-code defect;
- incorrect or obsolete test expectation;
- build, analyzer, packaging, or deployment defect;
- environmental or missing-tool failure;
- pre-existing unrelated failure;
- nondeterministic failure requiring isolation.

Trace the failing path far enough to identify the root cause. Do not suppress an exception, loosen an assertion, add a retry, or special-case the observed input unless that is the specified behavior.

## Mandatory red-green regression cycle

For every reproducible product-code defect discovered:

1. Add a focused regression test that expresses the observable contract and isolates the defect.
2. If an existing failing test already targets the same contract, add a distinct regression case or refine it to isolate the newly identified defect rather than adding a duplicate assertion.
3. Run the new or refined test before changing production code.
4. Confirm that it fails for the intended behavioral reason. A compile error, fixture error, unrelated exception, or test that already passes is not valid red evidence; correct the test and rerun it.
5. Record the red command and the meaningful failure.
6. Fix the source of the defect with the smallest coherent production change.
7. Run the identical regression test again and confirm it passes.
8. Record the green command and result.

When a formatter, analyzer, build, packaging, or environment failure has no meaningful observable regression-test contract, the failing native command is the red check. Fix the underlying configuration or source, rerun that exact command to green, and explain why an additional behavioral test would not protect against recurrence. Add an automated test whenever a stable consumer-observable contract exists.

Tests must defend behavior, boundaries, state transitions, precedence, recovery, or real errors. Never add source-text assertions, mock echoes, bare not-throw checks, or duplicate tests solely to satisfy the red-green requirement.

## Expand verification

After each focused regression is green:

1. Run the nearest affected test project or suite.
2. Run all other applicable validation identified at the start.
3. Repair newly exposed code defects with the same red-green protocol.
4. Keep pre-existing, environmental, and flaky failures explicit; do not silently relabel them or claim a passing suite.
5. Remove temporary diagnostics and fixtures that are not part of the regression coverage.

## Report

Report:

- every command run and its outcome;
- each failure classification;
- red-test evidence;
- root cause and production fix;
- green-test evidence;
- final affected-suite result;
- unresolved pre-existing, environmental, or nondeterministic failures.

Do not report completion without both red and green evidence for every repaired product defect.