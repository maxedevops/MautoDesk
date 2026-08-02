# MautoDesk — Phase 5: UI/UX Design

**Status:** Draft for review · **Phase:** 5 of 13
**Inputs:** Phase 1 personas, Phase 2 §8 frontend architecture, Phase 4 API contract
**Artifacts:** `frontend/packages/ui/tokens.css` · `docs/prototypes/inventory.html`

---

## 1. The design problem, stated honestly

The competition is not AutoManager. The competition is **a spiral notebook, a whiteboard, and a
group text.**

Small dealerships abandon DMS software for the same three reasons every time: it is slow, it demands
data the user doesn't have yet, and it takes more taps than the paper equivalent. A beautiful
interface that requires eleven fields to save a vehicle loses to a notebook that requires none.

So the design goal is not "modern and clean." It is:

> **A salesperson standing on a lot, in sunlight, on a phone, with a customer waiting, can do the
> thing faster in MautoDesk than on paper.**

Everything below follows from that. Where visual polish and speed conflict, speed wins.

### 1.1 Design principles

1. **Never block on data the user doesn't have.** A vehicle can be saved with a VIN and nothing
   else. Completeness is shown as progress, not enforced as validation.
2. **The list is the product.** Dealers live in the inventory grid and the lead queue, not on a
   dashboard. Optimize those two screens above all others.
3. **One primary action per screen.** If everything is emphasized, nothing is.
4. **Show the number that decides.** Days in inventory, gross, unanswered leads. Not vanity metrics.
5. **Latency is a design material.** A skeleton that appears in 50 ms feels faster than a spinner
   that appears in 200 ms. Design the loading state before the loaded state.
6. **Permission-shaped, not permission-broken.** A user without `inventory.cost.read` sees a screen
   that looks intentional, not one full of dashes and locked icons.
7. **Nothing important lives only in colour.** Aging, status, and confidence all carry a shape, a
   label, or a position as well.

---

## 2. Information architecture

### 2.1 Navigation model

A persistent left rail, a global command palette, and contextual sub-navigation. Three levels, no
more — a dealer should never be lost.

```
┌────────────┬──────────────────────────────────────────────────────┐
│            │  ⌘K  search anything          [+ New ▾]   🔔   ◐  👤 │
│  MautoDesk ├──────────────────────────────────────────────────────┤
│            │                                                      │
│  ◉ Today   │   Contextual sub-nav (tabs) when the section needs it│
│  ▤ Inventory│  ───────────────────────────────────────────────────│
│  ☺ Customers│                                                     │
│  ⚑ Leads    │                  Primary work surface               │
│  ⛒ Deals    │                                                     │
│  ▦ Documents│                                                     │
│  ▲ Reports  │                                                     │
│            │                                                      │
│  ⚙ Settings │                                                     │
└────────────┴──────────────────────────────────────────────────────┘
```

**Seven top-level destinations.** Not fourteen. The constitution lists ~32 modules; most are not
destinations, they are *things that happen inside* a vehicle, a customer, or a deal. Tasks,
appointments, messaging, photos, OCR, signatures, and publishing all live in context rather than as
peers in the sidebar — because a dealer thinks "this car" or "this deal," never "the OCR module."

| Rail item | What it is | Who lives here |
| --- | --- | --- |
| **Today** | The single most opinionated screen: what needs a human right now | Everyone, first thing each morning |
| **Inventory** | The vehicle grid. The centre of gravity | Everyone |
| **Customers** | People and businesses, with unified timelines | Sales, F&I, office |
| **Leads** | The response queue, sorted by how long someone has been ignored | Sales |
| **Deals** | Pipeline board + deal detail | Sales, F&I, management |
| **Documents** | Cross-cutting search; deal jackets live on the deal | Office / title clerk |
| **Reports** | Aging, gross, turn time, pipeline | Owner, management |

**Settings** is separated at the bottom because it is a different mode — configuration, not work.

### 2.2 The command palette is a first-class navigation surface

`⌘K` / `Ctrl+K` opens a palette that searches vehicles, customers, deals, and documents *and*
executes commands (`new vehicle`, `scan VIN`, `go to aging report`). For a power user — the office
manager who lives in the system eight hours a day — this is faster than any menu, and it means the
sidebar never has to grow to accommodate a rarely-used destination.

