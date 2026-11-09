import { useState, useTransition } from 'react';
import { type HelpVotes, serverActions } from '../generated/netjes.ts';

/** The C# server action help.vote, which MapHelpCenter maps through the mount at /_netjes/actions/help.vote. */
const vote = serverActions['help.vote'];

const button =
  'cursor-pointer rounded-full border border-help-accent bg-transparent px-4 py-1.5 text-sm font-semibold text-help-accent hover:bg-help-accent hover:text-help-accent-ink disabled:cursor-progress disabled:opacity-60';

/** "Was this helpful?", answered once per page view with the help.vote server action. */
export function HelpfulVote({ slug, votes }: { slug: string; votes: HelpVotes }) {
  const [counts, setCounts] = useState(votes);
  const [answered, setAnswered] = useState(false);
  const [pending, startTransition] = useTransition();

  const answer = (helpful: boolean) =>
    startTransition(async () => {
      const state = await vote({ slug, helpful });
      if (state.ok) {
        setCounts(state.result);
        setAnswered(true);
      }
    });

  return (
    <section aria-labelledby="helpful-heading" className="my-6 rounded-lg border border-help-line px-4 py-3">
      <h2 id="helpful-heading" className="m-0 text-base text-help-ink">
        Was this article helpful?
      </h2>
      {answered ? (
        <p role="status" className="mb-0 text-help-muted">
          Thanks for telling us. {counts.helpful} {counts.helpful === 1 ? 'reader' : 'readers'} found it helpful.
        </p>
      ) : (
        <p className="mb-0 flex gap-2">
          <button type="button" className={button} disabled={pending} onClick={() => answer(true)}>
            Yes
          </button>
          <button type="button" className={button} disabled={pending} onClick={() => answer(false)}>
            No
          </button>
        </p>
      )}
    </section>
  );
}
