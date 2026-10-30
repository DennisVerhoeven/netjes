import type { DefaultOptions } from '@tanstack/react-query';
import type { Options as ReactOptions } from '@vitejs/plugin-react';
import type { PluginOption } from 'vite';

/** Options for the `netjes()` Vite plugin. */
export type NetjesPluginOptions = {
  /** The directory of page files (Next.js App Router conventions), relative to the Vite root. Must match Netjes:AppDirectory. Default `app`. */
  appDir?: string;
  /**
   * Like Next.js's `basePath`: the path the app is mounted under with `app.UsePathBase(...)`, e.g. `/docs`.
   * Built asset URLs get it; links, forms, redirects and router calls get it from the server at runtime.
   */
  basePath?: string;
  /** Options for @vitejs/plugin-react, which netjes() includes. */
  react?: ReactOptions;
  /** Tailwind CSS through @tailwindcss/vite. Default true; `@import "tailwindcss"` in a CSS file a layout imports. */
  tailwind?: boolean;
  /**
   * The built-in TanStack Query client, seeded from C# with `PageResult.WithQuery`. Default true. `false` removes it
   * (and TanStack Query from the bundle); an object configures it. Options are written into the bundle as literals,
   * so plain values (numbers including Infinity, strings, booleans, objects, arrays) work and functions are rejected.
   */
  query?: boolean | { defaultOptions?: DefaultOptions };
  /** Vitest defaults (jsdom, Testing Library cleanup, no e2e/ or build output). Default true; the app's own `test` settings win. */
  test?: boolean;
};

/** The Netjes Vite plugin: page routes, the client and server builds, the dev server, TanStack Query, Tailwind and Vitest defaults. */
export default function netjes(options?: NetjesPluginOptions): PluginOption[];