**Design requirement:** results appear within 100 ms of the last keystroke, which is why search is
server-side against the `tsvector`/trigram indexes from Phase 3 §6 and debounced at 150 ms with the
previous results kept visible rather than cleared.

### 2.3 What "Today" actually shows

Not a dashboard of charts. A prioritized list of things that will get worse if ignored:

1. **Unanswered leads**, oldest first, with minutes elapsed. (Backed by `lead_unresponded_ix`.)
2. **Appointments today**, with a one-tap "arrived" action.
3. **Deals blocked**, with the specific blocker: "missing insurance," "needs manager approval,"
   "signature pending 3 days."
4. **Aging inventory crossing a threshold** — units that hit 60 or 90 days this week.
5. **Recon overdue** — vehicles sitting in a recon step longer than its target.

Each row is a link to the thing, not a summary of the thing. Charts belong in Reports.

---

## 3. Screen inventory

Release 1. Each screen names its primary action, because a screen without one is a screen nobody
knows what to do with.

| Screen | Primary action | Notes |
| --- | --- | --- |
| Today | (varies by row) | Prioritized work queue |
| Inventory grid | **Add vehicle** | Dense table, server-side paging/sort/filter, saved views |
| Vehicle detail | **Change status** | Tabs: Overview · Photos · Costs · Recon · History · Documents |
| Vehicle quick-add (mobile) | **Scan VIN** | Camera-first, three fields, saves in under 15 s |
| Customer list | **Add customer** | |
| Customer detail | **Start a deal** | Timeline is the main body, not a side panel |
| Lead queue | **Respond** | Sorted by unanswered duration; response timer visible |
| Lead detail | **Log response** | AI-drafted reply presented as a draft to edit, never to send blind |
| Deal pipeline | **New deal** | Kanban by status; drag advances, and validates |
| Deal detail | **Calculate** | The most complex screen in the app; see §5.3 |
| Deal jacket | **Send for signature** | Document checklist with satisfied/missing state |
| Signature ceremony | **Sign** | Signer-facing, mobile-first, no dealer chrome |
| Document list | **Upload** | |
| Reports | **Run** | Filter → table → export |
| Settings | (section-dependent) | Users, roles, fees, templates, integrations, billing |

---

## 4. Design system

Full token definitions are in [`frontend/packages/ui/tokens.css`](../frontend/packages/ui/tokens.css)
and are the single source of truth. What follows is the reasoning.

### 4.1 Colour doctrine

This is a tool someone stares at for eight hours. **Hierarchy comes from typography, whitespace, and
alignment. Colour is the last resort, not the first.** Four rules, enforced in the token file:

**1. Blue is for interaction.** The primary button, links, the focus ring, and the active nav
marker. If blue appears on something you cannot click, that is a bug. The one deliberate exception
is the brand mark in the rail, which is identity rather than affordance.

**2. Semantic colour marks state, and marks it small.** A dot, a 3 px left rule, an aging bar, an
icon — never a filled panel. Eight tinted pills stacked down a grid column is the busiest thing a
dense table can do, and the label already carries the meaning; the dot only speeds up scanning.

**3. Grouping is whitespace and a hairline rule.** A tinted background is not how you say "these
things belong together." Alignment is.

**4. Neutral type does the rest.** Three sizes and three weights separate a page title from a
section head from a row of data, with no colour involved.

| Role | Light | Dark |
| --- | --- | --- |
| Canvas | `#F8FAFC` | `#020617` |
| Card | `#FFFFFF` | `#0F172A` |
| Border | `#E2E8F0` | `rgb(148 163 184 / .14)` |
| Text | `#0F172A` | `#F1F5F9` |
| Muted | `#64748B` | `#94A3B8` |
| Primary (interaction only) | `#2563EB` | `#2563EB` |

### 4.2 Semantic colour is split by job

The same hue cannot do both jobs legibly, so each semantic ships as a **mark** and a **text** value:

