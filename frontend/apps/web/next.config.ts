import type { NextConfig } from 'next';

/**
 * Where photos are served from.
 *
 * ADR-0005 is explicit that nothing user-uploaded is ever served from the
 * application origin, so photos come from the media bucket's own host — MinIO
 * locally, the CDN in deployment. That host therefore has to be named in the
 * image policy; it cannot be inferred, and a wildcard would defeat the point.
 */
const mediaOrigin = process.env['MEDIA_ORIGIN'] ?? 'http://localhost:9000';

const config: NextConfig = {
  reactStrictMode: true,
  transpilePackages: ['@mautodesk/api-client'],
  poweredByHeader: false,

  /**
   * Traces the server bundle's actual dependencies into `.next/standalone`.
   *
   * Without it the container image has to carry the whole pnpm workspace
   * `node_modules` — hundreds of megabytes of build-time tooling shipped to
   * production, most of it never loaded at runtime.
   */
  output: 'standalone',

  experimental: {
    serverActions: {
      // Photos are posted to a Server Action, and the default cap is 1 MB —
      // which rejects essentially every photo a phone takes. The API enforces
      // its own 20 MB limit; this only has to be above it.
      bodySizeLimit: '25mb',
    },
  },

  /**
   * Security headers for the HTML surface.
   *
   * The API sets its own; this app serves markup and had none — found by the
   * Phase 10 E2E suite. The threat model differs from the API's: this origin
   * renders HTML and runs scripts, so `default-src 'none'` is not available and
   * the policy has to permit Next's own bundles.
   *
   * `'unsafe-inline'` on styles is Next's inline critical CSS. Removing it needs
   * nonce-based styling, which is a real change rather than a config tweak;
   * recorded in docs/10-testing.md rather than pretended away.
   */
  async headers() {
    return [
      {
        source: '/:path*',
        headers: [
          { key: 'X-Content-Type-Options', value: 'nosniff' },
          { key: 'X-Frame-Options', value: 'DENY' },
          { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
          {
            key: 'Permissions-Policy',
            // Revisited when the mobile lot-walk needs the camera for VIN scanning.
            value: 'camera=(), microphone=(), geolocation=()',
          },
          { key: 'Cross-Origin-Opener-Policy', value: 'same-origin' },
          {
            key: 'Content-Security-Policy',
            value: [
              "default-src 'self'",
              "script-src 'self' 'unsafe-inline'",
              "style-src 'self' 'unsafe-inline'",
              `img-src 'self' data: blob: ${mediaOrigin}`,
              "font-src 'self'",
              // The browser never calls the API directly — the BFF does, server
              // side — so no external connect origin is needed.
              "connect-src 'self'",
              "frame-ancestors 'none'",
              "form-action 'self'",
              "base-uri 'self'",
              "object-src 'none'",
            ].join('; '),
          },
        ],
      },
    ];
  },
};

export default config;
