import { type ComponentType, type ReactNode, Suspense, lazy, useSyncExternalStore } from 'react';

const subscribe = () => () => {};

/** False on the server and while hydrating, true afterwards (and for client-only renders): hydration-safe. */
function useHydrated() {
  return useSyncExternalStore(
    subscribe,
    () => true,
    () => false,
  );
}

function ClientOnly({ fallback, children }: { fallback: ReactNode; children: ReactNode }) {
  return useHydrated() ? children : fallback;
}

/** Options for `dynamic`. */
export type DynamicOptions = {
  /** Server-render the component (default), or only render it in the browser (`false`), e.g. for code that needs `window`. */
  ssr?: boolean;
  /** Shown while the module loads, and on the server when `ssr` is false. */
  loading?: ComponentType;
};

/**
 * Like `next/dynamic`: a component whose module loads on demand, in its own chunk.
 * `dynamic(() => import('./Chart.tsx'), { loading: Spinner })`.
 */
export function dynamic<P extends object>(
  load: () => Promise<{ default: ComponentType<P> } | ComponentType<P>>,
  options: DynamicOptions = {},
): ComponentType<P> {
  const Lazy = lazy(async () => {
    const module = await load();
    return { default: typeof module === 'function' ? module : module.default };
  });
  const Loading = options.loading;

  function Dynamic(props: P) {
    const fallback = Loading ? <Loading /> : null;
    const content = (
      <Suspense fallback={fallback}>
        <Lazy {...props} />
      </Suspense>
    );
    return options.ssr === false ? <ClientOnly fallback={fallback}>{content}</ClientOnly> : content;
  }
  Dynamic.displayName = 'Dynamic';
  return Dynamic;
}

/** A component that renders only in the browser, after hydration; `fallback` (default nothing) until then and on the server. */
export function clientOnly<P extends object>(Component: ComponentType<P>, options: { fallback?: ReactNode } = {}): ComponentType<P> {
  function ClientOnlyComponent(props: P) {
    return (
      <ClientOnly fallback={options.fallback ?? null}>
        <Component {...props} />
      </ClientOnly>
    );
  }
  ClientOnlyComponent.displayName = `clientOnly(${Component.displayName ?? Component.name ?? 'Component'})`;
  return ClientOnlyComponent;
}
