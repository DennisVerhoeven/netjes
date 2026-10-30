import type { ErrorPageProps } from 'netjes';

/** Errors below the app shell: the sidebar stays, so the rest of the app is one click away. */
export default function AppError({ error, reset }: ErrorPageProps) {
  return (
    <section role="alert" className="grid max-w-lg gap-3 px-8 py-10">
      <p className="key">{error.status ?? 500}</p>
      <h1 className="text-xl font-semibold">This page failed to load</h1>
      <p className="whitespace-pre-wrap wrap-anywhere text-ink-300">{error.message}</p>
      <button type="button" className="btn w-fit" onClick={reset}>
        Try again
      </button>
    </section>
  );
}
