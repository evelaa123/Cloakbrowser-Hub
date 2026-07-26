import { resolve } from 'node:path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: {
    alias: {
      '@shared': resolve('src/shared'),
      '@main': resolve('src/main'),
      '@renderer': resolve('src/renderer'),
      // The renderer builds against preact/compat, so tests must resolve the
      // same way or they would exercise a different component runtime.
      react: resolve('node_modules/preact/compat'),
      'react-dom': resolve('node_modules/preact/compat'),
    },
  },
  esbuild: {
    jsx: 'automatic',
    jsxImportSource: 'preact',
  },
  test: {
    globals: true,
    include: ['tests/**/*.test.ts', 'tests/**/*.test.tsx'],
    // Node is the right default: most tests cover main-process logic. Component
    // tests opt into jsdom per file with a `@vitest-environment` docblock.
    environment: 'node',
  },
});
