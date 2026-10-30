import { Link, type LayoutProps } from 'netjes';
import { Logo } from '../../components/brand.tsx';
import { useUser } from '../../lib/session.ts';

/** The public pages: landing, sign in, sign up. */
export default function MarketingLayout({ children }: LayoutProps) {
  const user = useUser();
  return (
    <div className="min-h-screen bg-[radial-gradient(60rem_30rem_at_70%_-10%,rgb(200_240_49/0.12),transparent)]">
      <header className="mx-auto flex max-w-6xl items-center justify-between px-6 py-5">
        <Logo />
        <nav aria-label="Account" className="flex items-center gap-2">
          {user ? (
            <>
              <span className="text-ink-300">{user.name}</span>
              <Link href="/projects" className="btn">
                Open your board
              </Link>
            </>
          ) : (
            <>
              <Link href="/login" className="btn btn-ghost">
                Sign in
              </Link>
              <Link href="/register" className="btn">
                Get started
              </Link>
            </>
          )}
        </nav>
      </header>
      {children}
    </div>
  );
}
