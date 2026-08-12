# ============================================================================
# Couple OS API — build context is the repository root, not this directory:
#
#   docker compose --profile app up -d --build
#
# See .dockerignore for what is deliberately kept out of the context, and why
# host bin/obj must never reach it.
# ============================================================================

# --- Build -----------------------------------------------------------------
# global.json pins 10.0.302 with rollForward: latestFeature. The floating 10.0
# tag currently carries 10.0.400, which satisfies it. If a future tag ever
# drops below the pinned feature band the SDK refuses to run at all, which is
# the correct failure: loud, at build time, naming the version it wanted.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore before the sources, so editing a .cs file does not re-resolve the
# package graph. The three root files are part of restore input, not extras:
# Directory.Build.props holds TargetFramework, and Directory.Packages.props
# holds every package version in the repository (central management), so a
# restore without them resolves nothing.
COPY global.json Directory.Build.props Directory.Packages.props ./

# Only the five projects the API actually needs. CoupleOS.Workers is absent
# because nothing in this graph references it; listing csproj files one by one
# rather than globbing keeps that fact checkable.
COPY src/CoupleOS.Domain/CoupleOS.Domain.csproj                 src/CoupleOS.Domain/
COPY src/CoupleOS.Application/CoupleOS.Application.csproj       src/CoupleOS.Application/
COPY src/CoupleOS.Infrastructure/CoupleOS.Infrastructure.csproj src/CoupleOS.Infrastructure/
COPY src/CoupleOS.AI/CoupleOS.AI.csproj                         src/CoupleOS.AI/
COPY src/CoupleOS.Api/CoupleOS.Api.csproj                       src/CoupleOS.Api/
RUN dotnet restore src/CoupleOS.Api/CoupleOS.Api.csproj

COPY src/ src/

# TreatWarningsAsErrors is on repository-wide (Directory.Build.props), so this
# step is also the build gate: a warning introduced anywhere in the graph stops
# the image from being produced.
RUN dotnet publish src/CoupleOS.Api/CoupleOS.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app

# --- Runtime ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# The image ships no HTTP client — no curl, no wget, no netcat (verified
# against aspnet:10.0 on Ubuntu 24.04). Without one, HEALTHCHECK has nothing
# to call and `docker compose ps` can only ever report the api container as
# running, never healthy, which is precisely what M0's exit criterion asks for.
# curl also makes the container debuggable from the inside with docker exec.
RUN apt-get update \
 && apt-get install --yes --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# Provided by the base image (uid 1654). The app binds 8080, so it needs no
# privileged port, and it writes nothing to disk.
USER $APP_UID

# Documentation only — the published port is compose's business. Kestrel binds
# whatever ASPNETCORE_URLS says, which compose sets to http://+:8080.
EXPOSE 8080

# /health returns 503 unless the database answers, so a healthy container means
# the application can reach Postgres as the non-superuser app role — not merely
# that a process is alive. start-period covers first-run JIT and the seeder.
#
# The body is matched rather than trusting the exit code alone. `curl --fail`
# treats a 3xx as success, and UseHttpsRedirection would answer 307 here if an
# HTTPS port were ever configured — so an exit-code-only probe could report
# healthy having never reached /health at all.
HEALTHCHECK --interval=10s --timeout=5s --retries=5 --start-period=30s \
  CMD curl --fail --silent --show-error http://127.0.0.1:8080/health | grep -qx Healthy

ENTRYPOINT ["dotnet", "CoupleOS.Api.dll"]
