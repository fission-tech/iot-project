# Build from the repository root: docker build -f src/EdgeGateway/EdgeGateway.Historian/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY schemas/ schemas/
COPY src/IoT.Contracts/ src/IoT.Contracts/
COPY src/EdgeGateway/EdgeGateway.Historian/ src/EdgeGateway/EdgeGateway.Historian/
RUN dotnet publish src/EdgeGateway/EdgeGateway.Historian/EdgeGateway.Historian.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app ./
# Settings come from environment variables (Historian__ConnectionString, Mqtt__Host, ...); see compose.yaml.
USER app
ENTRYPOINT ["dotnet", "EdgeGateway.Historian.dll"]
