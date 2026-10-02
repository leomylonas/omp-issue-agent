# IssueAgent deployment examples

## Docker Compose

Copy `issue-agent.env`, add provider/repository settings that reference
`/run/issue-agent-secrets/<secret-name>`, and provide the provider secret at
`secrets/github-token`. The base Compose file is brokerless and does not require an Auth Broker
token. Restrict each file-backed secret to its owner before starting the stack:

```sh
chmod 600 secrets/github-token
```

```sh
docker compose -f docker-compose.yml up -d --build
```

The entrypoint copies provider and notification secret files to a root-only runtime mount before
starting IssueAgent. Each OMP workflow is assigned a stable, distinct unprivileged UID and its
retained workflow directory is owner-only, so one workflow cannot read or alter another workflow's
worktree or attachments. A retained UID collision fails the affected workflow rather than sharing a
principal. IssueAgent retains `DAC_OVERRIDE` only to reconcile and clean up those retained directories
after OMP exits; the OMP wrapper removes it before executing OMP. Only explicitly configured OMP
execution secrets are mounted where OMP can read them.

To enable the bundled Auth Broker, create one bearer token file and start the broker overlay. The
token is mounted as a Compose secret and never placed in an environment file:

```sh
umask 077
openssl rand -hex 32 > secrets/omp-auth-broker-token
OMP_AUTH_BROKER_IMAGE=issue-agent:local \
  docker compose -f docker-compose.yml -f docker-compose.auth-broker.yml up -d --build
```

The broker persists its state and IssueAgent reads the same Compose secret only to pass it to OMP.
The broker's port is published on loopback only. Authenticate with the local OMP CLI first, then
migrate its OAuth state to the broker through the port-forwarded endpoint:

```sh
omp auth-broker login

OMP_AUTH_BROKER_URL=http://127.0.0.1:${OMP_AUTH_BROKER_PORT:-8081} \
OMP_AUTH_BROKER_TOKEN="$(cat secrets/omp-auth-broker-token)" \
  omp auth-broker migrate --from-local --include-oauth

OMP_AUTH_BROKER_URL=http://127.0.0.1:${OMP_AUTH_BROKER_PORT:-8081} \
OMP_AUTH_BROKER_TOKEN="$(cat secrets/omp-auth-broker-token)" \
  omp auth-broker status
```

Complete the provider OAuth flow locally before migration. The migration forwards that completed
OAuth state to the broker; it does not relay a browser callback. Do not expose the loopback port
or paste the bearer token into a browser.

To rotate the bearer token, replace its local secret file and recreate the broker overlay so its
single persistent auth store receives the new value:

```sh
umask 077
openssl rand -hex 32 > secrets/omp-auth-broker-token
docker compose -f docker-compose.yml -f docker-compose.auth-broker.yml \
  up -d --force-recreate omp-auth-broker issue-agent
```

To enable the optional Telegram and Slack examples, create both secret files and add the
notification overlay:

```sh
docker compose -f docker-compose.yml -f docker-compose.notifications.yml up -d --build
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
`http://<broker-service>:8081` into IssueAgent, starts the broker with
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

Keep the broker internal and use a local port-forward only for migration. Complete provider OAuth
with the local OMP CLI before exposing the broker on loopback:

```sh
omp auth-broker login

BROKER_SERVICE="$(kubectl -n "$NAMESPACE" get service \
  -l app.kubernetes.io/component=auth-broker,app.kubernetes.io/instance="$RELEASE" \
  -o jsonpath='{.items[0].metadata.name}')"
kubectl -n "$NAMESPACE" port-forward "svc/${BROKER_SERVICE}" 8081:8081

OMP_AUTH_BROKER_URL=http://127.0.0.1:8081 \
OMP_AUTH_BROKER_TOKEN="$(cat omp-auth-broker-token)" \
  omp auth-broker migrate --from-local --include-oauth
```

Migration forwards completed local OAuth state rather than relaying a browser callback. The broker
has no interactive web UI; never put its bearer token in a callback URL or expose the ClusterIP
service outside the cluster.

## Plain Docker

Build the image, create persistent storage, and provide configuration and secrets explicitly:

