FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /source
COPY Directory.Build.props ./
COPY schemas/ schemas/
COPY src/IoT.Contracts/ src/IoT.Contracts/
COPY src/EdgeGateway/EdgeGateway.Gateway/ src/EdgeGateway/EdgeGateway.Gateway/
COPY src/EdgeGateway/EdgeGateway.Configuration/ src/EdgeGateway/EdgeGateway.Configuration/
COPY deploy/dotnet/simulator/ deploy/dotnet/simulator/
COPY src/EdgeGateway/EdgeGateway.Historian/ src/EdgeGateway/EdgeGateway.Historian/
RUN --mount=type=cache,id=dotnet-nuget-${TARGETARCH},target=/root/.nuget/packages,sharing=locked dotnet publish src/EdgeGateway/EdgeGateway.Gateway -a "$TARGETARCH" -c Release -o /publish/gateway /p:UseAppHost=false \
 && dotnet publish src/EdgeGateway/EdgeGateway.Configuration -a "$TARGETARCH" -c Release -o /publish/configuration /p:UseAppHost=false \
 && dotnet publish deploy/dotnet/simulator -a "$TARGETARCH" -c Release -o /publish/simulator /p:UseAppHost=false \
 && dotnet publish src/EdgeGateway/EdgeGateway.Historian -a "$TARGETARCH" -c Release -o /publish/historian /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS gateway
WORKDIR /app
COPY --from=build /publish/gateway/ ./
RUN mkdir -p /state && chown app:app /state
USER app
ENV Gateway__DatabasePath=/state/outbox.db
ENTRYPOINT ["dotnet", "EdgeGateway.Gateway.dll"]
# The existing worker atomically refreshes this file every second. No extra .NET process or API is needed.
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=3 CMD status="${Gateway__DatabasePath%/*}/status.json"; test -f "$status" && age=$(( $(date +%s) - $(stat -c%Y "$status") )) && test "$age" -ge 0 && test "$age" -le 15

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS configuration
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /state && chown app:app /state
COPY --from=build /publish/configuration/ ./
USER app
ENTRYPOINT ["dotnet", "EdgeGateway.Configuration.dll"]
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=3 CMD curl --fail --silent http://127.0.0.1:5300/health || exit 1

FROM eclipse-mosquitto:2.1.2-alpine AS broker
COPY deploy/dotnet/mosquitto.conf /mosquitto/config/mosquitto.conf
USER mosquitto

FROM alpine:3.22 AS certs
RUN apk add --no-cache openssl
COPY deploy/dotnet/certificates.sh /certificates.sh
ENTRYPOINT ["sh", "/certificates.sh"]

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS simulator
WORKDIR /app
COPY --from=build /publish/simulator/ ./
USER app
ENTRYPOINT ["dotnet", "EdgeGateway.Simulator.dll"]

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS historian
WORKDIR /app
COPY --from=build /publish/historian/ ./
RUN mkdir -p /dashboards/devices && chown -R app:app /dashboards
USER app
ENTRYPOINT ["dotnet", "EdgeGateway.Historian.dll"]
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=3 CMD test -f /tmp/historian.ready && age=$(( $(date +%s) - $(stat -c%Y /tmp/historian.ready) )) && test "$age" -ge 0 && test "$age" -le 15

FROM timescale/timescaledb:2.31.2-pg17 AS history-db
COPY history/init/ /docker-entrypoint-initdb.d/
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=6 CMD pg_isready -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"

FROM grafana/grafana:12.4.16 AS grafana
COPY grafana/provisioning/ /etc/grafana/provisioning/
COPY grafana/dashboards/ /etc/grafana/dashboards/
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=6 CMD wget -q -O /dev/null http://127.0.0.1:3000/api/health || exit 1
