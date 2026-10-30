import { Link, defineLayout, usePathname } from 'netjes';
import { StateDot } from '../../../../components/task-bits.tsx';
import { layouts, route } from '../../../../generated/netjes.ts';
import { stateLabels, stateOrder, useProjectStats } from '../../../../lib/project.ts';

/**
 * Every page of a project. ProjectLayout.cs loads it (members only) and seeds the stats query, which the header keeps
 * live with TanStack Query; task actions invalidate it.
 */
export default defineLayout(layouts['app/(app)/projects/[id]'], function ProjectLayout({ project, children }) {
  const pathname = usePathname();
  const stats = useProjectStats(project.slug);
  const tabs = [
    { href: route('/projects/[id]', { id: project.slug }), label: 'Board' },
    { href: route('/projects/[id]/settings', { id: project.slug }), label: 'Members' },
  ].map((tab) => ({ ...tab, current: pathname === tab.href }));
  return (
    <div className="grid gap-6 px-8 py-6">
      <header className="grid gap-4 border-b border-ink-800 pb-4">
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <p className="key uppercase">{project.slug}</p>
            <h1 className="text-2xl font-semibold tracking-tight">{project.name}</h1>
            {project.description && <p className="mt-1 max-w-2xl text-ink-400">{project.description}</p>}
          </div>
          {stats.data && (
            <dl aria-label="Task counts" className="flex gap-5 text-sm">
              {stateOrder.map((state) => (
                <div key={state} className="grid gap-0.5">
                  <dt className="flex items-center gap-1.5 text-[12px] text-ink-400">
                    <StateDot state={state} />
                    {stateLabels[state]}
                  </dt>
                  <dd className="font-mono text-lg">{stats.data[state]}</dd>
                </div>
              ))}
            </dl>
          )}
        </div>
        <nav aria-label="Project" className="flex gap-1">
          {tabs.map((tab) => (
            <Link
              key={tab.href}
              href={tab.href}
              aria-current={tab.current ? 'page' : undefined}
              className="rounded-md px-3 py-1.5 text-sm text-ink-400 hover:bg-ink-850 hover:text-ink-100 aria-[current=page]:bg-ink-800 aria-[current=page]:text-ink-100"
            >
              {tab.label}
            </Link>
          ))}
        </nav>
      </header>
      {children}
    </div>
  );
});
