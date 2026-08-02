import Link from 'next/link';
import {
  ApiError,
  formatMoney,
  type VehicleListQuery,
  type VehiclePage,
} from '@mautodesk/api-client';
import { apiClient, currentPermissions } from '@/lib/api';
import { AgingIndicator, EmptyState, Note, StatusDot } from '@/components/primitives';

export const dynamic = 'force-dynamic';

const STATUS_FILTERS = [
  { value: 'available', label: 'Available' },
  { value: 'in_recon', label: 'In recon' },
  { value: 'pending_sale', label: 'Pending sale' },
] as const;

export default async function InventoryPage({
  searchParams,
}: {
  readonly searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const permissions = await currentPermissions();

  // Cost and gross are permission-gated. The columns are ABSENT for a user
  // without the permission, not greyed out — a disabled column teaches a
  // salesperson that cost data exists and they are not trusted with it, which
  // reads worse than a screen that simply does not have it
  // (docs/05-ux-design.md §6). The API omits the fields regardless; this only
  // decides whether to render a header.
  const showCosts = permissions.has('inventory.cost.read');

  const status = typeof params['status'] === 'string' ? params['status'] : undefined;
  const search = typeof params['q'] === 'string' ? params['q'] : undefined;

  const query: VehicleListQuery = {
    pageSize: 50,
    ...(status ? { status: [status] } : {}),
    ...(search ? { q: search } : {}),
  };

  // Explicitly typed: an unannotated `let` in a try/catch is implicitly `any`,
  // which would silently discard every type the generated client provides —
  // exactly the drift ADR-0010 exists to prevent, reintroduced in the client.
  let page: VehiclePage | undefined;
  let failure: string | null = null;

  try {
    page = await (await apiClient()).listVehicles(query);
  } catch (error) {
    // Next signals redirect() and notFound() by THROWING. A broad catch here
    // swallows them, so an unauthenticated visitor would see a rendered page
    // with an error note instead of being sent to sign in. Re-throw those
    // before treating anything as a failure.
    if (isNextControlFlow(error)) throw error;

    // An error state must say what failed and whether it is retryable, and
    // carry the trace id support needs. Never a blank page.
    failure =
      error instanceof ApiError
        ? `${error.message}${error.traceId ? ` (trace ${error.traceId})` : ''}`
        : 'The inventory service is not reachable. Check that the API is running.';
  }

  const isFiltered = Boolean(status || search);

  return (
    <div className="flex flex-col gap-5 px-5 py-6">
      <div className="flex flex-wrap items-end gap-4">
        <div>
          <h1 className="t-page m-0">Inventory</h1>
          <p className="mt-1 text-xs text-muted">
            {page ? `${page.totalCount} vehicles` : 'Loading…'}
          </p>
        </div>
        <div className="ml-auto flex gap-2">
          <button
            type="button"
            className="inline-flex min-h-8 items-center gap-2 rounded-md border border-control px-4 font-medium hover:bg-hover"
          >
            Saved views ▾
          </button>
          <button
            type="button"
            className="inline-flex min-h-8 items-center gap-2 rounded-md px-4 font-semibold"
            style={{ background: 'var(--accent-bg)', color: 'var(--text-on-accent)' }}
          >
            ＋ Add vehicle
          </button>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <FilterChip href="/inventory" active={!status} label="All" />
        {STATUS_FILTERS.map((filter) => (
          <FilterChip
            key={filter.value}
            href={`/inventory?status=${filter.value}`}
            active={status === filter.value}
            label={filter.label}
          />
        ))}
      </div>

      {failure ? (
        <Note tone="danger" title="Inventory could not be loaded">
          {failure}
        </Note>
      ) : null}

      {page ? (
        <div className="overflow-x-auto rounded-lg border border-line bg-surface">
          {page.items.length === 0 ? (
            <EmptyState filtered={isFiltered} onClearHref="/inventory" />
          ) : (
            <table className="w-full min-w-[54rem] border-collapse">
              <caption className="sr-only">
                Vehicle inventory, oldest first
              </caption>
              <thead>
                <tr>
                  <Th>Vehicle</Th>
                  <Th>Stock</Th>
                  <Th align="right">Mileage</Th>
                  <Th>Status</Th>
                  <Th align="right">Age</Th>
                  <Th align="right">Price</Th>
                  {showCosts ? <Th align="right">Cost</Th> : null}
                  <Th>Web</Th>
                </tr>
              </thead>
              <tbody>
                {page.items.map((vehicle) => (
                  <tr key={vehicle.id} className="border-b border-line last:border-0 hover:bg-hover">
                    <Td>
                      <Link
                        href={`/inventory/${vehicle.id}`}
                        className="font-semibold tracking-[-0.005em] hover:underline"
                      >
                        {[vehicle.modelYear, vehicle.make, vehicle.model, vehicle.trim]
                          .filter(Boolean)
                          .join(' ') || 'Unidentified vehicle'}
                      </Link>
                      <div className="font-mono text-[0.6875rem] text-faint">
                        {vehicle.vin ?? 'No VIN yet'}
                      </div>
                    </Td>
                    <Td className="text-xs text-muted tabular">{vehicle.stockNumber}</Td>
                    <Td align="right" className="tabular">
                      {vehicle.mileage?.toLocaleString('en-US') ?? '—'}
                    </Td>
                    <Td>
                      <StatusDot status={vehicle.status ?? 'acquired'} />
                    </Td>
                    <Td align="right">
                      <AgingIndicator days={vehicle.daysInInventory} />
                    </Td>
                    <Td align="right" className="tabular">
                      {formatMoney(vehicle.listPrice)}
                    </Td>
                    {showCosts ? (
                      <Td align="right" className="tabular">
                        —
                      </Td>
                    ) : null}
                    <Td>
                      <span className={vehicle.isPublished ? 'text-xs text-muted' : 'text-xs text-faint'}>
                        {vehicle.isPublished ? 'Live' : 'Not listed'}
                      </span>
                    </Td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      ) : null}

      <div className="flex flex-wrap items-center gap-5 pt-1 text-xs text-muted">
        <Legend width="0px" colour="transparent" label="0–30 days" />
        <Legend width="2px" colour="var(--info-mark)" label="31–60 · watch" />
        <Legend width="4px" colour="var(--warning-mark)" label="61–90 · stale" />
        <Legend width="6px" colour="var(--danger-mark)" label="91+ · critical" />
        <span className="ml-auto text-faint">
          Bar weight rises with age, so severity survives greyscale and colour blindness.
        </span>
      </div>
    </div>
  );
}

/**
 * Whether an error is one of Next's internal navigation signals.
 *
 * `redirect()` and `notFound()` are implemented as thrown errors carrying a
 * `digest`. Any `catch` on a path that can call them has to let them through, or
 * navigation silently stops working.
 */
function isNextControlFlow(error: unknown): boolean {
  return (
    typeof error === 'object' &&
    error !== null &&
    'digest' in error &&
    typeof (error as { digest?: unknown }).digest === 'string' &&
    ((error as { digest: string }).digest.startsWith('NEXT_REDIRECT') ||
      (error as { digest: string }).digest === 'NEXT_NOT_FOUND')
  );
}

function FilterChip({
  href,
  active,
  label,
}: {
  readonly href: string;
  readonly active: boolean;
  readonly label: string;
}) {
  return (
    <Link
      href={href}
      aria-current={active ? 'true' : undefined}
      // Selected is neutral and bold, not blue: a filter is not the page's
      // primary action, and blue is reserved for interaction the user is being
      // pushed toward.
      className={
        active
          ? 'inline-flex min-h-7 items-center rounded-full border border-control bg-active px-3 text-xs font-semibold text-ink'
          : 'inline-flex min-h-7 items-center rounded-full border border-line-strong px-3 text-xs text-muted hover:bg-hover hover:text-ink'
      }
    >
      {label}
    </Link>
  );
}

function Th({
  children,
  align = 'left',
}: {
  readonly children: React.ReactNode;
  readonly align?: 'left' | 'right';
}) {
  return (
    <th
      scope="col"
      className="t-label whitespace-nowrap border-b border-line bg-surface p-3"
      style={{ textAlign: align }}
    >
      {children}
    </th>
  );
}

function Td({
  children,
  align = 'left',
  className = '',
}: {
  readonly children: React.ReactNode;
  readonly align?: 'left' | 'right';
  readonly className?: string;
}) {
  return (
    <td className={`p-3 align-middle ${className}`} style={{ textAlign: align }}>
      {children}
    </td>
  );
}

function Legend({
  width,
  colour,
  label,
}: {
  readonly width: string;
  readonly colour: string;
  readonly label: string;
}) {
  return (
    <span className="flex items-center gap-2">
      <span
        aria-hidden="true"
        className="block size-5"
        style={{ borderLeft: `${width} solid ${colour}` }}
      />
      {label}
    </span>
  );
}
