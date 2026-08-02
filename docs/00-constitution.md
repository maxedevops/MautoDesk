# MautoDesk — Project Constitution

> This is the governing specification for MautoDesk, captured verbatim from the project owner.
> All subsequent phase documents derive from and must remain consistent with this file.
> Amendments to this document require an explicit decision record in `docs/decisions/`.

---

## Roles

Design and build as an elite team consisting of: Principal Software Architect · Senior Full-Stack
Engineer · DevSecOps Engineer · Cloud Infrastructure Architect · Database Architect · Cybersecurity
Engineer · AI/ML Engineer · UI/UX Designer · QA/Test Automation Engineer · Performance Engineer ·
API Architect · Product Manager.

## Mission

Build **MautoDesk**, a production-quality, modern, web-based dealership management system for
independent and small used car dealerships. It competes with products like AutoManager while
providing a dramatically better user experience, modern architecture, AI-powered automation,
superior performance, stronger security, and simplified workflows.

**Never generate prototype-quality code unless explicitly instructed.**

Every design decision must prioritize, in order:

1. Security 2. Reliability 3. Performance 4. Scalability 5. Maintainability
6. Clean architecture 7. User experience 8. Data integrity 9. Automation 10. Extensibility

## Primary design principles

Secure by default · Multi-tenant SaaS · Mobile responsive · API-first · Cloud native · Highly
modular · Event-driven where appropriate · Fast even with large inventories · Easily expandable ·
Easy to maintain · Simple for dealership employees to learn.

Every feature integrates naturally with every other feature. Avoid isolated modules. Everything
shares data through well-defined services.

## Technology stack

**Frontend:** Next.js (latest stable), React, TypeScript, Tailwind CSS, React Query, Zod, React Hook
Form, Framer Motion, TanStack Table, Zustand.

**Backend:** Preferred ASP.NET Core (.NET); alternative NestJS if requested. Architecture: Clean
Architecture, CQRS where beneficial, Repository Pattern, Dependency Injection, Service Layer,
Validation Layer, Authentication Layer, Authorization Layer, Logging Layer, Caching Layer,
Notification Layer, Background Job Layer.

**Database:** PostgreSQL, Redis, Entity Framework Core.
**Object storage:** Azure Blob Storage or Amazon S3.
**Search:** PostgreSQL full-text search initially; expandable to Elasticsearch/OpenSearch.

## Security requirements (non-negotiable)

RBAC · least privilege · tenant isolation · JWT authentication · refresh tokens · MFA · OAuth
support · encrypted secrets · Argon2id or bcrypt password hashing · HTTPS only · TLS everywhere ·
encryption at rest · encryption in transit · CSRF protection · CORS protection · XSS prevention ·
SQL injection prevention · parameterized queries · input validation · output encoding · rate
limiting · request throttling · IP reputation support · session expiration · refresh token rotation
· account lockout · audit logging · immutable audit events · security headers · Content Security
Policy · secure cookies · HttpOnly cookies · SameSite protection · sensitive data masking ·
automatic log redaction · secure document storage · object access policies · document versioning ·
hash verification · secure upload validation · virus scanning support · file type verification ·
maximum upload limits.

## Compliance

Support for: FTC Safeguards Rule · ESIGN · UETA · GDPR readiness · CCPA readiness · audit retention
· record retention · privacy controls · consent tracking.

## Multi-tenant SaaS architecture

A single SaaS platform serving many dealerships. Every dealership has: users, inventory, customers,
deals, documents, reports, settings, billing, templates, notifications.

**No dealership can access another dealership's information.** Tenant isolation must exist at every
layer: database, API, caching, storage, authentication, authorization, background jobs, search,
exports, reports.

## Application modules

Dashboard · Inventory · CRM · Customers · Leads · Sales · Deals · Trade-ins · Financing · Accounting
· Documents · OCR · Vehicle Photos · AI Descriptions · Marketplace Publishing · Website Publishing ·
Reporting · Analytics · Service Department · Appointments · Messaging · Notifications · Tasks ·
Calendar · Settings · User Management · Roles · Permissions · Audit Logs · Billing · Subscription
Management · API Integrations · System Administration.

## Canonical data flow

Acquire Vehicle → Enter VIN → VIN Decode → Populate Vehicle Details → Upload Photos → AI Image
Enhancement → Background Removal → OCR (optional) → Vehicle Description Generation → Pricing
Suggestions → Inventory Saved → Website Published → Marketplace Published → QR Code Generated →
Lead Generation → CRM → Appointment → Quote → Buyer Order → Financing → Documents → Electronic
Signature → Completed Sale → Accounting → Reporting → Archive.

**No duplicate data entry.** Whenever possible, data entered once populates every downstream
workflow.

## Module detail

**Inventory:** VIN decoding · photo management · vehicle specifications · condition · pricing ·
acquisition costs · reconditioning costs · features · factory options · location tracking · vehicle
history · internal notes · status · QR code generation · inventory aging · price changes · AI
descriptions · AI pricing suggestions · vehicle timeline.

**VIN decoder:** Integrate the free NHTSA VIN Decoder initially. Design an abstraction layer allowing
replacement with commercial providers. Store: year, make, model, trim, engine, transmission, drive
type, fuel, body, factory options.

**Photo pipeline:** bulk upload · drag-and-drop · image optimization · compression · auto resize ·
background removal · AI enhancement · watermarks · ordering · captions · thumbnail generation · CDN
delivery · lazy loading.

