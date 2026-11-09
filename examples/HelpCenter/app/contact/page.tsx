import { definePage, type Metadata } from 'netjes';
import { pages } from '../../generated/netjes.ts';
import { MessageForm } from '../../src/MessageForm.tsx';

export const metadata: Metadata = { title: 'Contact · Help center' };

/** How to reach the help desk. An app may replace this page with .Override(); its page then gets these props too. */
export default definePage(pages['/contact'], function Contact({ email, hours }) {
  return (
    <section>
      <h1 className="mt-0 text-help-ink">Contact us</h1>
      <p>
        Write to <a href={`mailto:${email}`}>{email}</a>. We answer {hours}.
      </p>
      <MessageForm />
    </section>
  );
});
