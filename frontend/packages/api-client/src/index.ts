/**
 * Typed client for the MautoDesk API.
 *
 * `schema.d.ts` is GENERATED from `contracts/openapi.json`, which is itself
 * generated from the running API and guarded by a drift test (ADR-0010). Do not
 * hand-edit either file: run `pnpm generate:client` after the backend changes.
 *
 * The chain that makes a type mismatch a build failure rather than a runtime
 * `undefined`:
 *
 *   ASP.NET endpoints
 *     → contracts/openapi.json      (generated; drift fails the .NET test suite)
 *       → schema.d.ts               (generated; `pnpm generate:client`)
 *         → this client             (typed against schema.d.ts)
 */

import type { paths, components } from './schema.js';

/* ------------------------------------------------------------------ types -- */

export type VehicleSummary = components['schemas']['VehicleSummaryDto'];
export type Vehicle = components['schemas']['VehicleDto'];
export type PublishReadiness = components['schemas']['PublishReadinessDto'];
export type VinDecode = components['schemas']['VinDecodeDto'];
export type ProblemDetails = components['schemas']['ProblemDetails'];
export type VehiclePage = components['schemas']['PagedResultOfVehicleSummaryDto'];
export type CreateVehicle = components['schemas']['CreateVehicleCommand'];

export type VehicleListQuery = NonNullable<
  paths['/api/v1/vehicles']['get']['parameters']['query']
>;

/* ----------------------------------------------------------------- errors -- */

/**
 * A structured API failure.
 *
 * Carries the RFC 9457 payload so a caller can branch on `code` rather than
 * matching on message text, and always exposes `traceId` — the only identifier
 * that crosses the boundary, and the one support needs to find the request in
 * the logs.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;
  readonly traceId: string | undefined;
  readonly problem: ProblemDetails | undefined;
  readonly fieldErrors: Record<string, string[]>;

  constructor(status: number, problem?: ProblemDetails) {
    super(problem?.detail ?? problem?.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;

    const extensions = problem as Record<string, unknown> | undefined;
    this.code = typeof extensions?.['code'] === 'string' ? extensions['code'] : undefined;
    this.traceId = typeof extensions?.['traceId'] === 'string' ? extensions['traceId'] : undefined;
    this.fieldErrors = (extensions?.['errors'] as Record<string, string[]>) ?? {};
  }

  /**
   * True when the record is missing *or* belongs to another tenant.
   *
   * The API does not distinguish these, deliberately — a 403 would confirm the
   * record exists. Clients must not try to tell them apart either.
   */
  get isNotFound(): boolean {
    return this.status === 404;
  }

  get isForbidden(): boolean {
    return this.status === 403;
  }

  /** The caller should show field-level messages rather than a banner. */
  get isValidation(): boolean {
    return this.status === 422;
  }
}

/* ----------------------------------------------------------------- client -- */

export interface ClientOptions {
  readonly baseUrl: string;
  /**
   * Extra headers for every request.
   *
   * In the browser this is empty: the app is a backend-for-frontend, so the
   * browser holds only an HttpOnly session cookie and never a bearer token —
   * which is what stops XSS from being able to exfiltrate one.
   */
  readonly headers?: Record<string, string>;
  readonly fetch?: typeof globalThis.fetch;
}

export class MautoDeskClient {
  readonly #baseUrl: string;
  readonly #headers: Record<string, string>;
  readonly #fetch: typeof globalThis.fetch;

  constructor(options: ClientOptions) {
    this.#baseUrl = options.baseUrl.replace(/\/+$/, '');
    this.#headers = options.headers ?? {};
    this.#fetch = options.fetch ?? globalThis.fetch;
  }

  async listVehicles(query: VehicleListQuery = {}, signal?: AbortSignal): Promise<VehiclePage> {
    const search = new URLSearchParams();

    for (const [key, value] of Object.entries(query)) {
      if (value === undefined || value === null) continue;
      // `status` is repeatable; everything else is scalar.
      if (Array.isArray(value)) {
        for (const item of value) search.append(key, String(item));
      } else {
        search.set(key, String(value));
      }
    }

    const qs = search.toString();
    return this.#request<VehiclePage>('GET', `/api/v1/vehicles${qs ? `?${qs}` : ''}`, undefined, signal);
  }

