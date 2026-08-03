# syntax=docker/dockerfile:1.7
#
# The web image (the backend-for-frontend).
#
# Built from the repository root, because the client package is generated from
# contracts/openapi.json and the workspace needs all of it:
#
#   docker build -f frontend/Dockerfile -t mautodesk-web .
#
# This tier holds the session key and the only credential in the system that the
# browser never sees, so it runs as its own non-root user with a runtime image
# that contains no package manager and no compiler.

# ------------------------------------------------------------ dependencies --
FROM node:24-bookworm-slim AS deps
WORKDIR /repo

RUN corepack enable

# Manifests and the lockfile first: dependencies change far less often than
# code, and this is the layer worth keeping across builds.
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./frontend/
COPY frontend/apps/web/package.json ./frontend/apps/web/
COPY frontend/packages/api-client/package.json ./frontend/packages/api-client/
COPY frontend/packages/ui/package.json ./frontend/packages/ui/
COPY frontend/e2e/package.json ./frontend/e2e/

WORKDIR /repo/frontend
RUN --mount=type=cache,target=/pnpm-store \
    pnpm config set store-dir /pnpm-store \
    && pnpm install --frozen-lockfile --filter @mautodesk/web... --filter @mautodesk/api-client...

# ------------------------------------------------------------------ build --
FROM deps AS build
WORKDIR /repo

# The generated contract, then the source. The client package is built from
# openapi.json, so a contract change must invalidate this layer.
COPY contracts/openapi.json ./contracts/openapi.json
COPY frontend/ ./frontend/

WORKDIR /repo/frontend

# The API client is a workspace dependency compiled from TypeScript; Next will
# not transpile it out of node_modules unless it has been built.
RUN pnpm --filter @mautodesk/api-client build

# A build-time value, unlike everything else here. Next inlines NEXT_PUBLIC_*
# at build time, and the session secret must NOT be one of them — it is read at
# runtime, from the environment, and never baked into an image layer.
ARG NEXT_PUBLIC_APP_NAME=MautoDesk
ENV NEXT_PUBLIC_APP_NAME=${NEXT_PUBLIC_APP_NAME}

RUN pnpm --filter @mautodesk/web build

# ---------------------------------------------------------------- runtime --
FROM node:24-bookworm-slim AS runtime
WORKDIR /app

ENV NODE_ENV=production \
    PORT=3000 \
    HOSTNAME=0.0.0.0

# The standalone output carries its own traced node_modules, so nothing is
# installed here and no package manager is present in the final image.
COPY --from=build /repo/frontend/apps/web/.next/standalone ./
# Static assets are not traced into the standalone output and have to be copied
# alongside it. There is no `public/` directory today — add a COPY for it here
# if one appears, or its contents will 404 in the container and nowhere else.
COPY --from=build /repo/frontend/apps/web/.next/static ./apps/web/.next/static

RUN useradd --system --uid 10002 --no-create-home --shell /usr/sbin/nologin mautodesk \
    && chown -R mautodesk:mautodesk /app
USER 10002

EXPOSE 3000

HEALTHCHECK --interval=15s --timeout=3s --start-period=15s --retries=3 \
    CMD node -e "fetch('http://127.0.0.1:3000/').then(r => process.exit(r.ok ? 0 : 1)).catch(() => process.exit(1))"

# The standalone server, at the path pnpm's workspace layout puts it.
CMD ["node", "apps/web/server.js"]
