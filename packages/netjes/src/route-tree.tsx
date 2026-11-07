import { Component, type ReactElement, type ReactNode, Suspense, createContext } from 'react';
import { withheldError } from './errors.ts';
import { type Metadata, MetadataTags, resolveMetadata } from './metadata.tsx';
import type { ErrorPageProps, LoadedSegment, RouteModules } from './modules.ts';
import type { PageKind, Params, SegmentRef } from './payload.ts';

export type { ErrorPageProps } from './modules.ts';

type RouteTreeProps = {
  modules: RouteModules;
  /** The payload's segments, for their loader props; matched to `modules.segments` by position. */
  segments?: readonly SegmentRef[];
  props: Record<string, unknown>;
  params: Params;
  status: number;
  pageKind?: PageKind | undefined;
  /** Remounts the page and templates (not layouts) when it changes, so page state does not leak between URLs. */
  pageKey: string;
  /** What `reset` does on an error page the server chose: load the page again. */
  retry?: () => void;
  /** The error an error page (`pageKind` error) gets in place of the one the server's props describe: one caught here. */
  error?: ErrorPageProps['error'];
  /** Metadata from the C# loader. */
  metadata?: Metadata | undefined;
};

/** How many URL segments the layout rendering here covers: what useSelectedLayoutSegment(s) count from. */
export const LayoutDepthContext = createContext(0);

/**
 * URL depth of a segment folder when the server did not send it (special pages): its folders after the router root,
 * route groups not counted.
 */
function depthOf(segmentId: string): number {
  return segmentId.split('/').slice(1).filter((folder) => !(folder.startsWith('(') && folder.endsWith(')'))).length;
}

type BoundaryProps = { resetKey: unknown; fallback: (error: unknown, reset: () => void) => ReactNode; children: ReactNode };

/** Catches render errors below it; resets when `resetKey` changes (a navigation) or `reset` is called. */
export class ErrorBoundary extends Component<BoundaryProps, { error: unknown; failed: boolean }> {
  override state = { error: null as unknown, failed: false };

  static getDerivedStateFromError(error: unknown) {
    return { error, failed: true };
  }

  override componentDidUpdate(previous: BoundaryProps) {
    if (previous.resetKey !== this.props.resetKey && this.state.failed) this.setState({ error: null, failed: false });
  }

  private readonly reset = () => this.setState({ error: null, failed: false });

  override render() {
    return this.state.failed ? this.props.fallback(this.state.error, this.reset) : this.props.children;
  }
}

/** What a boundary caught, as the Error `error.tsx` gets: an Error as it is, a string as its message, else a stand-in. */
export function asError(error: unknown): ErrorPageProps['error'] {
  if (error instanceof Error) return error;
  return typeof error === 'string' ? new Error(error) : withheldError();
}

/**
 * The error the server reported (props of the error page it rendered), as an Error for `error.tsx`: marked withheld
 * when the server says its message stands in for the exception's, and with the server's stack or none, never the
 * stack of the code here that built it.
 */
function serverError(props: Record<string, unknown>): ErrorPageProps['error'] {
  const error: ErrorPageProps['error'] = new Error(typeof props.message === 'string' ? props.message : 'Something went wrong');
  if (typeof props.status === 'number') error.status = props.status;
  if (props.withheld === true) error.withheld = true;
  // Defined rather than assigned, as an own property: Firefox keeps the stack in an accessor on Error.prototype.
  Object.defineProperty(error, 'stack', { value: typeof props.stack === 'string' ? props.stack : undefined, writable: true, configurable: true });
  return error;
}

/**
 * The page wrapped in its segments, outermost first. Per segment, as in the Next.js App Router:
 * layout > template > error boundary > Suspense (loading) > the next segment or the page. Layouts keep their state
 * while the page underneath changes; templates remount.
 */
export function RouteTree({ modules, segments = [], props, params, status, pageKind, pageKey, retry, metadata, error }: RouteTreeProps): ReactElement {
  const Page = modules.page;
  const pageProps = pageKind === 'error' ? { ...props, error: error ?? serverError(props), reset: retry ?? (() => {}) } : props;
  let element: ReactElement = Page ? <Page key={pageKey} {...pageProps} /> : <BuiltInPage key={pageKey} status={status} props={props} />;
  for (let i = modules.segments.length - 1; i >= 0; i--) {
    const { Layout, Template, Loading, Error: SegmentError, slots } = modules.segments[i]!;
    if (Loading) element = <Suspense fallback={<Loading />}>{element}</Suspense>;
    if (SegmentError) {
      const inner = element;
      element = (
        <ErrorBoundary resetKey={pageKey} fallback={(error, reset) => <SegmentError error={asError(error)} reset={reset} />}>
          {inner}
        </ErrorBoundary>
      );
    }
    if (Template) element = <Template key={pageKey}>{element}</Template>;
    if (Layout) {
      element = (
        <LayoutDepthContext value={segments[i]?.depth ?? depthOf(modules.segments[i]!.id)}>
          <Layout {...segments[i]?.props} {...slotElements(slots, params, segments[i]?.slotParams)} params={params}>
            {element}
          </Layout>
        </LayoutDepthContext>
      );
    }
  }
  const layoutsMetadata = modules.segments.map((segment) => segment.metadata);
  const hasMetadata = layoutsMetadata.some(Boolean) || Boolean(modules.pageMetadata) || Boolean(metadata);
  // One shape whether or not this page has metadata: the layouts stay at the same place in the tree, so React keeps
  // them mounted when the user navigates between a page with metadata and one without.
  return (
    <>
      {hasMetadata ? <MetadataTags metadata={resolveMetadata(layoutsMetadata, modules.pageMetadata, metadata)} /> : null}
      {element}
    </>
  );
}

/**
 * Parallel routes: each slot as a prop of its layout, named like the folder without the @ (null renders nothing).
 * Each slot's element gets the slot page's own params when the server sent them (else the page's).
 */
function slotElements(slots: LoadedSegment['slots'], params: Params, slotParams: Record<string, Params> | undefined): Record<string, ReactElement | null> {
  if (!slots) return {};
  return Object.fromEntries(Object.entries(slots).map(([name, Slot]) => [name, Slot ? <Slot params={slotParams?.[name] ?? params} /> : null]));
}

/** Shown for 404s and errors when the app has no not-found or error page of its own. */
export function BuiltInPage({ status, props }: { status: number; props: Record<string, unknown> }) {
  const notFound = status === 404;
  const title = notFound ? 'This page could not be found' : 'Something went wrong';
  const message = !notFound && typeof props.message === 'string' ? props.message : null;
  const stack = !notFound && typeof props.stack === 'string' ? props.stack : null;
  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: '42rem', margin: '12vh auto', padding: '0 1.5rem', lineHeight: 1.5 }}>
      <title>{`${status} · ${title}`}</title>
      <p style={{ fontSize: '3.5rem', fontWeight: 700, margin: 0 }}>{status}</p>
      <h1 style={{ fontSize: '1.5rem', margin: '0.25rem 0 1rem' }}>{title}</h1>
      {/* A message can take several lines, such as a compiler's code frame in development. */}
      {message && <p style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{message}</p>}
      {stack && <pre style={{ whiteSpace: 'pre-wrap', fontSize: '0.8rem', background: '#f4f4f5', padding: '1rem', borderRadius: 8 }}>{stack}</pre>}
      <p>
        <a href="/">Go to the home page</a>
      </p>
    </main>
  );
}
