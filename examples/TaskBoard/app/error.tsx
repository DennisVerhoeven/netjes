import type { ErrorPageProps, Metadata } from 'netjes';

export const metadata: Metadata = { title: 'Something went wrong' };

/** The last boundary inside the root layout: a loader or a page failed. */
export default function RootError({ error, reset }: ErrorPageProps) {
  return (
    <main className="grid min-h-screen place-items-center px-6">
      <div role="alert" className="grid max-w-md gap-4 text-center">
        <p className="key">{error.status ?? 500}</p>
        <h1 className="text-2xl font-semibold">Something went wrong</h1>
        <p className="whitespace-pre-wrap wrap-anywhere text-ink-300">{error.message}</p>
        <button type="button" className="btn mx-auto" onClick={reset}>
          Try again
        </button>
      </div>
    </main>
  );
}
