import { resolve } from 'node:path';
import { defineConfig, externalizeDepsPlugin } from 'electron-vite';

const alias = {
  '@shared': resolve('src/shared'),
  '@main': resolve('src/main'),
  '@renderer': resolve('src/renderer'),
  react: resolve('node_modules/preact/compat'),
  'react-dom': resolve('node_modules/preact/compat'),
};

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()],
    resolve: { alias },
    build: {
      outDir: 'dist/main',
      lib: { entry: resolve('src/main/index.ts'), formats: ['cjs'], fileName: () => 'index.cjs' },
      rollupOptions: {
        // These must stay external, not bundled:
        //  - cloakbrowser / playwright-core spawn a real binary and resolve
        //    paths relative to their own package location.
        //  - undici must be a single instance shared with fetch-socks; two
        //    bundled copies cannot exchange dispatchers.
        external: ['electron', 'cloakbrowser', 'playwright-core', 'undici', 'fetch-socks'],
      },
    },
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    resolve: { alias },
    build: {
      outDir: 'dist/preload',
      lib: { entry: resolve('src/preload/index.ts'), formats: ['cjs'], fileName: () => 'index.cjs' },
    },
  },
  renderer: {
    root: 'src/renderer',
    resolve: { alias },
    build: {
      outDir: 'dist/renderer',
      rollupOptions: { input: resolve('src/renderer/index.html') },
    },
  },
});
