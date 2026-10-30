import { Link, type Metadata } from 'netjes';

export const metadata: Metadata = { title: 'No access' };

/**
 * Page.Forbidden() from the project layout's loader (ProjectLayout.cs) lands here, a level up: a layout's own
 * boundary files sit inside it, and a non-member must not see the project's layout at all.
 */
export default function ProjectForbidden() {
  return (
    <section className="grid max-w-lg gap-3 px-8 py-10">
      <p className="key">403</p>
      <h1 className="text-xl font-semibold">You are not on this project</h1>
      <p className="text-ink-300">Ask one of its owners to add you, then try again.</p>
      <Link href="/projects" className="btn w-fit">
        Your projects
      </Link>
    </section>
  );
}
