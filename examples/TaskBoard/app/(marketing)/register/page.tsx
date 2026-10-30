import { Form, Link, type Metadata, definePage, useAction } from 'netjes';
import { Field, SubmitButton, errorsOf, valueOf, withTooManyAttempts } from '../../../components/forms.tsx';
import { pages, serverActions } from '../../../generated/netjes.ts';

export const metadata: Metadata = { title: 'Create an account' };

const signUp = withTooManyAttempts(serverActions['auth.register'], 'password', 'Too many sign-ups from your network. Try again later.');

/** Sign up; the C# action validates with DataAnnotations and signs the new user in. */
export default definePage(pages['/register'], function Register({ returnUrl }) {
  const [state, formAction] = useAction(signUp);
  return (
    <main className="mx-auto grid max-w-sm gap-6 px-6 pt-16 pb-24">
      <div className="grid gap-1">
        <h1 className="text-2xl font-semibold tracking-tight">Create your board</h1>
        <p className="text-ink-400">Free, and it takes ten seconds.</p>
      </div>
      <Form action={formAction} className="panel grid gap-4 p-5">
        <Field label="Name" name="name" autoComplete="name" required defaultValue={valueOf(state, 'name')} errors={errorsOf(state, 'name')} />
        <Field label="Email" name="email" type="email" autoComplete="email" required defaultValue={valueOf(state, 'email')} errors={errorsOf(state, 'email')} />
        <Field
          label="Password"
          name="password"
          type="password"
          autoComplete="new-password"
          required
          minLength={10}
          errors={errorsOf(state, 'password')}
          hint="At least 10 characters."
        />
        <SubmitButton pending="Creating…">Create account</SubmitButton>
      </Form>
      <p className="text-center text-ink-400">
        Have an account?{' '}
        <Link href={returnUrl ? `/login?returnUrl=${encodeURIComponent(returnUrl)}` : '/login'} className="text-signal hover:underline">
          Sign in
        </Link>
      </p>
    </main>
  );
});
