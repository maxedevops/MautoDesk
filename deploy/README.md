# Single-node deployment

Everything needed to run MautoDesk on one VPS: TLS, the two application images,
PostgreSQL, object storage, and the malware scanner uploads are checked against.

**This is not the topology `docs/02-architecture.md` describes for scale.** That
one is managed PostgreSQL, Cloudflare R2, and a CDN, and lives in `infra/` as
Terraform. This is the shape that fits on one machine. The compromises are named
in §4 rather than discovered later.

---

## 1. What you need

- A host with **4 GB RAM minimum** — ClamAV alone holds up to 2 GB of signature
  databases. Below that, uploads fail rather than degrade, because scanning is
  fail-closed by design.
- Docker with Compose v2.
- Two DNS records already pointing at the host: one for the app, one for photos.
  Caddy issues certificates over ACME on first start and cannot do that before
  the names resolve.

## 2. First run

```bash
cp deploy/env.production.example .env.production
```

Fill in every blank. Each secret is 32 bytes of base64:

```bash
openssl rand -base64 32
```

Bring up the data tier and apply the schema before anything else starts:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production up -d postgres minio minio-init
```

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production --profile migrate run --rm migrate
```

That job applies every migration, sets the application role's password, asserts
`app.rls_coverage_gaps()` returns zero, and runs the cross-tenant isolation
probe. **A non-zero exit means do not deploy** — the tenant isolation the whole
system rests on is not in place.

Then the rest:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production up -d
```

ClamAV takes around three minutes on a cold start to load its signature
databases. Uploads are refused until it is ready, and that is the intended
behaviour rather than a bug to wait out.

## 3. Deploying a change

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production --profile migrate run --rm migrate
```

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build
```

Migrations run first and are expand/contract by policy (`CLAUDE.md`), so the
schema stays compatible with the running application for one release. That
ordering is what makes a deploy safe to roll back by retagging the image.

## 4. What this topology costs you

Named here because each is a real limit, not a rough edge:

| Compromise | Consequence |
| --- | --- |
| **One API instance** | Rate limiter partitions are in-process. A second replica doubles every attacker's budget, so scale up rather than out until that moves to Valkey |
| **Photos served from this host** | ADR-0005 chose R2 for zero egress fees, because photo delivery is the dominant egress cost. Here it comes out of the VPS's transfer allowance |
| **Storage calls hairpin through Caddy** | The API signs URLs for the public media host and therefore also fetches through it. It buys one URL that both the API and the browser can use. If the host does not support hairpin NAT, the API cannot reach its own public name — add `extra_hosts: ["${MEDIA_DOMAIN}:172.17.0.1"]` to the `api` service so it resolves internally |
| **Photo verification is synchronous** | Decode and re-encode happen inline on confirm. A burst of uploads is the first thing that will saturate a small host |
| **No standby, no PITR** | `postgres-data` is a Docker volume on one disk. §5 is not optional |

## 5. Backups

Two things are unrecoverable if lost, and neither is the database alone.

**PostgreSQL** — everything except photos:

```bash
docker compose -f docker-compose.prod.yml exec -T postgres pg_dump -U postgres -Fc mautodesk > mautodesk-$(date +%F).dump
```

**The `minio-data` volume** — every photo. Snapshot the volume or mirror the
buckets with `mc`.

**`ENCRYPTION_MASTER_KEY`** — keep a copy somewhere that is not this machine.
TOTP secrets and every encrypted column are unrecoverable without it. A backup
of the database taken without the key is not a backup of the data.

Restores must be rehearsed. An untested backup is a hypothesis.

## 6. Behind Cloudflare

If you put Cloudflare in front of this, **add its published ranges to
`Network__TrustedProxies`** on the `api` service. They are public addresses, so
they are not in the default trust list — and without them the origin sees every
request as coming from Cloudflare, which collapses the per-IP authentication
limit into a single shared bucket and silently disables the control that stops
credential stuffing. `docs/06-backend.md` §11 has the detail.
