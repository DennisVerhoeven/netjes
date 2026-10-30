import type { ComponentType, ReactNode } from 'react';
import type { Metadata } from './metadata.tsx';
import type { Params, SegmentRef } from './payload.ts';

export type PageComponent = ComponentType<Record<string, unknown>>;
/** The props every layout gets: its `children` and the route `params`. The loader's props and parallel route slots arrive as further props. */
export type LayoutProps = { children: ReactNode; params: Params };
export type LayoutComponent = ComponentType<LayoutProps & Record<string, unknown>>;
/** The props of an `error.tsx`: what was thrown, and a way to try rendering the segment again. */
export type ErrorPageProps = { error: Error & { status?: number }; reset: () => void };

export type LoadedSegment = {
  id: string;
  /** The layout's `export const metadata`. */
  metadata?: Metadata;
  Layout?: LayoutComponent;
  Template?: ComponentType<{ children: ReactNode }>;
  Loading?: ComponentType;
  Error?: ComponentType<ErrorPageProps>;
  /** Parallel routes: what each slot renders (null when it renders nothing for this page). */
  slots?: Record<string, ComponentType<{ params: Params }> | null>;
};

/** What `import.meta.glob` produces: root-relative paths (`/app/x/page.tsx`) to lazy imports. */
export type ModuleMap = Record<string, () => Promise<unknown>>;

export type RouteModules = { page: PageComponent | null; segments: LoadedSegment[]; pageMetadata?: Metadata };

export type ModuleLoader = {
  load(file: string): Promise<ComponentType<never>>;
  /** A loaded module's `export const metadata`, if any. */
  metadataOf(file: string): Metadata | undefined;
  loadRoute(route: { page: string | null; segments: readonly SegmentRef[] }): Promise<RouteModules>;
};

/** The server knows a page this client build does not: the app was redeployed or a page was just added. */
export class MissingModuleError extends Error {
  override name = 'MissingModuleError';
  readonly file: string;
  constructor(file: string) {
    super(`netjes: no module for ${file} in this build`);
    this.file = file;
  }
}

/**
 * A module's import failed in the browser: the network dropped the request, a deploy removed its chunk, or the module
 * threw while it ran. The browser's own error is its `cause`, and its message is in this one's: for a chunk that did
 * not load, that names only the chunk.
 */
export class ModuleLoadError extends Error {
  override name = 'ModuleLoadError';
  readonly file: string;
  constructor(file: string, cause: unknown) {
    super(`netjes: ${file} could not be loaded (${cause instanceof Error ? cause.message : String(cause)})`, { cause });
    this.file = file;
  }
}

export type ModuleLoaderOptions = {
  /**
   * Reject a failed import with a {@link ModuleLoadError} that names the app's file. The client entry turns it on. The
   * server leaves it off: an import that fails there is the app's code (a syntax error, a bad import, a module that
   * needs the browser), and its own message and stack are what the sidecar logs and the development error page shows.
   */
  nameFailedImports?: boolean;
};

function isComponent(value: unknown): value is ComponentType<never> {
  // Function components, classes, and the objects returned by memo/forwardRef/lazy.
  return typeof value === 'function' || (typeof value === 'object' && value !== null && '$$typeof' in value);
}

export function createModuleLoader(modules: ModuleMap, options: ModuleLoaderOptions = {}): ModuleLoader {
  const cache = new Map<string, Promise<ComponentType<never>>>();
  const metadata = new Map<string, Metadata>();

  function load(file: string): Promise<ComponentType<never>> {
    const cached = cache.get(file);
    if (cached) return cached;
    const importer = modules[`/${file}`];
    if (!importer) return Promise.reject(new MissingModuleError(file));
    const loading = importer().then(
      (module) => {
        const component = typeof module === 'object' && module !== null && 'default' in module ? module.default : undefined;
        if (!isComponent(component)) throw new Error(`netjes: ${file} must have a default export that is a React component`);
        const exported = (module as { metadata?: unknown }).metadata;
        if (typeof exported === 'object' && exported !== null) metadata.set(file, exported as Metadata);
        return component;
      },
      (error: unknown) => {
        throw options.nameFailedImports ? new ModuleLoadError(file, error) : error;
      },
    );
    cache.set(file, loading);
    loading.catch(() => cache.delete(file));
    return loading;
  }

  return {
    load,
    metadataOf: (file) => metadata.get(file),
    async loadRoute(route) {
      const optional = (file: string | undefined) => (file ? load(file) : Promise.resolve(undefined));
      const [page, segments] = await Promise.all([
        route.page ? load(route.page) : null,
        Promise.all(
          route.segments.map(async (segment) => {
            const [Layout, Template, Loading, Error] = await Promise.all(
              [segment.layout, segment.template, segment.loading, segment.error].map(optional),
            );
            const loaded: LoadedSegment = { id: segment.id };
            if (segment.slots) {
              const entries = await Promise.all(
                Object.entries(segment.slots).map(async ([name, file]) => [name, file ? await load(file) : null] as const),
              );
              loaded.slots = Object.fromEntries(entries) as NonNullable<LoadedSegment['slots']>;
            }
            const layoutMetadata = segment.layout ? metadata.get(segment.layout) : undefined;
            if (layoutMetadata) loaded.metadata = layoutMetadata;
            if (Layout) loaded.Layout = Layout as LayoutComponent;
            if (Template) loaded.Template = Template as LoadedSegment['Template'] & object;
            if (Loading) loaded.Loading = Loading as ComponentType;
            if (Error) loaded.Error = Error as ComponentType<ErrorPageProps>;
            return loaded;
          }),
        ),
      ]);
      const pageMetadata = route.page ? metadata.get(route.page) : undefined;
      return { page: (page ?? null) as PageComponent | null, segments, ...(pageMetadata ? { pageMetadata } : {}) };
    },
  };
}
