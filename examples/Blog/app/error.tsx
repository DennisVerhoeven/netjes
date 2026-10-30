import { type ErrorPageProps, Link, type Metadata } from 'netjes';

export const metadata: Metadata = { title: 'Something went wrong' };

/**
 * Rendered inside the root layout with status 500 (or the request's error status) when a loader, action or page
 * throws. The server sends the stack only when error details are on (Development).
 */
export default function ErrorPage({ error, stack }: ErrorPageProps & { stack?: string | null }) {
  return (
    <section className="max-w-measure pt-8">
      <p className="m-0 font-serif text-[4.5rem]/none font-bold text-accent">{error.status ?? 500}</p>
      <h1>Something went wrong</h1>
      <p className="whitespace-pre-wrap wrap-anywhere">{error.message}</p>
      {stack && <pre className="text-[0.78rem] whitespace-pre-wrap">{stack}</pre>}
      <p>
        <Link href="/">Back to the home page</Link>
      </p>
    </section>
  );
}
