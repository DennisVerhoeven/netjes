import { pages } from '@netjes-examples/help-center/generated';
import { definePage, type Metadata } from 'netjes';

export const metadata: Metadata = { title: 'Contact' };

/**
 * TaskBoard's own contact page, in place of the help center's: Program.cs mounts the package with
 * .Override("/help/contact"). The package's C# loader still serves the page, so it reads the package's props schema.
 */
export default definePage(pages['/contact'], function TaskBoardContact({ email, hours }) {
  return (
    <section className="grid gap-3">
      <p className="label m-0 text-signal">TaskBoard support</p>
      <h1 className="m-0 text-2xl font-semibold">Talk to the people who build TaskBoard</h1>
      <p className="m-0 text-ink-300">
        Mail{' '}
        <a href={`mailto:${email}`} className="text-signal">
          {email}
        </a>
        , {hours}. Put the name of your board in the subject, and we can look at it with you.
      </p>
    </section>
  );
});
