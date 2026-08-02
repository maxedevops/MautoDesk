import type { ReactNode } from 'react';
import { agingBucket, statusLabel } from '@mautodesk/api-client';

/**
 * Vehicle status: a dot plus a plain-text label.
 *
 * Never a filled pill. Eight tinted pills down a grid column is the busiest
 * thing a dense table can do, and the label already carries the meaning — the
 * dot only speeds up scanning (docs/05-ux-design.md §4.1).
 */
export function StatusDot({ status }: { readonly status: string }) {
  const colour: Record<string, string> = {
    available: 'var(--status-available-dot)',
    in_recon: 'var(--status-in_recon-dot)',
    pending_sale: 'var(--status-pending_sale-dot)',
    sold: 'var(--status-sold-dot)',
    delivered: 'var(--status-sold-dot)',
  };

  return (
    <span className="inline-flex items-center gap-2 whitespace-nowrap">
      <span
        aria-hidden="true"
        className="size-2 shrink-0 rounded-full"
        style={{ background: colour[status] ?? 'var(--status-neutral-dot)' }}
      />
      {statusLabel(status)}
    </span>
  );
}

/**
 * Days in inventory, encoded on two channels.
 *
 * Bar weight rises monotonically with age so the severity ordering survives
 * greyscale and colour-vision deficiency, and only the two buckets that need
 * action colour their numeral — colouring every row means colouring nothing.
 */
export function AgingIndicator({ days }: { readonly days: number | null | undefined }) {
  if (days === null || days === undefined) {
    return <span className="text-faint">—</span>;
  }

  const bucket = agingBucket(days);

  const bar: Record<string, { width: string; colour: string }> = {
    fresh: { width: '0px', colour: 'transparent' },
    watch: { width: '2px', colour: 'var(--info-mark)' },
    stale: { width: '4px', colour: 'var(--warning-mark)' },
    critical: { width: '6px', colour: 'var(--danger-mark)' },
  };

  const text: Record<string, string> = {
    fresh: 'var(--text-secondary)',
    watch: 'var(--text-primary)',
    stale: 'var(--warning-text)',
    critical: 'var(--danger-text)',
  };

  const weight: Record<string, number> = {
    fresh: 500,
    watch: 500,
    stale: 600,
    critical: 700,
  };

  const style = bar[bucket]!;

  return (
    <span className="inline-flex items-center gap-2">
      <span
        aria-hidden="true"
        className="block size-5 shrink-0"
        style={{ borderLeft: `${style.width} solid ${style.colour}` }}
      />
      <span className="tabular" style={{ color: text[bucket], fontWeight: weight[bucket] }}>
        {days}d
      </span>
    </span>
  );
}

/**
 * A callout: ordinary surface with a semantic left rule.
 *
 * Not a tinted panel. A 3px stripe says "warning" as clearly as a filled amber
 * box without shouting across the rest of the screen.
 */
export function Note({
  tone,
  title,
  children,
}: {
  readonly tone: 'info' | 'warning' | 'danger' | 'success';
  readonly title?: string;
  readonly children: ReactNode;
}) {
  const mark = `var(--${tone === 'info' ? 'info' : tone === 'success' ? 'success' : tone}-mark)`;
  const text = `var(--${tone === 'info' ? 'info' : tone === 'success' ? 'success' : tone}-text)`;

  return (
    <div
      className="flex gap-3 rounded-r-md border border-l-[3px] bg-surface px-4 py-3 text-xs text-muted"
      style={{ borderColor: 'var(--border-subtle)', borderLeftColor: mark }}
    >
      <span aria-hidden="true" style={{ color: text }}>
        ▲
      </span>
      <span>
        {title ? <strong className="mb-0.5 block text-sm text-ink">{title}</strong> : null}
        {children}
      </span>
    </div>
  );
}

/**
 * Completeness toward a publishable listing.
 *
 * The mechanism that lets the save path stay permissive: the system asks rather
 * than blocks, so a salesperson can record a vehicle with a VIN and nothing else
 * (docs/05-ux-design.md §5.1). The bar is neutral grey — it is not interactive,
 * so it does not get the accent.
 */
export function ReadinessMeter({
  satisfied,
  total,
  missing,
}: {
  readonly satisfied: number;
  readonly total: number;
  readonly missing: readonly string[];
}) {
  const percent = total === 0 ? 0 : Math.round((satisfied / total) * 100);

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-baseline justify-between text-xs text-muted">
        <span>Ready to publish</span>
        <span className="tabular">
          {satisfied} of {total}
        </span>
      </div>
      <div
        className="h-1.5 overflow-hidden rounded-full bg-inset"
        role="progressbar"
        aria-valuenow={satisfied}
        aria-valuemin={0}
        aria-valuemax={total}
        aria-label="Publishing readiness"
      >
        <div
          className="h-full rounded-full"
          style={{ width: `${percent}%`, background: 'var(--text-tertiary)' }}
        />
      </div>
      {missing.length > 0 ? (
        <p className="text-xs text-faint">Still needed: {missing.join(', ')}</p>
      ) : null}
    </div>
  );
}

/** Empty state. Distinguishes "nothing yet" from "nothing matches". */
export function EmptyState({
  filtered,
  onClearHref,
}: {
  readonly filtered: boolean;
  readonly onClearHref?: string;
}) {
  return (
    <div className="flex flex-col items-center gap-3 px-5 py-10 text-center">
      <h3 className="text-base font-semibold">
        {filtered ? 'No vehicles match these filters' : 'No vehicles yet'}
      </h3>
      <p className="max-w-lg text-muted">
        {filtered
          ? 'Nothing in stock matches what you have selected. Clearing the filters will show everything.'
          : 'Scan a VIN to add your first vehicle. You only need a stock number to start — everything else can come later.'}
      </p>
      {filtered && onClearHref ? (
        <a
          href={onClearHref}
          className="inline-flex min-h-8 items-center rounded-md border border-control px-4 hover:bg-hover"
        >
          Clear filters
        </a>
      ) : null}
    </div>
  );
}
