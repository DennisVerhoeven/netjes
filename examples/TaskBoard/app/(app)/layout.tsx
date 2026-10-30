import { Form, Link, defineLayout, useAction, useNavigation } from 'netjes';
import { Avatar, Logo } from '../../components/brand.tsx';
import { layouts, serverActions } from '../../generated/netjes.ts';
import { useUser } from '../../lib/session.ts';

// AppShell.cs: MapLayoutLoader("app/(app)", ...).RequireAuthorization(). Loaded once per visit: moving between
// pages inside the app reuses it.
export default defineLayout(layouts['app/(app)'], function AppLayout({ projects, children }) {
  const user = useUser();
  const [, logoutAction] = useAction(serverActions['auth.logout']);
  const navigation = useNavigation();
  return (
    <div className="grid min-h-screen md:grid-cols-[15rem_minmax(0,1fr)]">
      <div
        aria-hidden="true"
        data-state={navigation.state}
        className="fixed inset-x-0 top-0 z-20 h-0.5 origin-left scale-x-0 bg-signal transition-transform duration-300 data-[state=loading]:scale-x-75 data-[state=loading]:duration-[6s] data-[state=submitting]:scale-x-75 data-[state=submitting]:duration-[6s]"
      />
      <aside className="flex flex-col gap-6 border-r border-ink-800 bg-ink-900 px-4 py-5">
        <Logo href="/projects" />
        <nav aria-label="Projects" className="grid gap-1">
          <p className="label px-2 pb-1">Projects</p>
          {projects.map((project) => (
            <Link
              key={project.slug}
              href={`/projects/${project.slug}`}
              className="flex items-center justify-between rounded-md px-2 py-1.5 text-ink-300 hover:bg-ink-800 hover:text-ink-100 aria-[current=page]:bg-ink-800 aria-[current=page]:text-ink-100"
            >
              <span className="truncate">{project.name}</span>
              <span className="key">{project.openTasks}</span>
            </Link>
          ))}
          <Link href="/projects" className="rounded-md px-2 py-1.5 text-ink-400 hover:bg-ink-800 hover:text-ink-100">
            + New project
          </Link>
        </nav>
        {user && (
          <div className="mt-auto flex items-center gap-2.5 border-t border-ink-800 pt-4">
            <Avatar name={user.name} size="md" />
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{user.name}</p>
              <p className="truncate text-[12px] text-ink-400">{user.email}</p>
            </div>
            <Form action={logoutAction}>
              <button type="submit" className="rounded-md px-2 py-1 text-[12px] text-ink-400 hover:bg-ink-800 hover:text-ink-100">
                Sign out
              </button>
            </Form>
          </div>
        )}
      </aside>
      <div className="min-w-0">{children}</div>
    </div>
  );
});
