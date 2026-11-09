import { definePage, useNavigation } from 'netjes';
import { Form, pages } from '../../../../generated/netjes.ts';
import { HelpfulVote } from '../../../../src/HelpfulVote.tsx';

const field =
  'w-full rounded-lg border border-help-line bg-transparent px-3 py-2 font-sans text-base text-help-ink aria-invalid:border-[#b42318]';

/**
 * An article, from the loader MapHelpCenter maps at "/articles/[slug]". Its note form posts to the page action at the
 * same URL, with or without JavaScript; the mounted Form stays on the page it is on, wherever the package is mounted.
 */
export default definePage(pages['/articles/[slug]'], function Article({ article, votes, feedbackCount, feedbackSent, errors, values }) {
  const navigation = useNavigation();
  const sending = navigation.state === 'submitting';
  const error = errors?.['comment'];
  return (
    <article>
      <h1 className="mt-0 text-help-ink">{article.title}</h1>
      {article.body.map((paragraph) => (
        <p key={paragraph}>{paragraph}</p>
      ))}
      <HelpfulVote slug={article.slug} votes={votes} />
      <section aria-labelledby="note-heading">
        <h2 id="note-heading" className="text-base text-help-ink">
          What was missing?
        </h2>
        {feedbackSent && (
          <p role="status" className="text-help-accent">
            Thanks, your note reached us.
          </p>
        )}
        <Form method="post" className="grid gap-2" noValidate>
          <label className="grid gap-1 text-sm font-semibold">
            Your note
            <textarea
              name="comment"
              rows={3}
              maxLength={500}
              defaultValue={values?.comment ?? ''}
              className={field}
              aria-invalid={error ? true : undefined}
              aria-describedby={error ? 'comment-error' : undefined}
            />
          </label>
          {error && (
            <p id="comment-error" className="m-0 text-sm text-[#b42318]">
              {error}
            </p>
          )}
          <button
            type="submit"
            disabled={sending}
            className="cursor-pointer justify-self-start rounded-full border-0 bg-help-accent px-4 py-2 text-sm font-semibold text-help-accent-ink disabled:cursor-progress disabled:opacity-60"
          >
            {sending ? 'Sending…' : 'Send note'}
          </button>
        </Form>
        <p className="text-sm text-help-muted">
          {feedbackCount} {feedbackCount === 1 ? 'note' : 'notes'} about this article so far.
        </p>
      </section>
    </article>
  );
});
