# ============================================================================
# Couple OS API — build context is the repository root, not this directory:
#
#   docker compose --profile app up -d --build
#
# See .dockerignore for what is deliberately kept out of the context, and why
# host bin/obj must never reach it.
# ============================================================================

# --- Build -----------------------------------------------------------------
# global.json pins 10.0.302 with rollForward: latestFeature, and this digest
# carries 10.0.400, which satisfies it. If a future tag ever drops below the
# pinned feature band the SDK refuses to run at all, which is the correct
# failure: loud, at build time, naming the version it wanted.
#
# Pinned by digest as of 2026-08-14 (STATUS debt 14). The tag is kept beside it
# for readability only — Docker resolves the digest and ignores the tag, so the
# two cannot disagree in a way that changes the build. The cost is that security
# patches now arrive when someone re-pins rather than on the next build, which is
# the trade a deployment wants and a laptop does not.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS runtime

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

# The two directories the app writes to: ASP.NET's data protection key ring,
# which encrypts antiforgery tokens, and the attachment bytes (ADR 0014).
# Compose mounts a named volume over each so both outlive the container.
#
# They have to exist in the image, owned by the runtime user, and that is the
# whole reason for these lines. Docker seeds a new named volume from the image
# directory it covers — including its ownership — but only if that directory
# exists. Create the volume over a path the image does not have and Docker makes
# it root-owned; the app then starts as uid 1654 and cannot write to its own
# volume.
#
# Which has now happened twice. The first time every page 500ed on a key ring it
# could not write. The second was this line's absence: attachments shipped with
# the volume and the compose entry and without the mkdir, and the first upload in
# a browser came back "Permission denied" on a directory the container owns
# everywhere except where it counts. The comment above was already here and
# describing it.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys /var/lib/coupleos/attachments \
 && chown -R $APP_UID:$APP_UID /home/app/.aspnet /var/lib/coupleos

# Provided by the base image (uid 1654). The app binds 8080, so it needs no
# privileged port, and it writes nothing to disk except the key ring above.
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