| | Mark (dots, rules, bars — needs 3:1) | Text (words — needs 4.5:1) |
| --- | --- | --- |
| Success | `#16A34A` · 3.30:1 | `#15803D` · 5.02:1 |
| Warning | `#D97706` · 3.19:1 | `#B45309` · 5.02:1 |
| Error | `#DC2626` · 4.83:1 | `#B91C1C` · 6.47:1 |

**This split is not pedantry — it is measured.** `#16A34A` and `#D97706` are excellent marks and land
near 3.2:1, which is correct for a non-text indicator under WCAG 1.4.11 but **below the 4.5:1 body
text requires**. Setting a warning *word* in `#D97706` would fail AA. The text values are one step
deeper for exactly that reason, and `contrast.test.mjs` fails the build if either drifts.

A `-wash` (tinted fill) token exists per semantic but is **restricted**: permitted only on a single
inline badge where no rule or dot is available. Never on a callout, card, row, nav item, or section.

### 4.3 Typographic hierarchy

The tool that replaces colour for organising a screen. If a heading needs a coloured background to
read as a heading, the type is doing too little.

| Level | Spec | Colour |
| --- | --- | --- |
| Page (`<h1>`, one per screen) | 20 px / 650 / −0.015em | text primary |
| Section head | 14 px / 600 | text primary |
| Micro-label (field label, table header) | 11 px / 700 / uppercase / +0.06em | muted — the size and caps do the work |
| Body | 14 px / 400 | text primary |
| Data | 14 px / tabular-nums | text primary |

Only the micro-label shifts colour, and only to muted grey — never to accent.

### 4.4 Aging: the one place colour carries real weight

Days in inventory is the number that most changes a dealer's behaviour, and it never relies on hue
alone (WCAG 1.4.1; roughly 1 in 12 men has a colour vision deficiency):

| Bucket | Bar | Numeral |
| --- | --- | --- |
| 0–30 | none | muted, 500 |
| 31–60 · watch | 2 px info | **text primary**, 500 |
| 61–90 · stale | 4 px warning | warning text, 600 |
| 91+ · critical | 6 px danger | error text, 700 |

Bar thickness rises monotonically, so the severity ordering is legible in greyscale and at a glance.
**Only the two buckets that need action colour their numeral** — colouring every row means colouring
nothing.

### 4.5 Dark mode

A first-class theme, not an inversion. Dealers work evenings and the lot office is often dim. Both
themes are authored, and both are measured: **74/74 contrast pairs pass WCAG 2.2 AA**, verified in CI
by `frontend/packages/ui/contrast.test.mjs`.

### 4.3 Type and density

- System font stack. A webfont is a render-blocking request that buys nothing in a data tool.
- Base 14 px in the app shell (dealers view dense tables on 1080p monitors), 16 px in reading
  contexts and on mobile — never below 16 px on an input, or iOS zooms on focus.
- Tabular numerals for every price, cost, mileage, and day count, so columns align and scanning
  works.
- **Three density modes** on the inventory grid — comfortable, compact, dense. The office manager
  wants 40 rows on screen; the salesperson wants photos. This is a saved preference, not a setting
  buried three levels deep.

### 4.4 Component inventory

Built on the Phase 2 stack. Everything below lives in `packages/ui` and is used, not re-invented,
per screen.

| Category | Components |
| --- | --- |
| Layout | AppShell, PageHeader, Section, SplitPane, Drawer, Sheet (mobile) |
| Data | DataTable (TanStack), Column presets, SavedViews, FilterBar, FacetChip, Pagination, EmptyState, SkeletonRow |
| Input | Field, TextInput, MoneyInput, VinInput, PhoneInput, DatePicker, Select, Combobox, Toggle, PhotoDropzone |
| Feedback | Toast, InlineAlert, ProgressJob, ConfirmDialog, PermissionGate |
| Display | StatusDot, AgingIndicator, PhotoGallery, Timeline, KeyValueList, MoneyBreakdown, Avatar |
| Navigation | SideRail, CommandPalette, Tabs, Breadcrumb, ContextMenu |

**`MoneyInput` and `MoneyBreakdown` are not generic components.** They accept and emit *strings*,
never JavaScript numbers, matching the API contract. A `parseFloat` anywhere near a price is a defect
(Phase 4 §11).

---

