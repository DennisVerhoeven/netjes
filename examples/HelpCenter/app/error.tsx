import type { ErrorPageProps } from 'netjes';
import { Link } from '../generated/netjes.ts';

/** A failure in one of the package's pages, inside its layout; the app's layouts above stay as they are. */
export default function HelpError({ error, reset }: ErrorPageProps) {
  return (
    <section role="alert">
      <h1 className="mt-0 text-help-ink">The help center could not load this page</h1>
      {!error.withheld && <p className="text-help-muted">{error.message}</p>}
      <p className="flex gap-3">
        <button
          type="button"
          onClick={reset}
          className="cursor-pointer rounded-full border-0 bg-help-accent px-4 py-2 text-sm font-semibold text-help-accent-ink"
        >
          Try again
        </button>
        <Link href="/" className="self-center text-help-accent">
          Back to the articles
        </Link>
      </p>
    </section>
  );
}
