import { Form, Link, definePage, useAction } from 'netjes';
import { useOptimistic } from 'react';
import { Field, SubmitButton, errorsOf, valueOf } from '../../../components/forms.tsx';
import { type ProjectCard, pages, serverActions } from '../../../generated/netjes.ts';

type ShownProject = ProjectCard & { pending?: boolean };

/** ProjectsEndpoints.cs: the user's projects, and a form to start one (shown at once, saved by a server action). */
export default definePage(pages['/projects'], function Projects({ projects }) {
  const [shown, addPending] = useOptimistic<ShownProject[], ShownProject>(projects, (current, next) => [...current, next]);
  const [state, formAction] = useAction(serverActions['projects.create'], {
    onSubmit: (data) =>
      addPending({
        slug: String(data.get('slug') ?? ''),
        name: String(data.get('name') ?? ''),
        description: String(data.get('description') ?? ''),
        role: 'owner',
        openTasks: 0,
        doneTasks: 0,
        pending: true,
      }),
  });

  return (
    <main className="grid gap-10 px-8 py-8">
      <header className="flex items-end justify-between gap-4">
        <div>
          <p className="label">Workspace</p>
          <h1 className="text-2xl font-semibold tracking-tight">Projects</h1>
        </div>
      </header>

      {shown.length === 0 ? (
        <p className="panel max-w-lg p-6 text-ink-300">No projects yet. Start one below; it gets a board, numbered tasks and a history.</p>
      ) : (
        <ul aria-label="Your projects" className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {shown.map((item) => (
            <li key={item.slug} aria-busy={item.pending || undefined} className={item.pending ? 'opacity-60' : undefined}>
              <Link href={`/projects/${item.slug}`} className="panel grid gap-3 p-5 transition-colors hover:border-ink-600">
                <div className="flex items-center justify-between gap-3">
                  <span className="truncate text-lg font-semibold">{item.name}</span>
                  <span className="key uppercase">{item.slug}</span>
                </div>
                <p className="line-clamp-2 min-h-[2.6em] text-ink-400">{item.description || 'No description.'}</p>
                <p className="flex gap-4 text-[13px] text-ink-300">
                  <span>{item.pending ? 'Creating…' : `${item.openTasks} open`}</span>
                  {!item.pending && <span>{item.doneTasks} done</span>}
                  <span className="ml-auto label">{item.role}</span>
                </p>
              </Link>
            </li>
          ))}
        </ul>
      )}

      <section aria-labelledby="new-project" className="grid max-w-xl gap-4">
        <h2 id="new-project" className="text-lg font-semibold">
          New project
        </h2>
        <Form action={formAction} className="panel grid gap-4 p-5">
          <Field label="Name" name="name" required defaultValue={valueOf(state, 'name')} errors={errorsOf(state, 'name')} />
          <Field
            label="Key"
            name="slug"
            required
            pattern="[a-z][a-z0-9\-]{1,23}"
            defaultValue={valueOf(state, 'slug')}
            errors={errorsOf(state, 'slug')}
            hint="Lowercase, used in links and task numbers: web → WEB-1."
          />
          <Field label="Description" name="description" multiline rows={2} defaultValue={valueOf(state, 'description')} errors={errorsOf(state, 'description')} />
          <SubmitButton pending="Creating…">Create project</SubmitButton>
        </Form>
      </section>
    </main>
  );
});
