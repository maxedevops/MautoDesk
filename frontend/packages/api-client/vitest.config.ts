import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
  resolve: {
    // The source uses NodeNext-style .js specifiers that resolve to .ts files.
    extensions: ['.ts', '.js', '.json'],
  },
});
