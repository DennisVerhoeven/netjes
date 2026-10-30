// Test harness for components and pages that use Netjes: `render(<Page />, { wrapper: createTestApp().Wrapper })`.
// The router is a fake: it records what the component asked for and moves to the new URL, but loads nothing.
import { type DefaultOptions, QueryClient, QueryClientProvider, hydrate } from '@tanstack/react-query';
import { type ReactNode, useLayoutEffect, useMemo, useState } from 'react';
import { type ActionClient, type ActionState, setActionClient } from './actions.tsx';
import type { AntiforgeryToken, PagePayload, Params } from './payload.ts';
import { PublicConfigProvider } from './public-config.tsx';
import { dehydratedStateOf, mergeOptions } from './query.tsx';
import { type InternalRouter, type NavigationState, RouterContexts, type SubmitBody } from './router.tsx';

/** What the fake app of `createTestApp` reports to the components under test. */
export type TestAppOptions = {
  /** The current URL. Default `/`. */
  url?: string;
  /** The page's route pattern, e.g. `/blog/[slug]`. */
  route?: string;
  /** What `useLocale()` returns. */
  locale?: string;
  /** What `useDraftMode()` reports. */
  draftMode?: boolean;
  /** What `usePublicConfig()` returns, like the server's `Netjes:PublicConfig` section. */
  publicConfig?: Record<string, unknown>;
  params?: Params;
  /** What the server would send as `SharedProps`. */
  shared?: unknown;
  /** Seeded into the QueryClient the way `PageResult.WithQuery` seeds it, as of now. */
  queries?: ReadonlyArray<{ key: readonly unknown[]; data: unknown }>;
  /** Like `netjes({ query: { defaultOptions } })`; the harness also turns retries off. */
  defaultOptions?: DefaultOptions;
  /** What `useNavigation()` returns. Default idle. */
  navigation?: NavigationState;
  /** The token `<Form method="post">` adds. Default a test token; `false` for none. */
  antiforgery?: AntiforgeryToken | false;
  queryClient?: QueryClient;
  /**
   * Answers server action calls by name; unknown actions succeed with no result. Like `.WithQuery` and
   * `.Invalidate` in C#, an answer can set and invalidate query entries.
   */
  actions?: Record<string, (input: unknown) => TestActionAnswer | Promise<TestActionAnswer>>;
};

/** What a fake server action answers: a success or failure state, plus query entries to set or invalidate. */
export type TestActionAnswer = ActionState<unknown> & {
  queries?: ReadonlyArray<{ key: readonly unknown[]; data: unknown }>;
  invalidate?: ReadonlyArray<readonly unknown[]>;
  /** After a failure: the submitted fields the state's `values` keep, as the server names them (the action's declared, non-private fields). Default: all. */
  fields?: readonly string[];
};

/** One thing the component under test did: a navigation, a router call, a form post or a server action call. */
export type TestNavigation =
  | { type: 'push' | 'replace'; href: string }
  | { type: 'refresh' | 'back' | 'forward' }
  | { type: 'submit'; action: string; data: URLSearchParams | FormData }
  | { type: 'action'; name: string; input: unknown };

/** The fake app `createTestApp` returns: a wrapper for Testing Library and a record of what the components did. */
export type TestApp = {
  /** Pass as Testing Library's `wrapper`. */
  Wrapper: (props: { children: ReactNode }) => ReactNode;
  /** Every navigation, router call and form post, in order. */
  navigations: TestNavigation[];
  queryClient: QueryClient;
};

/**
 * Registers the stand-in in the commit, and only then: a render React throws away (`renderToString`, children that
 * threw) leaves calls made outside any Wrapper failing at once. React runs the effects of earlier siblings first, and
 * a component's after those of the components inside it, so rendered ahead of the children this runs before any of
 * their effects, layout effects included.
 */
function RegisterActionClient({ client }: { client: ActionClient }) {
  useLayoutEffect(() => setActionClient(client), [client]);
  return null;
}

const TEST_TOKEN: AntiforgeryToken = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'test-token' };

