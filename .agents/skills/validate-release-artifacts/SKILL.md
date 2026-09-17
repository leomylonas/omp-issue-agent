---
name: validate-release-artifacts
description: Validate IssueAgent Docker, Docker Compose, Helm, monitoring, and release artifacts using checked-in commands and representative configurations. Use before merging deployment changes or preparing a release; never publish artifacts unless explicitly requested.
---

# Validate release artifacts

Validate the complete affected delivery surface without publishing, deploying to a shared environment, or mutating remote registries.

## Discover the canonical workflow

1. Read `AGENTS.md`, specification §§30–35, and checked-in CI, scripts, lock files, Dockerfiles, Compose files, chart files, and schemas.
2. Derive commands and supported configuration combinations from those sources. Do not invent flags or bypass checked-in wrappers.
3. Record required local tools and versions. A missing required tool is a validation failure, not permission to skip the check silently.
4. Determine which artifacts changed and which unchanged artifacts depend on them.

## Validation matrix

Run every applicable category.

### Application and image

- Build and publish inputs use locked restore and the intended configuration.
- Container build succeeds for the locally testable platform without leaking build credentials.
- The runtime image starts as the configured non-root user and contains the pinned required executables.
- Writable paths, read-only-root expectations, health endpoints, metrics endpoint, and graceful shutdown work in the running image.
- Image metadata and application/chart version relationships match the release rules.

### Docker Compose

- Render/validate every checked-in representative Compose configuration.
- Start the applicable stack, wait for declared health, exercise IssueAgent and Auth Broker connectivity where configured, and stop it cleanly.
- Confirm persistence mounts, configuration, secrets, and localhost-only example exposure resolve as documented.

### Helm

- Run chart linting.
- Render representative default, generated/existing resource, monitoring-enabled, Auth Broker-enabled/disabled, persistence, and resource-override combinations.
- Validate rendered objects with the repository's schema validator.
- Check single-replica behavior, probes, security contexts, PVC choices, checksum rollouts, service-account/RBAC absence, and supported monitoring resources.
- Do not compensate for invalid templates with post-render mutation.

### Monitoring assets

- Validate Prometheus rules with the native configured tool.
- Parse and validate Grafana dashboard JSON and sidecar metadata.
- Inspect metric labels for bounded cardinality and ensure dashboards/alerts use emitted metric names.

### Release contract

- Validate stable SemVer and exact agreement among application, image, and chart versions.
- Validate tag-trigger and successful-main behavior without publishing.
- Confirm no prohibited floating release tags or unsupported Kubernetes manifests are introduced.

## Failure handling

Capture the exact failing command and output. If validation exposes a code or artifact defect and the task includes repair, follow the red-green protocol in [test-and-fix](../test-and-fix/SKILL.md): establish a focused automated regression check, show it fail, fix the source artifact, show the same check pass, then rerun the complete affected matrix.

Do not edit generated output directly. Fix its source and regenerate it with checked-in tooling.

## Report

Provide:

- tool versions and environment constraints;
- commands and outcomes by matrix category;
- configurations rendered or exercised;
- red and green evidence for repaired defects;
- skipped checks with exact blockers;
- residual platform limitations, especially untested architecture-specific behavior.

A successful lint alone is not release-artifact validation.