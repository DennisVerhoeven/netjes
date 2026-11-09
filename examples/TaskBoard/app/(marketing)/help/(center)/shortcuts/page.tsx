import { Link, type Metadata } from 'netjes';

export const metadata: Metadata = { title: 'Shortcuts' };

const tips = [
  ['Share a task by its URL', 'Every task has a key, such as WEB-12, and a page of its own: send its address to a teammate.'],
  ['Go Back without waiting', 'Back and Forward return to the board as you left it, without loading it again.'],
  ['Stay signed in', 'A session lasts two weeks from the last time you used TaskBoard, on each browser.'],
] as const;

/**
 * A page of TaskBoard's own, in the help center's (center) route group: it renders inside the package's layouts, its
 * list of articles beside it, and gets no loader of its own.
 */
export default function Shortcuts() {
  return (
    <section className="grid gap-4">
      <h1 className="m-0 text-2xl font-semibold">Shortcuts</h1>
      <dl className="m-0 grid gap-3">
        {tips.map(([title, text]) => (
          <div key={title}>
            <dt className="font-semibold">{title}</dt>
            <dd className="m-0 text-ink-300">{text}</dd>
          </div>
        ))}
      </dl>
      <p className="m-0">
        <Link href="/projects" className="text-signal">
          Open your board
        </Link>
      </p>
    </section>
  );
}
