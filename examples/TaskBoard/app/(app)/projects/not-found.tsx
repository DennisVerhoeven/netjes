import { Link, type Metadata, usePathname } from 'netjes';

export const metadata: Metadata = { title: 'No such project' };

/** A project slug that does not exist (Page.NotFound() from the project layout's loader). */
export default function ProjectNotFound() {
  const pathname = usePathname();
  return (
    <section className="grid max-w-lg gap-3 px-8 py-10">
      <p className="key">404 · {pathname}</p>
      <h1 className="text-xl font-semibold">There is no project here</h1>
      <Link href="/projects" className="btn w-fit">
        Your projects
      </Link>
    </section>
  );
}
