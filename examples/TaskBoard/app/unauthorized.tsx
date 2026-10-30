import { Link, type Metadata, usePathname, useRouter, useSearchParams } from 'netjes';
import { useEffect } from 'react';
import { Logo } from '../components/brand.tsx';
import { route } from '../generated/netjes.ts';

export const metadata: Metadata = { title: 'Sign in' };

/**
 * Status 401: the cookie is missing or expired. With JavaScript the visitor goes straight to the login page, which
 * brings them back here afterwards; without it, they get the link.
 */
export default function Unauthorized() {
  const pathname = usePathname();
  const search = useSearchParams();
  const router = useRouter();
  const target = route('/login', {}, { returnUrl: pathname + (search.size ? `?${search}` : '') });

  useEffect(() => {
    void router.replace(target);
  }, [router, target]);

  return (
    <main className="grid min-h-screen place-items-center px-6">
      <div className="grid max-w-sm gap-5 text-center">
        <div className="mx-auto">
          <Logo />
        </div>
        <h1 className="text-xl font-semibold">Sign in to continue</h1>
        <p className="text-ink-300">Your session has ended, or this page needs an account.</p>
        <Link href={target} className="btn mx-auto">
          Sign in
        </Link>
      </div>
    </main>
  );
}