  async getVehicle(vehicleId: string, signal?: AbortSignal): Promise<Vehicle> {
    return this.#request<Vehicle>('GET', `/api/v1/vehicles/${vehicleId}`, undefined, signal);
  }

  async createVehicle(body: CreateVehicle, signal?: AbortSignal): Promise<Vehicle> {
    return this.#request<Vehicle>('POST', '/api/v1/vehicles', body, signal);
  }

  async changeStatus(vehicleId: string, status: string, reason?: string): Promise<Vehicle> {
    return this.#request<Vehicle>('POST', `/api/v1/vehicles/${vehicleId}/status`, {
      status,
      reason: reason ?? null,
    });
  }

  /** `newPrice` is a decimal string. Never pass a number. */
  async changePrice(vehicleId: string, newPrice: string, reason?: string): Promise<Vehicle> {
    return this.#request<Vehicle>('POST', `/api/v1/vehicles/${vehicleId}/price`, {
      priceType: 'list',
      newPrice,
      reason: reason ?? null,
    });
  }

  async publish(vehicleId: string): Promise<Vehicle> {
    return this.#request<Vehicle>('POST', `/api/v1/vehicles/${vehicleId}/publish`);
  }

  async decodeVin(vin: string, signal?: AbortSignal): Promise<VinDecode> {
    return this.#request<VinDecode>('GET', `/api/v1/vin/${vin}/decode`, undefined, signal);
  }

  async #request<T>(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    const response = await this.#fetch(`${this.#baseUrl}${path}`, {
      method,
      signal,
      headers: {
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...this.#headers,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: 'no-store',
    });

    if (response.status === 204) {
      return undefined as T;
    }

    if (!response.ok) {
      // Error bodies are problem+json, but a proxy or a crash can return HTML.
      // Failing to parse must not mask the status the caller needs to branch on.
      let problem: ProblemDetails | undefined;
      try {
        problem = (await response.json()) as ProblemDetails;
      } catch {
        problem = undefined;
      }
      throw new ApiError(response.status, problem);
    }

    return (await response.json()) as T;
  }
}

/* ------------------------------------------------------------ formatting -- */

/**
 * Formats a decimal money string for display.
 *
 * **Takes a string and never parses it into a number for arithmetic.** The
 * server sends `"28995.00"` precisely so no client rounds a price through an
 * IEEE-754 double; `Number()` here would reintroduce exactly the problem the
 * string representation exists to prevent. This function only ever formats.
 */
export function formatMoney(amount: string | null | undefined): string {
  if (amount === null || amount === undefined || amount === '') return '—';

  const [whole = '0', fraction = '00'] = amount.split('.');
  const negative = whole.startsWith('-');
  const digits = negative ? whole.slice(1) : whole;
  const grouped = digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');

  return `${negative ? '-' : ''}$${grouped}.${fraction.padEnd(2, '0').slice(0, 2)}`;
}

/** Aging bucket for a day count. Mirrors docs/05-ux-design.md §4.4. */
export type AgingBucket = 'fresh' | 'watch' | 'stale' | 'critical';

export function agingBucket(days: number | null | undefined): AgingBucket {
  if (days === null || days === undefined) return 'fresh';
  if (days > 90) return 'critical';
  if (days > 60) return 'stale';
  if (days > 30) return 'watch';
  return 'fresh';
}

/** Human label for a wire status value. */
export function statusLabel(status: string): string {
  const labels: Record<string, string> = {
    acquired: 'Acquired',
    in_recon: 'In recon',
    available: 'Available',
    on_hold: 'On hold',
    pending_sale: 'Pending sale',
    sold: 'Sold',
    delivered: 'Delivered',
    wholesaled: 'Wholesaled',
    archived: 'Archived',
  };
  return labels[status] ?? status;
}