function dataOf(body: SubmitBody): URLSearchParams | FormData {
  return body.contentType === null ? body.body : new URLSearchParams(body.body);
}

/** Creates a fake app around the components under test: pass its `Wrapper` to Testing Library's `render`. The router loads nothing; it records what was asked of it. */
export function createTestApp(options: TestAppOptions = {}): TestApp {
  const navigations: TestNavigation[] = [];
  const defaults = mergeOptions(options.defaultOptions);
  const queryClient =
    options.queryClient ??
    new QueryClient({ defaultOptions: { ...defaults, queries: { retry: false, gcTime: Number.POSITIVE_INFINITY, ...defaults.queries } } });
  const now = Date.now();
  const seeded = dehydratedStateOf(options.queries?.map((query) => ({ ...query, updatedAt: now })));
  if (seeded) hydrate(queryClient, seeded);
  const initialUrl = new URL(options.url ?? '/', window.location.origin);
  // Forms without an action post to the current URL, which they read from the window.
  window.history.replaceState(null, '', initialUrl.href);
  const antiforgery = options.antiforgery === false ? undefined : (options.antiforgery ?? TEST_TOKEN);
  // The router's stand-in for server action calls: records each one and answers it from `options.actions`.
  const actionClient: ActionClient = {
    async invoke(name, _url, input) {
      navigations.push({ type: 'action', name, input });
      const answer: TestActionAnswer = (await options.actions?.[name]?.(input)) ?? { ok: true, result: undefined };
      for (const query of answer.queries ?? []) queryClient.setQueryData(query.key, query.data);
      for (const queryKey of answer.invalidate ?? []) void queryClient.invalidateQueries({ queryKey: [...queryKey] });
      return answer.ok
        ? { kind: 'action', ok: true, status: 200, result: answer.result, revalidate: false }
        : {
            kind: 'action',
            ok: false,
            status: answer.status,
            errors: answer.errors,
            ...(answer.fields ? { fields: [...answer.fields] } : {}),
            revalidate: false,
          };
    },
  };

  function Wrapper({ children }: { children: ReactNode }) {
    const [url, setUrl] = useState(initialUrl);
    const router = useMemo<InternalRouter>(() => {
      const go = (type: 'push' | 'replace', href: string) => {
        navigations.push({ type, href });
        const next = new URL(href, window.location.href);
        window.history.replaceState(null, '', next.href);
        setUrl(next);
        return Promise.resolve();
      };
      return {
        push: (href) => go('push', href),
        replace: (href) => go('replace', href),
        prefetch: () => {},
        refresh: () => {
          navigations.push({ type: 'refresh' });
          return Promise.resolve();
        },
        back: () => navigations.push({ type: 'back' }),
        forward: () => navigations.push({ type: 'forward' }),
        submit: (action, body) => {
          navigations.push({ type: 'submit', action: action.href, data: dataOf(body) });
          return Promise.resolve();
        },
        onActionResponse: () => () => {},
        onCookiesChanged: () => () => {},
      };
    }, []);
    const payload = useMemo<PagePayload>(
      () => ({
        kind: 'page',
        route: options.route ?? null,
        page: null,
        segments: [],
        props: {},
        params: options.params ?? {},
        shared: options.shared ?? null,
        status: 200,
        url: url.pathname + url.search,
        version: 'test',
        ...(antiforgery ? { antiforgery } : {}),
        ...(options.draftMode ? { draftMode: true } : {}),
        ...(options.locale ? { locale: options.locale } : {}),
      }),
      [url],
    );

    return (
      <QueryClientProvider client={queryClient}>
        <PublicConfigProvider value={options.publicConfig ?? {}}>
          <RouterContexts router={router} payload={payload} url={url} navigation={options.navigation ?? { state: 'idle' }}>
            <RegisterActionClient client={actionClient} />
            {children}
          </RouterContexts>
        </PublicConfigProvider>
      </QueryClientProvider>
    );
  }

  return { Wrapper, navigations, queryClient };
}