```sh
docker build -t issue-agent:local ..
docker volume create issue-agent-data
docker run --rm --name issue-agent \
  --read-only --cap-drop=ALL \
  --cap-add=CHOWN --cap-add=FOWNER --cap-add=DAC_OVERRIDE \
  --cap-add=SETGID --cap-add=SETPCAP --cap-add=SETUID \
  --security-opt=no-new-privileges \
  --tmpfs /tmp --tmpfs /run/issue-agent-secrets \
  -p 127.0.0.1:8080:8080 \
  -v issue-agent-data:/data \
  -e PI_CONFIG_FILES=/etc/omp/config.yml \
  --volume "$PWD/omp:/etc/omp:ro" \
  --env-file "$PWD/issue-agent.env" \
  --mount type=bind,src="$PWD/secrets/github-token",dst=/run/secrets-source/github_token,readonly \
  issue-agent:local
```

The listed capabilities are required only while the root entrypoint repairs a mounted data volume,
copies root-owned secret sources, assigns each OMP workflow a distinct filesystem owner, and switches
the host and OMP to their unprivileged UIDs. The entrypoint retains `DAC_OVERRIDE` for host-only
post-run workflow access; by default, IssueAgent invokes the unprivileged OMP wrapper, which drops
that and all other inherited capabilities before OMP executes. Point file-backed provider and
notification settings at `/run/issue-agent-secrets/<secret-name>`; only `/data` is persistent
writable application storage.
OMP configuration is read-only. `PI_CONFIG_FILES` is the pinned OMP runtime setting that points to
the mounted `/etc/omp/config.yml` file; OMP sessions and native state remain under `/data/omp`.

### Plain Docker Auth Broker

The broker is supported with plain Docker as a separate container. Create an isolated network and
broker storage, then create the bearer-token file with restrictive permissions:

```sh
docker network create issue-agent
docker volume create omp-auth-broker-data
umask 077
openssl rand -hex 32 > secrets/omp-auth-broker-token
```

Start the broker on that network. Its token and mutable auth directory are owned by the
unprivileged broker UID, so the broker can update its persistent OAuth state without making the
bearer token group-readable:

```sh
docker run -d --name omp-auth-broker --network issue-agent \
  --read-only --cap-drop=ALL \
  --cap-add=CHOWN --cap-add=FOWNER --cap-add=DAC_OVERRIDE \
  --cap-add=SETGID --cap-add=SETUID \
  --security-opt=no-new-privileges --tmpfs /tmp \
  -p 127.0.0.1:8081:8081 \
  -v omp-auth-broker-data:/data \
  --mount type=bind,src="$PWD/secrets/omp-auth-broker-token",dst=/run/secrets/omp_auth_broker_token,readonly \
  --entrypoint /bin/sh issue-agent:local -ec '
    chown -R 10001:10001 /data
    install -o 10001 -g 10001 -d -m 0700 /data/.omp
    install -o 10001 -g 10001 -m 0600 /run/secrets/omp_auth_broker_token /data/.omp/auth-broker.token
    exec /usr/bin/setpriv --reuid=10001 --regid=10001 --clear-groups --no-new-privs -- /usr/local/bin/omp auth-broker serve --bind=0.0.0.0:8081
  '
```

For the `issue-agent` command above, add `--network issue-agent`,
`-e IssueAgent__Omp__AuthBrokerUrl=http://omp-auth-broker:8081`,
`-e IssueAgent__Omp__ExecutionSecrets__OMP_AUTH_BROKER_TOKEN__File=/run/issue-agent-secrets/omp_auth_broker_token`, and
`--mount type=bind,src="$PWD/secrets/omp-auth-broker-token",dst=/run/secrets-source/omp_auth_broker_token,readonly`.
This copies the token into the file-backed execution-secret path, allowing only the explicitly
configured OMP process to receive it. Run the same local OAuth login and migration commands
described for Compose, using `127.0.0.1:8081`.

## Optional integrations

`issue-agent.env` contains examples for OTLP metrics and tracing, Telegram, and Slack. Configure notification
tokens through mounted secret files and start `docker-compose.notifications.yml` with the base
Compose file; do not place token values or webhook credentials directly in the env file.