## 5. Key flows, with click budgets

A click budget is a design constraint, not an aspiration. Exceeding it is a bug.

### 5.1 Acquire a vehicle — target: under 15 seconds, 4 interactions

The constitution's flow begins here, and this is where most DMS products lose the user.

```
[Inventory] → tap ⊕
   ↓
Camera opens directly on the VIN barcode scanner (not a menu, not a form)
   ↓  barcode read
VIN decoded, year/make/model/engine/body filled in and shown for confirmation
   ↓
Two fields only: mileage, stock number (auto-suggested from the sequence)
   ↓
[Save] → vehicle exists, status = acquired
   ↓
Immediately offered: "Add photos" (camera) · "Add cost" · "Done"
```

**Nothing else is required.** Price, description, features, and costs are all added later by whoever
has that information. The screen shows a completeness meter — *"Ready to publish: 3 of 6"* — which
invites completion rather than blocking the save.

**Manual VIN entry is always one tap away**, because barcodes on windshields are scratched, dirty,
and sometimes absent. The `VinInput` component validates the 17-character format client-side and
rejects I, O, and Q with an inline message that explains why rather than just refusing.

**When VIN decode is slow or the provider is down** (the API returns 503 per the contract), the form
does not block: the user continues with manual entry and a background job retries the decode,
back-filling the record and surfacing a toast when it lands.

### 5.2 Respond to a lead — target: under 30 seconds

Response time is the strongest predictor of lead conversion, which is why it is a stored column in
the schema and the sort order of the queue.

```
[Leads] → the top row is always the oldest unanswered lead, with a live timer
   ↓ tap
Split view: the customer's message and history on the left,
            an AI-drafted reply on the right — already written, editable
   ↓
Edit if needed → [Send]  (channel defaults to how they contacted us)
   ↓
Lead advances to `contacted`, first_response_at is stamped, timer stops
```

**The AI draft is pre-generated when the lead arrives**, not on demand, so it is already on screen
when the salesperson opens the row. It is grounded strictly in the vehicle record and the customer's
message. The send button is never armed by AI alone — a human presses it, every time (ADR-0004).

### 5.3 Quote → buyer order — the hardest screen

The deal screen is where a wrong number becomes a signed contract, so its design carries more weight
than any other.

**Layout: two columns that never scroll independently on desktop.**

- **Left — inputs.** Vehicle, customer, trade, price, fees, products, financing. Editable.
- **Right — the calculation.** A `MoneyBreakdown` reading exactly as it will read on the printed
  buyer order, line for line, in the same order.

**Four rules for this screen:**

1. **The breakdown is never stale silently.** When any input changes, the right column dims, shows
   *"Recalculate to update"*, and the Contract action disables. It never shows an out-of-date number
   as if it were current. This maps to `calculationStale` in the API contract.
2. **Every line is traceable.** Tapping a fee shows where it came from — dealer configuration,
   statutory schedule, or manual entry — and its rule-set version. When a customer asks "what is
   this $89?", the answer is one tap away.
3. **Gross is permission-gated and visually separate**, in its own panel below the fold, so a
   salesperson screen-sharing with a customer does not expose it by accident. This is a real,
   frequent, embarrassing failure in existing products.
4. **When there is no approved rule set for the jurisdiction, the screen says so plainly** — *"Tax
   rules for Sedgwick County, KS are not yet configured. This deal cannot be priced."* — and names
   who to contact. It does not show a zero, and it does not show an estimate. A guessed tax figure on
   a buyer order is worse than a blocked screen.

### 5.4 Deal jacket completion — the office manager's screen

A checklist, not a folder. Each required document shows satisfied / missing / expiring, and a missing
row's primary action is the fastest way to satisfy it — upload, generate from template, or scan.

The value here is discovering a missing document **at deal time, not at title time**, when it costs
real money. So the checklist is also surfaced on the deal detail and in Today's "deals blocked" list.

---

## 6. States: designed, not defaulted

Every list and detail screen specifies four states. Most software ships only the third.

