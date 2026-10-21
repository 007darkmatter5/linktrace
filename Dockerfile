# Linktrace container image. Built for linux/amd64 and linux/arm64 by .github/workflows/release.yml.
#   docker build --build-arg VERSION=1.0.0-local -t linktrace .

# Compile on the build machine's own architecture and cross-publish for the target (no emulation needed).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

# Restore only after the full source is copied. Restoring from the .csproj alone (the usual layer-caching
# trick) makes the SDK leave out Blazor's framework scripts (_framework/blazor.web.js), and the app then
# renders but never becomes interactive.
COPY src/ src/
RUN dotnet restore src/Linktrace/Linktrace.csproj -a $TARGETARCH \
    && dotnet publish src/Linktrace/Linktrace.csproj -c Release -a $TARGETARCH --no-restore -o /out/app -p:Version=$VERSION \
    && test -f /out/app/wwwroot/_framework/blazor.web.js \
    && mkdir -p /out/data /out/keys

# No RUN steps below: the final stage is for the target architecture and must build without emulation.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="Linktrace" \
      org.opencontainers.image.description="Trace network cabling and port-to-port connections" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/007darkmatter5/linktrace"

WORKDIR /app
COPY --from=build /out/app .
COPY --from=build --chown=1654:1654 /out/data /data
COPY --from=build --chown=1654:1654 /out/keys /keys
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/linktrace-entrypoint

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__DefaultConnection="Data Source=/data/linktrace.db;Default Timeout=30" \
    DataProtection__KeysPath=/keys \
    PUID=1654 \
    PGID=1654

# The database lives in /data; the keys that sign people's login cookies in /keys.
VOLUME ["/data", "/keys"]
EXPOSE 8080

# Starts as root only to give /data and /keys to PUID:PGID, then runs Linktrace as that user (see entrypoint.sh).
ENTRYPOINT ["linktrace-entrypoint"]
