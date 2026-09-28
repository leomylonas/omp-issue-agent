# syntax=docker/dockerfile:1.7@sha256:a57df69d0ea827fb7266491f2813635de6f17269be881f696fbfdf2d83dda33e
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src
COPY ["IssueAgent.slnx", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY src/IssueAgent.Configuration/IssueAgent.Configuration.csproj src/IssueAgent.Configuration/packages.lock.json src/IssueAgent.Configuration/
COPY src/IssueAgent.Context/IssueAgent.Context.csproj src/IssueAgent.Context/packages.lock.json src/IssueAgent.Context/
COPY src/IssueAgent.Domain/IssueAgent.Domain.csproj src/IssueAgent.Domain/packages.lock.json src/IssueAgent.Domain/
COPY src/IssueAgent.Git/IssueAgent.Git.csproj src/IssueAgent.Git/packages.lock.json src/IssueAgent.Git/
COPY src/IssueAgent.Host/IssueAgent.Host.csproj src/IssueAgent.Host/packages.lock.json src/IssueAgent.Host/
COPY src/IssueAgent.Notifications/IssueAgent.Notifications.csproj src/IssueAgent.Notifications/packages.lock.json src/IssueAgent.Notifications/
COPY src/IssueAgent.Observability/IssueAgent.Observability.csproj src/IssueAgent.Observability/packages.lock.json src/IssueAgent.Observability/
COPY src/IssueAgent.Omp/IssueAgent.Omp.csproj src/IssueAgent.Omp/packages.lock.json src/IssueAgent.Omp/
COPY src/IssueAgent.Providers.GitHub/IssueAgent.Providers.GitHub.csproj src/IssueAgent.Providers.GitHub/packages.lock.json src/IssueAgent.Providers.GitHub/
COPY src/IssueAgent.Providers.GitLab/IssueAgent.Providers.GitLab.csproj src/IssueAgent.Providers.GitLab/packages.lock.json src/IssueAgent.Providers.GitLab/
COPY src/IssueAgent.Providers/IssueAgent.Providers.csproj src/IssueAgent.Providers/packages.lock.json src/IssueAgent.Providers/
COPY src/IssueAgent.Workflow/IssueAgent.Workflow.csproj src/IssueAgent.Workflow/packages.lock.json src/IssueAgent.Workflow/
RUN --mount=type=cache,id=issueagent-nuget,target=/root/.nuget/packages \
    dotnet restore src/IssueAgent.Host/IssueAgent.Host.csproj --locked-mode
COPY . .
RUN --mount=type=cache,id=issueagent-nuget,target=/root/.nuget/packages \
    dotnet publish src/IssueAgent.Host/IssueAgent.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c AS runtime
ARG TARGETARCH
ARG OMP_VERSION=18.2.4
ARG GIT_LFS_VERSION=3.8.0
WORKDIR /app
RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl git openssh-client ca-certificates util-linux \
    && case "$TARGETARCH" in \
         amd64) omp_asset="omp-linux-x64"; omp_sha256="61b4cd50ceaea70baccae7b52a22034469130ea2985a0b2e9adc0f7b3a77a85f"; lfs_arch="amd64"; lfs_sha256="e455e00f15d9b95661b8d53498ffb0c3367962cf1ec73c31ab7369516cd6ab8d" ;; \
         arm64) omp_asset="omp-linux-arm64"; omp_sha256="59fe68eee9103494016e22d4261c81ac64a3493df5a9b54816bc064a44fad7cf"; lfs_arch="arm64"; lfs_sha256="ac9c8efac980bb0505ead384d087e2acb6486fd8498691a2165fa174ec6118c2" ;; \
         *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
       esac \
    && curl --fail --location --silent --show-error \
         --output /tmp/git-lfs.tar.gz \
         "https://github.com/git-lfs/git-lfs/releases/download/v${GIT_LFS_VERSION}/git-lfs-linux-${lfs_arch}-v${GIT_LFS_VERSION}.tar.gz" \
    && echo "${lfs_sha256}  /tmp/git-lfs.tar.gz" | sha256sum --check --strict \
    && tar --extract --gzip --strip-components=1 \
         --file /tmp/git-lfs.tar.gz \
         --directory /usr/local/bin \
         "git-lfs-${GIT_LFS_VERSION}/git-lfs" \
    && chmod 0755 /usr/local/bin/git-lfs \
    && curl --fail --location --silent --show-error \
         --output /usr/local/bin/omp \
         "https://github.com/can1357/oh-my-pi/releases/download/v${OMP_VERSION}/${omp_asset}" \
    && echo "${omp_sha256}  /usr/local/bin/omp" | sha256sum --check --strict \
    && chmod 0755 /usr/local/bin/omp \
    && rm -f /tmp/git-lfs.tar.gz \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --create-home --uid 10001 issueagent \
    && useradd --create-home --uid 10002 omp \
    && mkdir --parents /data/omp/agent \
    && chown --recursive issueagent:issueagent /data \
    && chmod 2770 /data /data/omp /data/omp/agent
COPY docker/issue-agent-entrypoint.sh /usr/local/bin/issue-agent-entrypoint
COPY docker/omp-unprivileged.sh /usr/local/bin/omp-unprivileged
RUN chmod 0755 /usr/local/bin/issue-agent-entrypoint /usr/local/bin/omp-unprivileged
COPY --from=build /app/publish .
USER root
ENV HOME=/data \
    PI_CODING_AGENT_DIR=/data/omp/agent \
    PI_CODING_AGENT_SESSION_DIR=/data/omp \
    ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=6 \
    CMD curl --fail --silent http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["/usr/local/bin/issue-agent-entrypoint", "dotnet", "IssueAgent.Host.dll"]