| State | Design |
| --- | --- |
| **Loading** | Skeleton rows matching final layout and row height, so nothing shifts when data lands. No spinners on full pages. Render within 50 ms |
| **Empty (first run)** | Explains what belongs here and offers the primary action. *"No vehicles yet. Scan a VIN to add your first."* |
| **Empty (filtered)** | Different message and different action: *"No vehicles match these filters."* + **Clear filters**. Conflating these two is a common and disorienting mistake |
| **Error** | What failed, whether it is retryable, and the `traceId` for support. Never a raw status code, never a blank page |
| **Permission-denied** | The section is **absent**, not disabled. A greyed-out button teaches a salesperson that cost data exists and they are not trusted with it — which is a worse experience than a screen that simply does not have that column |
| **Offline** | Read-only from cache with a persistent banner. Writes queue and replay; the queue is visible and cancellable |

**Optimistic updates** apply to low-risk, reversible actions — reorder photos, mark a task done,
change a lead status. They do **not** apply to money, status transitions on a contracted deal, or
anything that sends something to a customer. Those wait for the server and say so.

---

## 7. Asynchronous work

The Phase 4 contract makes VIN decode, OCR, AI generation, image processing, publishing, and report
export asynchronous (202 + job polling). The UI must make that feel deliberate rather than broken.

**The `ProgressJob` pattern:**

- The triggering action stays disabled with an inline status, rather than opening a modal that traps
  the user. *"Publishing to 3 channels…"*
- Progress appears **in place**, on the object it concerns — a publishing badge on the vehicle row,
  a processing overlay on the photo thumbnail.
- Long jobs (bulk photo processing, report export) drop into a notification, so the user can leave
  the screen. Nothing requires the user to sit and watch.
- **Failures are actionable.** *"Cars.com feed failed: credentials rejected. [Fix credentials]"* —
  not "Job failed."

**Photo upload specifically** — the highest-volume async interaction in the product:

thumbnails appear immediately from local object URLs, upload progress rides on each tile, then each
tile transitions through scanning → processing → ready. A rejected file (wrong type, infected, size)
shows on its own tile with the reason, and the other twenty-nine continue. One bad file never fails
a batch.

---

## 8. Mobile: a different product, not a smaller one

Phase 1 established that recon, VIN scan, photo capture, and lead response happen on a phone, on a
lot, one-handed. These get purpose-built layouts.

| Job | Mobile design |
| --- | --- |
| **Add vehicle** | Camera-first VIN scan; 3 fields; thumb-reachable save |
| **Photograph a vehicle** | Full-screen capture with a shot-list guide (front ¾, rear ¾, interior, odometer, VIN plate…), progress against the list, batch upload with retry |
| **Recon update** | Scan VIN → the vehicle's recon steps → tap to advance; a cost can be added with an amount and a photo of the invoice |
| **Lead response** | Notification → lead → draft reply → send. Three taps from lock screen |
| **Lot lookup** | Scan or type the last 6 of a VIN → price, days in stock, status. The single most common lot-floor query |

**Constraints that are actually design decisions:**

- Primary actions sit in the bottom third — that is where a thumb reaches.
- Minimum touch target 44 × 44 px, and no destructive action adjacent to a frequent one.
- **The interface must be legible in direct sunlight**: high-contrast mode is not a preference here,
  it is the default outdoors. Light theme uses near-black on near-white for primary text.
- Every camera flow works with gloves and dirty hands: large targets, no long-press, no drag.
- Offline-tolerant: a lot often has poor signal. Photos and recon updates queue.

**What is deliberately desktop-only:** deal structuring, accounting, report building, and settings.
Squeezing the deal screen onto a phone would compromise §5.3's guarantees, and nobody structures F&I
on a phone.

---

## 9. Accessibility — WCAG 2.2 AA

Not a compliance checkbox. Dealership staff include people with colour vision deficiencies, people
using screen magnification, and people who are far faster on a keyboard than a mouse.

