import type { ErrorPageProps } from 'netjes';

/** Errors below the root layout land here; the layout stays usable. */
export default function ErrorPage({ error, reset }: ErrorPageProps) {
  return (
    <section role="alert" className="space-y-4">
      <h1 className="text-3xl font-bold tracking-tight">Something went wrong</h1>
      {/* In development the message can take several lines: a syntax error comes with a code frame. */}
      <p className="whitespace-pre-wrap wrap-anywhere text-slate-600">{error.message}</p>
      <button type="button" onClick={reset} className="cursor-pointer rounded-full bg-slate-900 px-4 py-2 font-medium text-white">
        Try again
      </button>
    </section>
  );
}
