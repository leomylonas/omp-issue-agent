# IssueAgent deployment examples

## Docker Compose

Copy `issue-agent.env`, add provider/repository settings, provide the provider secret at
`secrets/github-token`, and start IssueAgent:

```sh
docker compose -f docker-compose.yml up -d issue-agent
```

To run the optional Auth Broker profile, pin its image explicitly:

```sh
OMP_AUTH_BROKER_IMAGE=ghcr.io/example/omp-auth-broker:0.1.0 \
  docker compose -f docker-compose.yml --profile auth-broker up -d
```

IssueAgent health and metrics are bound to `127.0.0.1:8080`. The Auth Broker is reachable only on
the Compose network.

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
  -v "$PWD/omp:/etc/omp:ro" \
  --env-file "$PWD/issue-agent.env" \
  --mount type=bind,src="$PWD/secrets/github-token",dst=/run/secrets/github_token,readonly \
  issue-agent:local
```

The container runs as the non-root `issueagent` user. `/data` is the only persistent writable application path; OMP configuration is read-only.

## Optional integrations

`issue-agent.env` contains examples for OTLP tracing, Telegram, and Slack. Configure notification
tokens through mounted secret files; do not place token values or webhook credentials directly in
the env file.
