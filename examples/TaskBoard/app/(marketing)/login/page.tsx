import { Form, Link, type Metadata, definePage, useAction } from 'netjes';
import { Field, SubmitButton, errorsOf, valueOf, withTooManyAttempts } from '../../../components/forms.tsx';
import { pages, route, serverActions } from '../../../generated/netjes.ts';

export const metadata: Metadata = { title: 'Sign in' };

const signIn = withTooManyAttempts(serverActions['auth.login'], 'password', 'Too many sign-in attempts. Try again in a few minutes.');

/**
 * Sign in. A server action; without JavaScript the same form posts, and a wrong password comes back here. Past the
 * sign-in limits (429) the form says so; without JavaScript that is the error page.
 */
export default definePage(pages['/login'], function Login({ returnUrl }) {
  const [state, formAction] = useAction(signIn);
  const registerHref = route('/register', {}, { returnUrl });
  return (
    <main className="mx-auto grid max-w-sm gap-6 px-6 pt-16 pb-24">
      <div className="grid gap-1">
        <h1 className="text-2xl font-semibold tracking-tight">Welcome back</h1>
        <p className="text-ink-400">Sign in to your boards.</p>
      </div>
      <Form action={formAction} className="panel grid gap-4 p-5">
        {returnUrl && <input type="hidden" name="returnUrl" value={returnUrl} />}
        <Field label="Email" name="email" type="email" autoComplete="email" required defaultValue={valueOf(state, 'email')} errors={errorsOf(state, 'email')} />
        <Field label="Password" name="password" type="password" autoComplete="current-password" required errors={errorsOf(state, 'password')} />
        <SubmitButton pending="Signing in…">Sign in</SubmitButton>
      </Form>
      <p className="text-center text-ink-400">
        New here?{' '}
        <Link href={registerHref} className="text-signal hover:underline">
          Create an account
        </Link>
      </p>
    </main>
  );
});
