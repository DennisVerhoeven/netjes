import type { ErrorPageProps } from 'netjes';

/** Errors below the docs layout land here; the layout and its navigation stay usable. */
export default function DocsError({ error, reset }: ErrorPageProps) {
  return (
    <section role="alert" className="prose">
      <h1>This doc failed to load</h1>
      <p className="whitespace-pre-wrap wrap-anywhere">{error.message}</p>
      <button type="button" className="button" onClick={reset}>
        Try again
      </button>
    </section>
  );
}
