/**
 * Asset module declarations.
 *
 * Vite rewrites an asset import into a URL string at build time; TypeScript
 * knows nothing about that on its own.
 *
 * This lives in its own file rather than in `global.d.ts` because that file has
 * a top-level `import`/`export`, which makes it a *module* — and inside a
 * module, `declare module '*.png'` is read as augmenting an existing module
 * rather than declaring an ambient wildcard, so it silently fails to apply.
 * This file deliberately has no imports or exports, keeping it a script and the
 * declaration ambient.
 *
 * Declared by hand instead of pulling in `vite/client`, which would also bring
 * `import.meta.env` and a pile of ambient globals the renderer does not use.
 */

declare module '*.png' {
  const src: string;
  export default src;
}
