import { Form, type Metadata, definePage, useNavigation } from 'netjes';
import { pages } from '../../generated/netjes.ts';

const label = 'grid gap-1.5 text-[0.9rem] font-semibold';
const field = 'w-full rounded-lg border border-line bg-paper px-3 py-2.5 font-sans text-base font-normal text-ink aria-invalid:border-danger';

export const metadata: Metadata = { title: 'Guestbook' };

const timeFormat = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' });

/** Signing posts to the MapPageAction handler for /guestbook. Works the same with JavaScript disabled. */
export default definePage(pages['/guestbook'], function Guestbook({ entries, errors, values }) {
  const navigation = useNavigation();
  const submitting = navigation.state === 'submitting';
  return (
    <div className="max-w-measure">
      <h1>Guestbook</h1>
      <Form method="post" className="card mb-8 grid gap-3" noValidate>
        <label className={label}>
          Name
          <input
            name="name"
            defaultValue={values?.name ?? ''}
            maxLength={40}
            className={field}
            aria-invalid={errors?.name ? true : undefined}
            aria-describedby={errors?.name ? 'name-error' : undefined}
          />
        </label>
        {errors?.name && (
          <p id="name-error" className="-mt-1.5 mb-0 text-sm text-danger">
            {errors.name}
          </p>
        )}
        <label className={label}>
          Message
          <textarea
            name="message"
            rows={3}
            defaultValue={values?.message ?? ''}
            maxLength={280}
            className={field}
            aria-invalid={errors?.message ? true : undefined}
            aria-describedby={errors?.message ? 'message-error' : undefined}
          />
        </label>
        {errors?.message && (
          <p id="message-error" className="-mt-1.5 mb-0 text-sm text-danger">
            {errors.message}
          </p>
        )}
        <button type="submit" className="button justify-self-start" disabled={submitting}>
          {submitting ? 'Signing…' : 'Sign the guestbook'}
        </button>
      </Form>
      {entries.length === 0 ? (
        <p className="text-muted">Nobody has signed yet. Be the first.</p>
      ) : (
        <ol className="m-0 grid list-none gap-3 p-0" aria-label="Entries">
          {entries.map((entry) => (
            <li key={`${entry.signedAt}-${entry.name}`} className="card">
              <p className="mt-0 mb-2">{entry.message}</p>
              <p className="m-0 text-[0.85rem] text-muted">
                {entry.name} · <time dateTime={entry.signedAt}>{timeFormat.format(new Date(entry.signedAt))}</time>
              </p>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
});
