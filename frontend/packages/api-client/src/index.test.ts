import { describe, expect, it } from 'vitest';
import { ApiError, agingBucket, formatMoney, statusLabel } from './index.js';

/**
 * The pure functions in the client.
 *
 * Small surface, disproportionate consequences: `formatMoney` is the last thing
 * that touches a price before a dealer reads it, and `agingBucket` decides
 * whether a vehicle looks urgent.
 */

describe('formatMoney', () => {
  it('formats a decimal string with thousands separators', () => {
    expect(formatMoney('28995.00')).toBe('$28,995.00');
    expect(formatMoney('1234567.89')).toBe('$1,234,567.89');
  });

  it('does not group below a thousand', () => {
    expect(formatMoney('999.00')).toBe('$999.00');
    expect(formatMoney('0.00')).toBe('$0.00');
  });

  /**
   * The important one.
   *
   * The server sends money as a string precisely so no client rounds it through
   * an IEEE-754 double. A formatter that parses would reintroduce exactly the
   * bug the string representation exists to prevent — and 0.1 + 0.2 is the
   * canonical demonstration that it cannot be trusted.
   */
  it('never round-trips through a JavaScript number', () => {
    // 20 significant digits: a double holds ~15-17 and would silently corrupt it.
    expect(formatMoney('12345678901234.56')).toBe('$12,345,678,901,234.56');

    // A value a double cannot represent exactly survives intact.
    expect(formatMoney('0.10')).toBe('$0.10');
    expect(formatMoney('0.30')).toBe('$0.30');
  });

  it('shows an em dash rather than $0.00 when there is no price', () => {
    // A vehicle that has not been priced yet is different from one priced at
    // zero, and a grid that shows $0.00 for both is actively misleading.
    expect(formatMoney(null)).toBe('—');
    expect(formatMoney(undefined)).toBe('—');
    expect(formatMoney('')).toBe('—');
  });

  it('handles a negative amount', () => {
    expect(formatMoney('-9500.00')).toBe('-$9,500.00');
  });

  it('pads a short fraction rather than truncating the value', () => {
    expect(formatMoney('100.5')).toBe('$100.50');
    expect(formatMoney('100')).toBe('$100.00');
  });
});

describe('agingBucket', () => {
  it('maps day counts to the buckets from the design system', () => {
    expect(agingBucket(0)).toBe('fresh');
    expect(agingBucket(30)).toBe('fresh');
    expect(agingBucket(31)).toBe('watch');
    expect(agingBucket(60)).toBe('watch');
    expect(agingBucket(61)).toBe('stale');
    expect(agingBucket(90)).toBe('stale');
    expect(agingBucket(91)).toBe('critical');
    expect(agingBucket(365)).toBe('critical');
  });

  it('treats a sold vehicle with no age as fresh rather than critical', () => {
    // Null means "not applicable", not "infinitely old". Getting this wrong
    // paints every sold vehicle red.
    expect(agingBucket(null)).toBe('fresh');
    expect(agingBucket(undefined)).toBe('fresh');
  });
});

describe('statusLabel', () => {
  it('humanises the wire values', () => {
    expect(statusLabel('in_recon')).toBe('In recon');
    expect(statusLabel('pending_sale')).toBe('Pending sale');
    expect(statusLabel('available')).toBe('Available');
  });

  it('passes through an unknown status instead of hiding it', () => {
    // A status the server knows and this build does not should be visible, not
    // silently blanked — the user can at least report what they saw.
    expect(statusLabel('some_future_status')).toBe('some_future_status');
  });
});

describe('ApiError', () => {
  it('exposes the code and trace id for support', () => {
    const error = new ApiError(409, {
      title: 'Conflict',
      detail: 'Stock number A-100 is already in use.',
      code: 'vehicle.stock_number.duplicate',
      traceId: '00-abc-def-01',
    } as never);

    expect(error.status).toBe(409);
    expect(error.code).toBe('vehicle.stock_number.duplicate');
    expect(error.traceId).toBe('00-abc-def-01');
    expect(error.message).toContain('already in use');
  });

  it('classifies not-found without distinguishing cross-tenant', () => {
    const error = new ApiError(404);

    expect(error.isNotFound).toBe(true);
    // 404 covers "missing" and "another tenant's" identically by design. A
    // client that tried to tell them apart would be reading a distinction the
    // API deliberately does not make.
    expect(error.isForbidden).toBe(false);
  });

  it('surfaces field errors for a validation failure', () => {
    const error = new ApiError(422, {
      title: 'Validation failed',
      errors: { vin: ['VIN must be exactly 17 characters.'] },
    } as never);

    expect(error.isValidation).toBe(true);
    expect(error.fieldErrors['vin']).toContain('VIN must be exactly 17 characters.');
  });

  it('survives a non-JSON error body', () => {
    // A proxy or a crash can return HTML. Failing to parse must not mask the
    // status the caller needs to branch on.
    const error = new ApiError(502);

    expect(error.status).toBe(502);
    expect(error.message).toContain('502');
    expect(error.fieldErrors).toEqual({});
  });
});
