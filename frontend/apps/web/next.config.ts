import type { NextConfig } from 'next';

const config: NextConfig = {
  reactStrictMode: true,
  transpilePackages: ['@mautodesk/api-client'],
  poweredByHeader: false,

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
              "img-src 'self' data: blob:",
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