**OCR pipeline:** Upload → OpenCV preprocessing → PaddleOCR → LLM field extraction → validation →
JSON → database. Supported documents: driver license, title, registration, insurance, purchase
orders, bills of sale, odometer statements, trade documents, finance documents. Keep: original
image, OCR text, JSON extraction, corrections, confidence scores.

**AI features:** vehicle descriptions · customer replies · pricing suggestions · lead summaries ·
deal summaries · sales recommendations · vehicle merchandising · follow-up generation · email
drafting · text drafting · document summarization. Future AI integrations plug into a common AI
service layer.

**CRM:** customers · leads · tasks · reminders · calls · emails · texts · appointments · notes ·
tags · status · lead scoring · timeline · communication history.

**Sales:** quotes · deals · trade-ins · deposits · buyer orders · purchase agreements · finance ·
insurance · commission · gross profit · vehicle delivery · deal jacket.

**Document management:** driver licenses · insurance · titles · contracts · inspection forms ·
photos · scanned documents · invoices · purchase paperwork · finance paperwork. Everything version
controlled. Everything searchable.

**Digital signatures:** Document Service · Signature Service · Document Vault. Capture: signature
image, printed name, timestamp (UTC), document hash, completed hash, browser, IP (where
appropriate), device, user, audit trail. Signed PDFs become immutable.

**Reporting:** inventory aging · gross profit · net profit · vehicle turn time · salesperson
performance · lead sources · marketing · expenses · commissions · taxes · deal pipeline ·
appointments · service · financial dashboard. Every report supports filtering, export, CSV, Excel,
PDF, and printing.

## API design

RESTful · versioned · OpenAPI · Swagger · consistent response format · pagination · filtering ·
sorting · searching · validation · error handling · rate limiting · idempotency where appropriate.

## Database design

Normalize appropriately. Index strategically. Support: soft delete · audit fields (created,
modified, deleted) · tenant ID · concurrency tokens · optimistic concurrency · transactions ·
migration history.

## Performance

Targets: sub-200 ms API responses where practical · fast page loads · lazy loading · code splitting
· caching · Redis · background jobs · batch processing · queue processing · image optimization ·
minimal database round trips · efficient queries · avoid N+1 queries.

## Background services

Workers for: emails · SMS · marketplace sync · inventory publishing · OCR · AI generation · reports
· notifications · cleanup · image processing.

## Integrations

NHTSA VIN Decoder · Facebook Marketplace · Cars.com · AutoTrader · dealer website · email · SMS ·
payment processors · calendar · accounting software · future APIs through adapters.

| Feature | Provider |
| --- | --- |
| Auction inventory | API Auctions or Apibara |
| VIN decode | NHTSA VIN Decoder API |
| Book values | J.D. Power or Black Book |
| CarFax/AutoCheck | Commercial APIs |
| Dealer websites | Custom integration |
| CRM | Built into the DMS |
| Accounting | QuickBooks API or Xero API |

## User experience

Simple · modern · fast · minimal clicks · dashboard driven · context-aware navigation · dark mode ·
accessibility compliant · keyboard shortcuts · responsive · mobile friendly.

## Testing

Every feature requires unit tests, integration tests, API tests, security tests, performance tests,
UI tests, end-to-end tests, and regression tests. **Never produce untested production code.**

## Logging & monitoring

Structured logging · audit logs · application logs · security logs · performance metrics · health
monitoring · distributed tracing. Health endpoints · metrics · alerts · error tracking · crash
reporting · performance monitoring.

## Deployment

Containerized · Docker · CI/CD · GitHub Actions · Infrastructure as Code · automatic migrations ·
rollback support. Environment separation: development, testing, staging, production.

## Project organization

Maintain a clean repository. Separate: frontend · backend · shared contracts · infrastructure ·
database · documentation · tests · scripts.

## Documentation

Generate documentation continuously: architecture · ER diagrams · API documentation · deployment
guides · developer guides · admin guides · user manuals · database documentation · security
documentation.

## Development process

**Never jump directly into coding. Always proceed in phases.**

| Phase | Focus |
| --- | --- |
| 1 | Requirements analysis |
| 2 | Architecture |
| 3 | Database design |
| 4 | API contracts |
| 5 | UI/UX |
| 6 | Backend |
| 7 | Frontend |
| 8 | Authentication |
| 9 | Security review |
| 10 | Testing |
| 11 | Performance optimization |
| 12 | Deployment |
| 13 | Documentation |

After every completed phase: review architecture, identify technical debt, recommend improvements,
refactor where beneficial. Do not duplicate logic. Do not create unnecessary complexity.

## Output requirements

For every feature generated:

1. Explain the architecture.
2. Explain why the design was chosen.
3. Identify security considerations.
4. Identify scalability considerations.
5. Identify performance considerations.
6. Identify future expansion opportunities.
7. Generate production-quality code.
8. Generate tests.
9. Generate documentation.
10. Ensure compatibility with every existing module.

Where multiple implementations are possible, recommend the most secure, maintainable, and scalable
solution, even if it requires additional development effort.

## Goal

Not merely to build software, but to create an enterprise-grade dealership management platform that
can evolve into one of the most capable, secure, high-performance, AI-enabled Dealer Management
Systems available — while remaining intuitive for small dealerships.
