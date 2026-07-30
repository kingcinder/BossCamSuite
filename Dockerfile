# ── Build stage: Svelte management UI ─────────────────────────
FROM node:24-alpine AS ui-build
WORKDIR /ui
COPY src/BossCam.ManagementUI/package*.json ./
RUN npm ci || npm install
COPY src/BossCam.ManagementUI/ ./
RUN npm run build

# ── Build stage: .NET service ─────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
WORKDIR /src
COPY BossCamSuite.Linux.sln Directory.Build.props ./
COPY src/ ./src/
COPY tests/ ./tests/
COPY --from=ui-build /ui/src/BossCam.Service/wwwroot ./src/BossCam.Service/wwwroot
RUN dotnet restore BossCamSuite.Linux.sln
RUN dotnet build BossCamSuite.Linux.sln -c Release --no-restore
RUN dotnet publish src/BossCam.Service/BossCam.Service.csproj -c Release --no-restore -o /app

# ── Runtime stage ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet-build /app /app
EXPOSE 5317

ENV ASPNETCORE_URLS=http://+:5317
ENV BOSSCAM_BIND=0.0.0.0
ENV BOSSCAM_PORT=5317
# Must be set for non-loopback binds. Generate with: openssl rand -hex 32
# ENV BOSSCAM_LAN_TOKEN=

VOLUME ["/root/.local/share/BossCamSuite"]

ENTRYPOINT ["dotnet", "/app/BossCam.Service.dll"]
