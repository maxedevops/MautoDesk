import Link from 'next/link';
import { notFound } from 'next/navigation';
import { ApiError, formatMoney, statusLabel } from '@mautodesk/api-client';
import { apiClient, currentPermissions } from '@/lib/api';
import { AgingIndicator, Note, ReadinessMeter, StatusDot } from '@/components/primitives';

export const dynamic = 'force-dynamic';

export default async function VehiclePage({
  params,
}: {
  readonly params: Promise<{ vehicleId: string }>;
}) {
  const { vehicleId } = await params;
  const permissions = await currentPermissions();

  let vehicle;

  try {
    vehicle = await (await apiClient()).getVehicle(vehicleId);
  } catch (error) {
    // A 404 here means "missing OR another tenant's" — the API does not
    // distinguish them, and neither does this screen. Rendering a generic
    // not-found is the correct behaviour for both.
    if (error instanceof ApiError && error.isNotFound) {
      notFound();
    }
    throw error;
  }

  const title =
    [vehicle.modelYear, vehicle.make, vehicle.model, vehicle.trim].filter(Boolean).join(' ') ||
    'Unidentified vehicle';

  const readiness = vehicle.readiness;
  const canPublish = permissions.has('inventory.publish');

  return (
    <div className="flex flex-col gap-5 px-5 py-6">
      <nav aria-label="Breadcrumb" className="text-xs text-muted">
        <Link href="/inventory" className="hover:underline" style={{ color: 'var(--accent-fg)' }}>
          Inventory
        </Link>
        <span aria-hidden="true"> / </span>
        <span>{vehicle.stockNumber}</span>
      </nav>

      <div className="flex flex-wrap items-end gap-4">
        <div>
          <h1 className="t-page m-0">{title}</h1>
          <p className="mt-1 flex flex-wrap items-center gap-3 text-xs text-muted">
            <span>Stock {vehicle.stockNumber}</span>
            <span className="font-mono">{vehicle.vin ?? 'No VIN yet'}</span>
            <StatusDot status={vehicle.status ?? 'acquired'} />
          </p>
        </div>
        <div className="ml-auto flex gap-2">
          <button
            type="button"
            className="inline-flex min-h-8 items-center rounded-md border border-control px-4 font-medium hover:bg-hover"
          >
            Change status
          </button>
          {/* Hidden, not disabled, when the user cannot publish. */}
          {canPublish ? (
            <button
              type="button"
              className="inline-flex min-h-8 items-center rounded-md px-4 font-semibold"
              style={{ background: 'var(--accent-bg)', color: 'var(--text-on-accent)' }}
            >
              Publish
            </button>
          ) : null}
        </div>
      </div>

      {readiness && !isReady(readiness) ? (
        <Note tone="warning" title="This vehicle is not ready to publish yet">
          Nothing is blocking you from saving it — the missing pieces can be added by whoever has
          them.
        </Note>
      ) : null}

      <div className="grid items-start gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <section className="overflow-hidden rounded-lg border border-line bg-surface">
          <div className="border-b border-line px-4 py-3">
            <span className="t-section">Details</span>
          </div>
          <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-2 p-4">
            <Row label="Year" value={vehicle.modelYear?.toString()} />
            <Row label="Make" value={vehicle.make} />
            <Row label="Model" value={vehicle.model} />
            <Row label="Trim" value={vehicle.trim} />
            <Row label="Body" value={vehicle.bodyStyle} />
            <Row label="Drive" value={vehicle.driveType} />
            <Row label="Engine" value={vehicle.engine} />
            <Row label="Fuel" value={vehicle.fuelType} />
            <Row label="Transmission" value={vehicle.transmission} />
            <Row label="Exterior" value={vehicle.exteriorColor} />
            <Row label="Mileage" value={vehicle.mileage?.toLocaleString('en-US')} numeric />
            <Row label="Status" value={statusLabel(vehicle.status ?? 'acquired')} />
          </dl>
        </section>

        <div className="flex flex-col gap-5">
          <section className="overflow-hidden rounded-lg border border-line bg-surface">
            <div className="border-b border-line px-4 py-3">
              <span className="t-section">Pricing &amp; age</span>
            </div>
            <div className="flex flex-col gap-3 p-4">
              <div className="flex items-baseline justify-between">
                <span className="text-xs text-muted">List price</span>
                <span className="tabular text-lg font-bold">{formatMoney(vehicle.listPrice)}</span>
              </div>
              <div className="flex items-baseline justify-between">
                <span className="text-xs text-muted">Days in inventory</span>
                <AgingIndicator days={vehicle.daysInInventory} />
              </div>
              {/*
                Cost and gross are absent entirely rather than nulled. The API
                does not return them without inventory.cost.read, so there is
                nothing here to hide badly.
              */}
            </div>
          </section>

          {readiness ? (
            <section className="overflow-hidden rounded-lg border border-line bg-surface">
              <div className="border-b border-line px-4 py-3">
                <span className="t-section">Publishing</span>
              </div>
              <div className="p-4">
                <ReadinessMeter
                  satisfied={readiness.satisfied ?? 0}
                  total={readiness.total ?? 0}
                  missing={readiness.missing ?? []}
                />
              </div>
            </section>
          ) : null}

          {vehicle.aiDescriptionDraft ? (
            <Note tone="info" title="AI description awaiting review">
              This draft is not published. Someone with approval rights must read it first —
              advertising equipment the vehicle does not have is a consumer-protection problem, so
              the system will not publish model output on its own.
            </Note>
          ) : null}
        </div>
      </div>
    </div>
  );
}

function isReady(readiness: { satisfied?: number; total?: number }): boolean {
  return (readiness.satisfied ?? 0) >= (readiness.total ?? 0);
}

function Row({
  label,
  value,
  numeric = false,
}: {
  readonly label: string;
  readonly value?: string | null;
  readonly numeric?: boolean;
}) {
  return (
    <>
      <dt className="text-xs text-muted">{label}</dt>
      <dd className={`m-0 text-right ${numeric ? 'tabular' : ''}`}>
        {value ? value : <span className="text-faint">—</span>}
      </dd>
    </>
  );
}
