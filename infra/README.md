# Infrastructure — Terraform for DigitalOcean and Cloudflare

**Status: not yet implemented.** Phase 12. Infrastructure is reviewed in pull requests like any other
code.

## Intended layout

```
modules/
  network/        VPC, firewall rules, origin lock-down
  database/       Managed PostgreSQL: primary + standby, PITR, backup retention
  cache/          Managed Valkey
  storage/        Cloudflare R2 buckets, lifecycle rules, access policies
  app/            App Platform components (web, api, public-api, worker, ocr-worker)
  cloudflare/     DNS, WAF, CDN, rate limiting, Turnstile, bot management
  observability/  Grafana Cloud + Sentry wiring, alert rules
environments/
  staging/
  production/
```

## What runs where

| Concern | Service |
| --- | --- |
| Compute | DigitalOcean App Platform (v1) → DOKS when per-component autoscaling or private networking control is needed (`TD-003`) |
| Database | DigitalOcean Managed PostgreSQL 16, primary + standby, PITR |
| Cache | DigitalOcean Managed Valkey |
| Object storage | Cloudflare R2 — chosen over DO Spaces for zero egress fees; photo delivery is the dominant egress cost |
| Edge | Cloudflare: DNS, TLS, WAF, CDN, bot management, Turnstile, rate limiting |
| Secrets | DigitalOcean app secrets for bootstrap values only. Per-tenant integration credentials are encrypted in the database, never in config |

## The gap to be honest about

**DigitalOcean has no managed KMS or HSM.** The envelope-encryption master key (ADR-0007) lives in
the platform secret store, which is weaker than a hardware-backed key. Compensating controls are in
place — per-record data keys, tenant and record IDs bound as additional authenticated data, rotation
runbook, restricted access, full audit — but this is tracked as a **known finding**, `RISK-SEC-001`,
to be closed before the first customer with a formal security programme.

`IDataKeyProvider` is the same interface an AWS KMS or Azure Key Vault backend would implement, so
closing it is a provider swap, not a refactor.

## Operational commitments

- **Restores are rehearsed quarterly.** An unrehearsed backup is a hope, not a control. RPO ≤ 15 min,
  RTO ≤ 4 h.
- **Origin lock-down:** the DigitalOcean origin accepts traffic only from Cloudflare.
- **Staging never holds production PII.** Synthetic and anonymized data only.
- **Migrations are expand/contract**, backward-compatible for one release, so a deploy needs no
  downtime and a rollback never strands the database ahead of the code.
- Annual third-party penetration test and biannual vulnerability assessment — an FTC Safeguards
  requirement, so it is scheduled, not aspirational.