| Requirement | Implementation |
| --- | --- |
| Contrast | 4.5:1 body, 3:1 large text and UI boundaries, **in both themes**. Enforced by a token-level contrast test, not eyeballed |
| Colour independence | §4.2 — every colour-coded state also carries shape, position, or text |
| Keyboard | Every interactive element reachable and operable. Visible focus ring that survives theme changes. Logical tab order. Skip-to-content link |
| Focus management | Dialogs trap focus and restore it on close. Route changes move focus to the `<h1>` and announce via a live region |
| Screen readers | Semantic HTML first; ARIA only where semantics fall short. The DataTable exposes real `<table>` semantics — a div grid pretending to be a table is unusable with a screen reader |
| Motion | All animation respects `prefers-reduced-motion`. Nothing conveys meaning through motion alone |
| Forms | Labels are always visible — never placeholder-as-label. Errors are programmatically associated and announced |
| Targets | 44 × 44 px minimum (WCAG 2.2 target size) |
| Zoom | Usable at 200 % without horizontal scrolling; wide tables scroll within their own container |

Verified by `axe` in CI plus a manual keyboard-only pass per module before that module ships.

---

## 10. Keyboard shortcuts

For the office manager who lives here all day. Discoverable via `?`, never required.

| Key | Action |
| --- | --- |
| `⌘K` / `Ctrl+K` | Command palette |
| `/` | Focus search on the current list |
| `g` then `i` | Go to Inventory |
| `g` then `l` | Go to Leads |
| `g` then `d` | Go to Deals |
| `g` then `t` | Go to Today |
| `n` | New (context-sensitive: vehicle on Inventory, deal on Deals) |
| `j` / `k` | Move down / up a list |
| `Enter` | Open the focused row |
| `e` | Edit the focused record |
| `?` | Shortcut reference |
| `Esc` | Close the topmost layer |

Single-letter shortcuts are suppressed while a text input has focus — an obvious rule that a
surprising number of applications get wrong.

---

## 11. Performance as a design constraint

Phase 1's budgets (LCP < 2.0 s, INP < 200 ms on 4G / mid-tier Android) constrain what may be
designed, so they are stated here rather than treated as an engineering problem discovered later.

- **The inventory grid virtualizes rows.** A 500-unit dealer must scroll at 60 fps on a mid-range
  Android.
- **Thumbnails are ≤ 40 KB WebP/AVIF** served from the Cloudflare CDN, sized to the rendered box.
  Full-resolution images load only in the gallery.
- **The grid never fetches everything to filter client-side.** Every filter maps to a server
  parameter that maps to an index from Phase 3 §6. If a filter cannot be served by an index, it does
  not ship as a filter.
- **No layout shift.** Every image has explicit dimensions; every skeleton matches its final row
  height.
- **Route-level code splitting follows the sidebar**, so a salesperson who never opens Reports never
  downloads it.

---

## 12. What we are deliberately not doing

Stated so these are decisions rather than omissions:

- **No configurable dashboard widgets.** Every DMS has them; nobody configures them past week one.
  Today is opinionated instead.
- **No modal dialogs for anything longer than a confirmation.** Long forms get a page or a drawer,
  because modals lose work and cannot be linked to.
- **No infinite scroll on the inventory grid.** Dealers refer to "page 3" and need stable positions;
  offset paging is correct here. Infinite scroll is reserved for timelines and message threads.
- **No tooltips carrying essential information.** They do not exist on touch devices.
- **No filled status pills, and no tinted callout panels.** Both are colour doing a job that a dot,
  a rule, or plain alignment does more quietly (§4.1).
- **No "advanced filter" query builder.** A generic filter DSL produces unindexable queries and is an
  injection surface (Phase 4 §5). Saved views cover the real need.
- **No in-app onboarding tour.** Empty states that explain themselves work better and are always
  correct.

---

## 13. What Phase 6 needs from this

1. `frontend/packages/ui/tokens.css` is the contract for colour, spacing, type, and motion. Backend
   work does not touch it, but the API must supply what the states above need.
2. **Every list endpoint must return the counts the filter chips display**, or the UI must make a
   second request — decide this in Phase 6, not after the grid is built.
3. **`calculationStale` must be authoritative from the server** (§5.3 rule 1). The client must not
   infer staleness.
4. **Job progress needs a percentage where one is meaningful** (photo batches, report generation),
   not just a status — the design promises progress, so the contract must supply it.
5. **The `requiredDocuments` array on a deal drives the jacket checklist** (§5.4). Its
   `satisfied` flag is computed server-side; the client must not re-derive completeness rules.
