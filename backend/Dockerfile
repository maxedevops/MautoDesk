# syntax=docker/dockerfile:1.7
#
# The API image.
#
# Built from the repository root so the whole solution is in context:
#
#   docker build -f backend/Dockerfile -t mautodesk-api .
#
# Multi-stage, because the SDK is ~800 MB and none of it is needed at runtime.
# The final image carries the ASP.NET runtime, the published output, and the two
# native libraries SkiaSharp needs — and nothing else that a shell could use.

# ---------------------------------------------------------------- restore --
FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS restore
WORKDIR /src

# Manifests first, so a code change does not invalidate the restore layer. This
# is the difference between a 15-second rebuild and a two-minute one on a VPS.
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/MautoDesk.sln ./backend/
COPY NuGet.config ./

# .editorconfig is not cosmetic here: Directory.Build.props turns analyzer
# warnings into errors, and this file is where the deliberate exceptions live.
# Omitting it fails the build on rules the repository has consciously waived.
COPY .editorconfig ./
COPY backend/src/MautoDesk.SharedKernel/*.csproj ./backend/src/MautoDesk.SharedKernel/
COPY backend/src/MautoDesk.Infrastructure/*.csproj ./backend/src/MautoDesk.Infrastructure/
COPY backend/src/MautoDesk.Api/*.csproj ./backend/src/MautoDesk.Api/
COPY backend/src/Modules/Inventory/MautoDesk.Inventory.Domain/*.csproj ./backend/src/Modules/Inventory/MautoDesk.Inventory.Domain/
COPY backend/src/Modules/Inventory/MautoDesk.Inventory.Contracts/*.csproj ./backend/src/Modules/Inventory/MautoDesk.Inventory.Contracts/
COPY backend/src/Modules/Inventory/MautoDesk.Inventory.Application/*.csproj ./backend/src/Modules/Inventory/MautoDesk.Inventory.Application/
COPY backend/src/Modules/Inventory/MautoDesk.Inventory.Infrastructure/*.csproj ./backend/src/Modules/Inventory/MautoDesk.Inventory.Infrastructure/
COPY backend/src/Modules/Identity/MautoDesk.Identity.Domain/*.csproj ./backend/src/Modules/Identity/MautoDesk.Identity.Domain/
COPY backend/src/Modules/Identity/MautoDesk.Identity.Contracts/*.csproj ./backend/src/Modules/Identity/MautoDesk.Identity.Contracts/
COPY backend/src/Modules/Identity/MautoDesk.Identity.Application/*.csproj ./backend/src/Modules/Identity/MautoDesk.Identity.Application/
COPY backend/src/Modules/Identity/MautoDesk.Identity.Infrastructure/*.csproj ./backend/src/Modules/Identity/MautoDesk.Identity.Infrastructure/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore backend/src/MautoDesk.Api/MautoDesk.Api.csproj

# ---------------------------------------------------------------- publish --
FROM restore AS publish
COPY backend/src/ ./backend/src/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish backend/src/MautoDesk.Api/MautoDesk.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        # Not trimmed: the audit ledger, the log-redaction policy, and EF all
        # walk types by reflection, and trimming quietly removes what they need.
        # A smaller image is not worth a runtime failure in the one component
        # whose job is to be trustworthy.
        /p:PublishTrimmed=false

# ---------------------------------------------------------------- runtime --
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS runtime

# SkiaSharp decodes and re-encodes every uploaded photo, and its native library
# needs these two. Without them the app starts happily and fails on the first
# upload — which reads as a photo bug rather than a packaging one, so they are
# installed here rather than discovered in production.
# curl is here only for the health check below; the runtime image ships without
# any HTTP client, and a health check that cannot run is worse than none.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 libfreetype6 curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=publish /app/publish ./

# A non-root user with no login shell. The app writes nothing to its own
# filesystem — photos go to object storage, state goes to PostgreSQL — so the
# only thing a writable image buys an attacker is somewhere to stage a payload.
RUN useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin mautodesk \
    && chown -R mautodesk:mautodesk /app
USER 10001

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# /health/ready checks the database; /health/live does not. Readiness is the
# right probe for a container that must not receive traffic before PostgreSQL
# answers, which on a cold VPS boot is a real interval.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "MautoDesk.Api.dll"]
