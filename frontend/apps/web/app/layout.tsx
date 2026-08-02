import type { Metadata } from 'next';
import type { ReactNode } from 'react';
import './globals.css';

export const metadata: Metadata = {
  title: 'MautoDesk',
  description: 'Dealership management for independent used-car dealers.',
};

const NAV = [
  { href: '/today', icon: '◉', label: 'Today' },
  { href: '/inventory', icon: '▤', label: 'Inventory' },
  { href: '/customers', icon: '☺', label: 'Customers' },
  { href: '/leads', icon: '⚑', label: 'Leads' },
  { href: '/deals', icon: '⛒', label: 'Deals' },
  { href: '/documents', icon: '▦', label: 'Documents' },
  { href: '/reports', icon: '▲', label: 'Reports' },
] as const;

export default function RootLayout({ children }: { readonly children: ReactNode }) {
  return (
    <html lang="en">
      <body>
        {/* Keyboard users reach the work surface without tabbing the whole rail. */}
        <a
          href="#main"
          className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-50 focus:rounded-md focus:bg-surface focus:px-4 focus:py-2"
        >
          Skip to content
        </a>

        <div className="grid min-h-screen grid-cols-[14rem_minmax(0,1fr)]">
          <nav
            aria-label="Main"
            className="flex flex-col gap-0.5 border-r border-line bg-surface px-2 py-3"
          >
            <div className="flex items-center gap-2 px-3 pb-5 pt-2 text-base font-bold tracking-tight">
              <span
                aria-hidden="true"
                className="grid size-6 place-items-center rounded-sm text-xs font-bold"
                style={{ background: 'var(--accent-bg)', color: 'var(--text-on-accent)' }}
              >
                M
              </span>
              MautoDesk
            </div>

            {NAV.map((item) => (
              <a
                key={item.href}
                href={item.href}
                // Inventory is the only implemented destination in this phase.
                aria-current={item.href === '/inventory' ? 'page' : undefined}
                className="flex min-h-9 items-center gap-3 rounded-r-md border-l-2 border-transparent px-3 text-muted hover:bg-hover hover:text-ink aria-[current=page]:border-l-[color:var(--accent-bg)] aria-[current=page]:bg-hover aria-[current=page]:font-semibold aria-[current=page]:text-ink"
              >
                <span aria-hidden="true" className="w-4 text-center opacity-80">
                  {item.icon}
                </span>
                {item.label}
              </a>
            ))}
          </nav>

          <div>
            <header className="sticky top-0 z-10 flex h-13 items-center gap-3 border-b border-line bg-surface px-5 py-2">
              <div className="flex min-h-8 max-w-md flex-1 items-center gap-2 rounded-md border border-line-strong bg-inset px-3 text-faint">
                <span aria-hidden="true">⌕</span>
                <span>Search vehicles, customers, deals…</span>
                <kbd className="ml-auto rounded-sm border border-line-strong bg-surface px-2 font-mono text-[0.6875rem] text-muted">
                  ⌘K
                </kbd>
              </div>
            </header>

            <main id="main">{children}</main>
          </div>
        </div>
      </body>
    </html>
  );
}
