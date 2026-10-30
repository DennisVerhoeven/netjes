import { type DefaultOptions, type DehydratedState, HydrationBoundary, QueryClient, QueryClientProvider, dehydrate } from '@tanstack/react-query';
import { type ReactNode, useContext, useEffect, useMemo, useState } from 'react';
import type { SeededQuery } from './payload.ts';
import { RenderCleanupContext } from './render-cleanup.ts';
import { useInternalRouter, usePagePayload } from './router.tsx';

export type QueryShellOptions = {
  /** Merged over the Netjes defaults (`staleTime` of one minute, so seeded data is not refetched straight away). */
  defaultOptions?: DefaultOptions;
};

const DEFAULTS: DefaultOptions = { queries: { staleTime: 60_000 } };

/** The Netjes defaults with the app's options merged over them, as the built-in client and the test harness use. */
export function mergeOptions(options: DefaultOptions | undefined): DefaultOptions {
  return {
    ...DEFAULTS,
    ...options,
    queries: { ...DEFAULTS.queries, ...options?.queries },
    mutations: { ...DEFAULTS.mutations, ...options?.mutations },
  };
}

/**
 * Turns the payload's seeded queries into TanStack's dehydrated state, using only public APIs. Server timestamps
 * are moved onto the browser clock, so freshness and "newer data wins" compare like with like.
 */
export function dehydratedStateOf(queries: readonly SeededQuery[] | undefined, clockOffset = 0): DehydratedState | undefined {
  if (!queries?.length) return undefined;
  const scratch = new QueryClient();
  for (const query of queries) scratch.setQueryData(query.key, query.data, { updatedAt: query.updatedAt + clockOffset });
  const state = dehydrate(scratch);
  scratch.clear();
  return state;
}

/**
 * The server keeps one QueryClient per render in a long-lived process: a finite gcTime would leave a timer (and the
 * client's data) behind for every query of every request.
 */
function onServer(options: DefaultOptions): DefaultOptions {
  return { ...options, queries: { ...options.queries, gcTime: Number.POSITIVE_INFINITY } };
}

/**
 * The app-wide QueryClient: one per request on the server, one per page load in the browser (it survives
 * client navigations). Each page's seeded queries are hydrated into it; data the client fetched more recently wins.
 */
export function createQueryShell(options: QueryShellOptions = {}) {
  const defaultOptions = mergeOptions(options.defaultOptions);

  return function QueryShell({ children }: { children: ReactNode }) {
    const onRenderEnd = useContext(RenderCleanupContext);
    const [client] = useState(() => {
      const created = new QueryClient({ defaultOptions: typeof window === 'undefined' ? onServer(defaultOptions) : defaultOptions });
      // A query's own gcTime still schedules a timer; the sidecar drops the client with its document.
      onRenderEnd?.(() => created.clear());
      return created;
    });
    const { queries, clockOffset } = usePagePayload();
    const state = useMemo(() => dehydratedStateOf(queries, clockOffset), [queries, clockOffset]);
    const router = useInternalRouter();
    // Server actions can set entries (.WithQuery) and invalidate keys (.Invalidate) in C#.
    useEffect(
      () =>
        router.onActionResponse((response) => {
          for (const query of response.queries ?? []) client.setQueryData(query.key, query.data);
          for (const queryKey of response.invalidate ?? []) void client.invalidateQueries({ queryKey });
        }),
      [router, client],
    );
    // Signing in or out (a call or post that set or deleted a cookie): any query may hold the previous user's data,
    // those seeded by earlier pages too, so all of them go. The page loads again, seeding its own; the action's own
    // query entries are applied after this.
    useEffect(() => router.onCookiesChanged(() => client.clear()), [router, client]);
    return (
      <QueryClientProvider client={client}>
        <HydrationBoundary state={state}>{children}</HydrationBoundary>
      </QueryClientProvider>
    );
  };
}
