# syntax=docker/dockerfile:1
# The core image (#197): the core server and command line, no hosted modules. Build it from the repository's root:
#   docker build -t packmoat-core:local dotnet
# Base images are pinned by digest (the tag is kept for people). To take updates, replace each digest with the current one for its tag.
# Both digests are multi-arch lists (linux/amd64 and linux/arm64), so one Dockerfile builds either (#233 E2):
#   docker buildx build --platform linux/arm64 -t packmoat-core:arm64 --load dotnet
# The build stage runs on the machine's own platform and publishes for the target's, so no .NET build runs under emulation.
# Running it: docs/deploy/README.md.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
# The commit being built. It lands in the informational version, "1.0.0+<sha>", which /healthz reports. A local build reports "ok unknown".
ARG PACKMOAT_COMMIT=unknown
# The image's architecture, set by BuildKit: amd64 or arm64. dotnet publish takes either name.
ARG TARGETARCH
RUN dotnet publish src/Packmoat.Server/Packmoat.Server.csproj -c Release -a "$TARGETARCH" -o /out/host  -p:SourceRevisionId="${PACKMOAT_COMMIT}" \
 && dotnet publish src/Packmoat.Cli/Packmoat.Cli.csproj       -c Release -a "$TARGETARCH" -o /out/admin -p:SourceRevisionId="${PACKMOAT_COMMIT}"

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
# Unprivileged user. /data is the only writable path and holds the SQLite database and backups.
RUN groupadd --gid 10001 packmoat && useradd --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin packmoat \
 && mkdir -p /data/backups && chown -R packmoat:packmoat /data
COPY --from=build /out/host  /app/host
COPY --from=build /out/admin /app/admin
# The operator tool: docker exec <container> packmoat-admin tenant list
RUN printf '#!/bin/sh\nexec dotnet /app/admin/Packmoat.Cli.dll "$@"\n' > /usr/local/bin/packmoat-admin && chmod 755 /usr/local/bin/packmoat-admin

ENV ASPNETCORE_URLS=http://127.0.0.1:8080 \
    PACKMOAT_DB=/data/packmoat.db \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
# PACKMOAT_MASTER_KEY and PACKMOAT_PUBLIC_BASE_URL are supplied at run time (env file), never baked in.
# PORT, when set at run time, moves the server to http://+:PORT (every interface) and the health check with it (Program.cs).
USER 10001:10001
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/${PORT:-8080} && printf "GET /healthz HTTP/1.0\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200"' || exit 1
ENTRYPOINT ["dotnet", "/app/host/Packmoat.Server.dll"]
