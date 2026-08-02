import Link from 'next/link';
import { redirect } from 'next/navigation';
import { ApiError, type CreateVehicle } from '@mautodesk/api-client';
import { apiClient, currentPermissions } from '@/lib/api';
import { Note } from '@/components/primitives';

export const dynamic = 'force-dynamic';

/**
 * Add a vehicle.
 *
 * **Only the VIN is worth insisting on, and even that is optional.** A
 * salesperson on the lot with a customer waiting has a VIN and nothing else; a
 * form that refuses to save until eleven fields are filled loses to a spiral
 * notebook (docs/05-ux-design.md §5.1). Everything here is optional, the server
 * generates the stock number when one is not given, and the decode fills in what
 * the VIN already knows.
 */
export default async function NewVehiclePage({
  searchParams,
}: {
  readonly searchParams: Promise<Record<string, string | undefined>>;
}) {
  const params = await searchParams;
  const error = params['error'];
  const permissions = await currentPermissions();

  if (!permissions.has('inventory.vehicle.write')) {
    // Absent, not disabled: a form the user can fill in and then be refused is
    // worse than never offering it.
    return (
      <div className="flex flex-col gap-5 px-5 py-6">
        <h1 className="t-page m-0">Add vehicle</h1>
        <Note tone="info" title="You cannot add vehicles">
          Your role does not include inventory write access. Ask an administrator if you need it.
        </Note>
      </div>
    );
  }

  async function create(formData: FormData) {
    'use server';

    const text = (name: string): string | null => {
      const value = String(formData.get(name) ?? '').trim();
      return value === '' ? null : value;
    };

    const number = (name: string): number | null => {
      const value = text(name);
      return value === null ? null : Number(value);
    };

    const command: CreateVehicle = {
      vin: text('vin'),
      // Left to the server when blank: stock numbers are sequential per tenant
      // and generating one here would race with another user doing the same.
      stockNumber: text('stockNumber'),
      decodeVin: formData.get('decodeVin') === 'on',
      modelYear: number('modelYear'),
      make: text('make'),
      model: text('model'),
      trim: text('trim'),
      mileage: number('mileage'),
      exteriorColor: text('exteriorColor'),
      interiorColor: text('interiorColor'),
      // A decimal string all the way down. Never Number() — that is how a price
      // loses a cent (ADR §11).
      listPrice: text('listPrice'),
      location: text('location'),
      notes: text('notes'),
    };

    let created;

    try {
      created = await (await apiClient()).createVehicle(command);
    } catch (failure) {
      const message =
        failure instanceof ApiError
          ? [failure.message, ...Object.values(failure.fieldErrors).flat()].join(' ')
          : 'The vehicle could not be saved. Check that the API is running.';

      redirect(`/inventory/new?error=${encodeURIComponent(message)}`);
    }

    redirect(`/inventory/${created.id}`);
  }

  return (
    <div className="flex flex-col gap-5 px-5 py-6">
      <nav aria-label="Breadcrumb" className="text-xs text-muted">
        <Link href="/inventory" className="hover:underline" style={{ color: 'var(--accent-fg)' }}>
          Inventory
        </Link>
        <span aria-hidden="true"> / </span>
        <span>New vehicle</span>
      </nav>

      <div>
        <h1 className="t-page m-0">Add vehicle</h1>
        <p className="mt-1 text-xs text-muted">
          A VIN is enough to start. Everything else can be filled in by whoever has it.
        </p>
      </div>

      {error ? (
        <Note tone="danger" title="The vehicle was not saved">
          {error}
        </Note>
      ) : null}

      <form action={create} className="flex max-w-3xl flex-col gap-5">
        <section className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-4">
          <span className="t-section">Identity</span>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="VIN" name="vin" maxLength={17} autoCapitalize="characters" spellCheck={false} />
            <Field label="Stock number" name="stockNumber" placeholder="Generated if blank" />
          </div>

          <label className="flex items-center gap-2 text-xs text-muted">
            <input type="checkbox" name="decodeVin" defaultChecked className="size-4" />
            Look the VIN up and fill in what it knows
          </label>
        </section>

        <section className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-4">
          <span className="t-section">Vehicle</span>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Year" name="modelYear" inputMode="numeric" maxLength={4} />
            <Field label="Make" name="make" />
            <Field label="Model" name="model" />
            <Field label="Trim" name="trim" />
            <Field label="Mileage" name="mileage" inputMode="numeric" />
            <Field label="Exterior colour" name="exteriorColor" />
            <Field label="Interior colour" name="interiorColor" />
            <Field label="Location" name="location" />
          </div>
        </section>

        <section className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-4">
          <span className="t-section">Pricing</span>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="List price" name="listPrice" inputMode="decimal" placeholder="0.00" />
          </div>
        </section>

        <section className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-4">
          <span className="t-section">Notes</span>
          <label className="flex flex-col gap-1">
            <span className="sr-only">Notes</span>
            <textarea
              name="notes"
              rows={3}
              className="rounded-md border border-control bg-surface px-3 py-2 text-base text-ink"
            />
          </label>
        </section>

        <div className="flex gap-2">
          <button
            type="submit"
            className="inline-flex min-h-11 items-center rounded-md px-4 font-semibold"
            style={{ background: 'var(--accent-bg)', color: 'var(--text-on-accent)' }}
          >
            Save vehicle
          </button>
          <Link
            href="/inventory"
            className="inline-flex min-h-11 items-center rounded-md border border-control px-4 font-medium hover:bg-hover"
          >
            Cancel
          </Link>
        </div>
      </form>
    </div>
  );
}

function Field({
  label,
  name,
  ...rest
}: { readonly label: string; readonly name: string } & React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="flex flex-col gap-1">
      <span className="t-label">{label}</span>
      <input
        name={name}
        // 16px minimum: below it, iOS zooms the viewport on focus and the field
        // jumps out from under the user's thumb.
        className="min-h-11 rounded-md border border-control bg-surface px-3 text-base text-ink"
        {...rest}
      />
    </label>
  );
}
