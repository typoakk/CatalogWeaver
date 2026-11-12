FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETRID=linux-x64
WORKDIR /src
COPY src/MediaImporter/MediaImporter.csproj src/MediaImporter/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore src/MediaImporter/MediaImporter.csproj -r ${TARGETRID}
COPY src/ src/
COPY adapters/ adapters/
COPY encoders/ encoders/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/MediaImporter/MediaImporter.csproj -c Release -r ${TARGETRID} --self-contained false --no-restore /p:UseAppHost=false -o /out

FROM build AS checks
COPY tests/ tests/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet build tests/MediaImporter.Tests/MediaImporter.Tests.csproj -c Release -r ${TARGETRID}
ENTRYPOINT ["dotnet", "vstest", "tests/MediaImporter.Tests/bin/Release/net10.0/linux-x64/MediaImporter.Tests.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
RUN mkdir -p /data/media /data/config && chown app:app /data/media /data/config
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Importer__AdaptersPath=/app/adapters
ENV Importer__EncodersPath=/app/encoders
ENV Importer__MediaRoot=/data/media
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s --retries=3 CMD curl --fail --silent --max-time 6 http://127.0.0.1:8080/health/ready || exit 1
USER app
ENTRYPOINT ["dotnet", "CatalogWeaver.dll"]
