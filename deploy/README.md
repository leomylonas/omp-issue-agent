# IssueAgent deployment examples

## Docker Compose

Copy `issue-agent.env`, add provider/repository settings, provide the provider secret at
`secrets/github-token`, and start IssueAgent:

```sh
ISSUE_AGENT_OMP_AUTH_BROKER_URL="" \
  docker compose -f docker-compose.yml up -d issue-agent
```

To run the optional Auth Broker profile, use the same image (it contains the pinned OMP binary)
and its supported CLI entrypoint:

```sh
docker compose -f docker-compose.yml --profile auth-broker up -d --build
```

IssueAgent is wired to the bundled broker at `http://omp-auth-broker:8081` when the profile is
enabled. To make that explicit (or to use a separately published image with the same contract):

```sh
ISSUE_AGENT_OMP_AUTH_BROKER_URL=http://omp-auth-broker:8081 \
OMP_AUTH_BROKER_IMAGE=issue-agent:local \
  docker compose -f docker-compose.yml --profile auth-broker up -d --build
```

The broker runs `omp auth-broker serve --bind=0.0.0.0:8081`. Its interactive setup endpoint is
published only on host loopback at `http://127.0.0.1:${OMP_AUTH_BROKER_PORT:-8081}`. It is not
reachable from the network. Open it only during initial login and stop the profile afterwards if
setup is complete.


## Helm
The chart generates both the IssueAgent ConfigMap and a read-only OMP ConfigMap by default.
The generated map includes `config.yml`; provide OMP files under `omp.config.data`, or reference
an existing ConfigMap with `omp.config.existingConfigMap` and ensure it contains the configured
`omp.config.file` key. Existing ConfigMaps and Secrets must also provide their content checksum
(`existingConfigMapChecksum`, `omp.config.existingConfigMapChecksum`, or `existingSecretChecksum`)
when rendering offline so changes deterministically roll the pod. `PI_CONFIG_FILES` is set to the
mounted file path (for example `/etc/omp/config.yml`), and OMP session state remains in `/data/omp`.

When `omp.authBroker.enabled=true`, the chart reuses the release IssueAgent image by default,
injects `http://<release>-issue-agent-auth-broker:8081` into IssueAgent, and starts the broker with
`omp auth-broker serve --bind=0.0.0.0:8081`. A separately published image may be supplied only when
it provides that same executable contract.
For Helm, keep the broker internal and use a local port-forward only during setup:

```sh
kubectl -n "$NAMESPACE" port-forward \
  "svc/${RELEASE}-issue-agent-auth-broker" 8081:8081
```

## Plain Docker

Build the image, create persistent storage, and provide configuration and secrets explicitly:

```sh
docker build -t issue-agent:local ..
docker volume create issue-agent-data
docker run --rm --name issue-agent \
  --read-only --cap-drop=ALL --security-opt=no-new-privileges \
  --tmpfs /tmp \
  -p 127.0.0.1:8080:8080 \
  -v issue-agent-data:/data \
  -e PI_CONFIG_FILES=/etc/omp/config.yml \
  --volume "$PWD/omp:/etc/omp:ro" \
  --env-file "$PWD/issue-agent.env" \
  --mount type=bind,src="$PWD/secrets/github-token",dst=/run/secrets/github_token,readonly \
  issue-agent:local
```

The container runs as the non-root `issueagent` user. `/data` is the only persistent writable
application path; OMP configuration is read-only. `PI_CONFIG_FILES` is the pinned OMP runtime
setting that points to the mounted `/etc/omp/config.yml` file; OMP sessions and native state remain
under `/data/omp`.

## Optional integrations

`issue-agent.env` contains examples for OTLP tracing, Telegram, and Slack. Configure notification
tokens through mounted secret files; do not place token values or webhook credentials directly in
the env file.
