# IssueAgent deployment examples

## Docker Compose

Copy `issue-agent.env`, add provider/repository settings, and provide the provider secret at
`secrets/github-token`. For an Auth Broker deployment, create one bearer token file before starting
either service; it is mounted as a Compose secret and never placed in an environment file:

```sh
umask 077
openssl rand -hex 32 > secrets/omp-auth-broker-token
```

Start IssueAgent without a broker by clearing its broker URL:

```sh
ISSUE_AGENT_OMP_AUTH_BROKER_URL="" \
  docker compose -f docker-compose.yml up -d issue-agent
```

To start the bundled broker, use the same image (it contains the pinned OMP binary):

```sh
OMP_AUTH_BROKER_IMAGE=issue-agent:local \
  docker compose -f docker-compose.yml --profile auth-broker up -d --build
```

The broker persists the bearer token in its auth store and IssueAgent reads the same Compose secret
only to pass it to OMP. The broker's port is published on loopback only. Log in through the actual
OMP CLI—not a browser UI—using the port-forwarded endpoint and its bearer token:

```sh
OMP_AUTH_BROKER_URL=http://127.0.0.1:${OMP_AUTH_BROKER_PORT:-8081} \
OMP_AUTH_BROKER_TOKEN="$(cat secrets/omp-auth-broker-token)" \
  omp auth-broker login

OMP_AUTH_BROKER_URL=http://127.0.0.1:${OMP_AUTH_BROKER_PORT:-8081} \
OMP_AUTH_BROKER_TOKEN="$(cat secrets/omp-auth-broker-token)" \
  omp auth-broker status
```

Keep the terminal open while completing the provider OAuth flow. Open the authorization URL in a
browser on the same machine and allow its callback to return there; if the provider displays a
callback URL/code, paste it into the still-running `omp auth-broker login` prompt. Do not expose
the loopback port or paste the bearer token into a browser.

To inspect or rotate the broker-native token from the broker's persistent store, use its supported
CLI commands. On rotation, replace the Compose secret file with the command output and restart both
services so their shared value changes together:

```sh
docker compose -f docker-compose.yml exec -T omp-auth-broker \
  omp auth-broker token --regenerate > secrets/omp-auth-broker-token
docker compose -f docker-compose.yml --profile auth-broker restart
```


## Helm
The chart generates both the IssueAgent ConfigMap and a read-only OMP ConfigMap by default.
The generated map includes `config.yml`; provide OMP files under `omp.config.data`, or reference
an existing ConfigMap with `omp.config.existingConfigMap` and ensure it contains the configured
`omp.config.file` key. Existing ConfigMaps and Secrets must also provide their content checksum
(`existingConfigMapChecksum`, `omp.config.existingConfigMapChecksum`, or `existingSecretChecksum`)
when rendering offline so changes deterministically roll the pod. `PI_CONFIG_FILES` is set to the
mounted file path (for example `/etc/omp/config.yml`), and OMP session state remains in `/data/omp`.

When `omp.authBroker.enabled=true`, the chart reuses `image.repository` and the release image tag
unless `omp.authBroker.image` overrides either one. It injects
`http://<release>-issue-agent-auth-broker:8081` into IssueAgent, starts the broker with
`omp auth-broker serve --bind=0.0.0.0:8081`, and mounts the same
`OMP_AUTH_BROKER_TOKEN` Secret key into the broker's native auth store and the IssueAgent OMP
environment. Supply that key through an existing `secret.existingSecret`, or keep its value out of
shell history with `--set-file`:

```sh
umask 077
openssl rand -hex 32 > omp-auth-broker-token
helm upgrade --install "$RELEASE" helm/issue-agent --namespace "$NAMESPACE" \
  --set omp.authBroker.enabled=true \
  --set-file secret.stringData.OMP_AUTH_BROKER_TOKEN=omp-auth-broker-token
```

Keep the broker internal and use a local port-forward only during setup. Then invoke the OMP CLI
from the same machine, with the bearer token from the protected local file:

```sh
kubectl -n "$NAMESPACE" port-forward \
  "svc/${RELEASE}-issue-agent-auth-broker" 8081:8081

OMP_AUTH_BROKER_URL=http://127.0.0.1:8081 \
OMP_AUTH_BROKER_TOKEN="$(cat omp-auth-broker-token)" \
  omp auth-broker login
```

Complete the provider flow in the local browser and leave the CLI running for its callback. The
broker has no interactive web UI; never put its bearer token in a callback URL or expose the
ClusterIP service outside the cluster.

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
