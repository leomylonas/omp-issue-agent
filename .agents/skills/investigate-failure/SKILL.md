---
name: investigate-failure
description: Investigate a reported or newly observed IssueAgent failure, establish the root cause with evidence, capture it in a failing regression test, implement the fix when requested, and prove the regression and affected suite pass. Use for bugs, crashes, incorrect state, and intermittent operational failures.
---

# Investigate failure

Treat the reported behavior as ground truth. Do not ask the reporter to reproduce evidence already provided. Local execution is for diagnosis, regression coverage, and verification.

## Preserve evidence

1. Record the observed behavior, expected behavior, affected version or state, available logs, and reproduction inputs.
2. Read `AGENTS.md` and the specification sections governing the failed behavior.
3. Protect the working tree and retained diagnostic state. Do not clean, reset, retry away, or mutate evidence before capturing it.
4. For intermittent or recovery failures, preserve ordering, timestamps, correlation identifiers, remote state, local state, and the last known successful checkpoint.

## Narrow the cause

1. Trace the request or operation across every relevant boundary.
2. Build the smallest local scenario that exercises the same observable contract. Do not replace the reported problem with an easier approximation.
3. Form one falsifiable hypothesis at a time.
4. Use focused logging, inspection, or debugging to distinguish the first incorrect state transition or value from downstream symptoms.
5. Check concurrency, cancellation, retries, partial remote success, restart timing, stale local state, and credential/trust boundaries when applicable.
6. State the root cause in terms of the violated invariant or contract, not merely the throwing line.

## Capture and repair

Before changing production code, follow the mandatory regression protocol in [test-and-fix](../test-and-fix/SKILL.md):

1. Add a focused test reproducing the defect through the most realistic practical boundary.
2. Run it and show that it fails for the intended reason.
3. Implement the root-cause fix without suppressing the symptom or weakening the contract.
4. Run the identical test and show that it passes.
5. Run the affected suite and any relevant restart, negative-path, or integration checks.

If the user requested diagnosis only, stop after establishing the root cause and describe the exact regression test and fix that should follow; do not modify code.

If no stable automated test can represent the failure, explain the technical reason and construct the closest deterministic executable regression check. A claim that a test is inconvenient is not sufficient.

## Report

Report:

- failure and expected contract;
- evidence examined;
- hypotheses tested and rejected;
- root cause with file/symbol evidence;
- regression test and red result;
- fix and rationale;
- identical regression test and green result;
- affected-suite results;
- remaining uncertainty or operational follow-up.

Do not declare a probabilistic or intermittent issue fixed from a single successful retry.