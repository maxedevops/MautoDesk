import { createHash } from 'node:crypto';
import Link from 'next/link';
import { revalidatePath } from 'next/cache';
import { notFound, redirect } from 'next/navigation';
import { ApiError, formatMoney, statusLabel } from '@mautodesk/api-client';
import { apiClient, currentPermissions } from '@/lib/api';
import { AgingIndicator, Note, ReadinessMeter, StatusDot } from '@/components/primitives';

export const dynamic = 'force-dynamic';

export default async function VehiclePage({
  params,
  searchParams,
}: {
  readonly params: Promise<{ vehicleId: string }>;
  readonly searchParams: Promise<Record<string, string | undefined>>;
}) {
  const { vehicleId } = await params;
  const query = await searchParams;
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
  const canEditPhotos = permissions.has('inventory.photo.write');

  // Photos load with the page rather than on demand: a listing screen without
  // its photos is not a listing screen.
  const photos = await (await apiClient()).listPhotos(vehicleId).catch(() => []);
  const canWrite = permissions.has('inventory.vehicle.write');

  // Straight from the server, so the menu offers exactly the moves the domain
  // will accept rather than a copy of the transition table that can drift.
  const transitions = vehicle.allowedTransitions ?? [];

  async function changeStatus(formData: FormData) {
    'use server';

    const status = String(formData.get('status') ?? '');
    const reason = String(formData.get('reason') ?? '').trim();

    if (status === '') {
      return;
    }

    try {
      await (await apiClient()).changeStatus(vehicleId, status, reason === '' ? undefined : reason);
    } catch (failure) {
      redirect(`/inventory/${vehicleId}?error=${encodeURIComponent(describe(failure))}`);
    }

    // The page reads the vehicle on every request, but the cached render has to
    // be dropped or the user sees the status they just left.
    revalidatePath(`/inventory/${vehicleId}`);
    redirect(`/inventory/${vehicleId}`);
  }

  /**
   * Uploads one photo through the three-step pipeline.
   *
   * The file passes through the Next server rather than going straight from the
   * browser to the bucket. Direct-to-bucket would save a hop, but it needs CORS
   * opened on the quarantine bucket and the presigned URL handed to client-side
   * JavaScript — and this app deliberately keeps every credential, including
   * capability URLs, on the server (ADR §4).
   */
  async function uploadPhoto(formData: FormData) {
    'use server';

    const file = formData.get('photo');

    if (!(file instanceof File) || file.size === 0) {
      redirect(`/inventory/${vehicleId}?error=${encodeURIComponent('Choose a photo to upload.')}`);
    }

    const bytes = Buffer.from(await file.arrayBuffer());
    const digest = createHash('sha256').update(bytes).digest('hex');

    try {
      const client = await apiClient();

      const intent = await client.requestPhotoUpload(vehicleId, {
        contentType: file.type,
        byteSize: bytes.byteLength,
        sha256: digest,
      });

      // Straight to the quarantine bucket. The API never sees the bytes.
      const put = await fetch(intent.uploadUrl!, {
        method: 'PUT',
        headers: { 'Content-Type': file.type },
        body: bytes,
      });

      if (!put.ok) {
        redirect(
          `/inventory/${vehicleId}?error=${encodeURIComponent(
            `The upload did not complete (${put.status}). Try again.`,
          )}`,
        );
      }

      // Nothing is a photo until this passes: size, digest, malware, and a
      // decode, followed by a re-encode that strips the metadata.
      await client.confirmPhotoUpload(vehicleId, intent.photoId!);
    } catch (failure) {
      redirect(`/inventory/${vehicleId}?error=${encodeURIComponent(describe(failure))}`);
    }

    revalidatePath(`/inventory/${vehicleId}`);
    redirect(`/inventory/${vehicleId}`);
  }

  async function makePrimaryPhoto(formData: FormData) {
    'use server';

    const photoId = String(formData.get('photoId') ?? '');

    try {
      await (await apiClient()).setPrimaryPhoto(vehicleId, photoId);
    } catch (failure) {
      redirect(`/inventory/${vehicleId}?error=${encodeURIComponent(describe(failure))}`);
    }

    revalidatePath(`/inventory/${vehicleId}`);
    redirect(`/inventory/${vehicleId}`);
  }

  async function removePhoto(formData: FormData) {
    'use server';

    const photoId = String(formData.get('photoId') ?? '');

    try {
      await (await apiClient()).deletePhoto(vehicleId, photoId);
    } catch (failure) {
      redirect(`/inventory/${vehicleId}?error=${encodeURIComponent(describe(failure))}`);
    }

    revalidatePath(`/inventory/${vehicleId}`);
    redirect(`/inventory/${vehicleId}`);
  }

  async function publish() {
    'use server';

    try {
      await (await apiClient()).publish(vehicleId);
    } catch (failure) {
      // Publishing fails for ordinary, fixable reasons — no photos, no price —
      // so the message the API gives is the useful thing to show.
      redirect(`/inventory/${vehicleId}?error=${encodeURIComponent(describe(failure))}`);
    }

    revalidatePath(`/inventory/${vehicleId}`);
    redirect(`/inventory/${vehicleId}?published=1`);
  }

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
        <div className="ml-auto flex flex-wrap items-end gap-2">
          {canWrite && transitions.length > 0 ? (
            <form action={changeStatus} className="flex flex-wrap items-end gap-2">
              <label className="flex flex-col gap-1">
                <span className="t-label">Change status</span>
                <select
                  name="status"
                  defaultValue=""
                  className="min-h-8 rounded-md border border-control bg-surface px-3 text-base text-ink"
                >
                  <option value="" disabled>
                    Move to…
                  </option>
                  {transitions.map((target) => (
                    <option key={target} value={target}>
                      {statusLabel(target)}
                    </option>
                  ))}
                </select>
              </label>
              <label className="flex flex-col gap-1">
                <span className="sr-only">Reason</span>
                <input
                  name="reason"
                  placeholder="Reason (optional)"
                  className="min-h-8 rounded-md border border-control bg-surface px-3 text-base text-ink"
                />
              </label>
              <button
                type="submit"
                className="inline-flex min-h-8 items-center rounded-md border border-control px-4 font-medium hover:bg-hover"
              >
                Apply
              </button>
            </form>
          ) : null}

          {/* Hidden, not disabled, when the user cannot publish. */}
          {canPublish && !vehicle.isPublished ? (
            <form action={publish}>
              <button
                type="submit"
                className="inline-flex min-h-8 items-center rounded-md px-4 font-semibold"
                style={{ background: 'var(--accent-bg)', color: 'var(--text-on-accent)' }}
              >
                Publish
              </button>
            </form>
          ) : null}
        </div>
      </div>

      {query['error'] ? (
        <Note tone="danger" title="That change was not applied">
          {query['error']}
        </Note>
      ) : null}

      {query['published'] ? (
        <Note tone="success" title="Published">
          This vehicle is live on the website and queued for syndication.
        </Note>
      ) : null}

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

        <section className="overflow-hidden rounded-lg border border-line bg-surface lg:col-span-2">
          <div className="flex items-center justify-between border-b border-line px-4 py-3">
            <span className="t-section">Photos</span>
            <span className="text-xs text-muted">
              {photos.filter((photo) => photo.status === 'ready').length} ready
            </span>
          </div>

          <div className="flex flex-col gap-4 p-4">
            {photos.length === 0 ? (
              <p className="m-0 text-xs text-muted">
                No photos yet. A vehicle cannot be published without at least one, and listings
                without photos are skipped by shoppers.
              </p>
            ) : (
              <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
                {photos.map((photo) => (
                  <li
                    key={photo.id}
                    className="flex flex-col gap-2 overflow-hidden rounded-md border border-line"
                  >
                    {photo.status === 'ready' && photo.thumbnailUrl ? (
                      // eslint-disable-next-line @next/next/no-img-element -- the
                      // URL is a short-lived presigned one, which next/image
                      // cannot cache or optimise anyway.
                      <img
                        src={photo.thumbnailUrl}
                        alt={photo.caption ?? `Photo of ${title}`}
                        className="aspect-[4/3] w-full object-cover"
                      />
                    ) : (
                      <div className="flex aspect-[4/3] w-full items-center justify-center bg-inset p-2 text-center text-[0.6875rem] text-muted">
                        {photo.status === 'rejected'
                          ? (photo.rejectionReason ?? 'Rejected')
                          : 'Checking…'}
                      </div>
                    )}

                    <div className="flex items-center justify-between gap-1 px-2 pb-2">
                      <span className="text-[0.6875rem] text-faint">
                        {photo.isPrimary ? 'Lead photo' : photo.status}
                      </span>

                      {canEditPhotos ? (
                        <span className="flex gap-1">
                          {photo.status === 'ready' && !photo.isPrimary ? (
                            <form action={makePrimaryPhoto}>
                              <input type="hidden" name="photoId" value={photo.id ?? ''} />
                              <button type="submit" className="text-[0.6875rem] underline">
                                Make lead
                              </button>
                            </form>
                          ) : null}

                          <form action={removePhoto}>
                            <input type="hidden" name="photoId" value={photo.id ?? ''} />
                            <button
                              type="submit"
                              className="text-[0.6875rem] underline"
                              style={{ color: 'var(--danger-text)' }}
                            >
                              Remove
                            </button>
                          </form>
                        </span>
                      ) : null}
                    </div>
                  </li>
                ))}
              </ul>
            )}

            {canEditPhotos ? (
              <form action={uploadPhoto} className="flex flex-wrap items-center gap-2">
                <input
                  type="file"
                  name="photo"
                  accept="image/jpeg,image/png,image/webp"
                  required
                  className="min-h-11 max-w-full text-xs text-muted"
                />
                <button
                  type="submit"
                  className="inline-flex min-h-11 items-center rounded-md border border-control px-4 font-medium hover:bg-hover"
                >
                  Upload photo
                </button>
                <span className="text-[0.6875rem] text-faint">
                  JPEG, PNG, or WebP, up to 20 MB. Location data is removed on upload.
                </span>
              </form>
            ) : null}
          </div>
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

/**
 * Turns a failure into something worth showing a dealer.
 *
 * The API's own message is the useful one — "a vehicle needs at least one photo
 * before it can be published" beats anything this layer could invent — and the
 * trace id is what support needs to find the request.
 */
function describe(failure: unknown): string {
  if (failure instanceof ApiError) {
    return `${failure.message}${failure.traceId ? ` (trace ${failure.traceId})` : ''}`;
  }

  return 'The change could not be saved. Check that the API is running.';
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
