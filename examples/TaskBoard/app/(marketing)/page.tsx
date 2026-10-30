import { Form, Link, type Metadata, useAction } from 'netjes';
import { Field, SubmitButton, errorsOf, valueOf, withTooManyAttempts } from '../../components/forms.tsx';
import { serverActions } from '../../generated/netjes.ts';
import { useUser } from '../../lib/session.ts';

export const metadata: Metadata = {
  title: { absolute: 'TaskBoard · plan the week, ship the week' },
  openGraph: { type: 'website', siteName: 'TaskBoard' },
};

const columns = [
  { name: 'Todo', color: 'bg-state-todo', cards: ['WEB-12 Pricing page copy', 'WEB-14 Fix the signup email'] },
  { name: 'In progress', color: 'bg-state-progress', cards: ['WEB-9 Launch post'] },
  { name: 'Done', color: 'bg-state-done', cards: ['WEB-3 Pick a domain', 'WEB-5 Logo'] },
];

const signIn = withTooManyAttempts(serverActions['auth.login'], 'password', 'Too many sign-in attempts. Try again in a few minutes.');

/**
 * Signs in without leaving the page: the action answers without a redirect (`stay`), and, as after any action that
 * sets or deletes a cookie, the router loads the page again, now for the signed-in user. Without JavaScript the post
 * comes back here.
 */
function SignInHere() {
  const [state, formAction] = useAction(signIn);
  return (
    <Form action={formAction} aria-label="Sign in here" className="panel grid max-w-[34rem] gap-3 p-4 sm:grid-cols-[1fr_1fr_auto] sm:items-end">
      <input type="hidden" name="stay" value="true" />
      <Field label="Email" name="email" type="email" autoComplete="email" required defaultValue={valueOf(state, 'email')} errors={errorsOf(state, 'email')} />
      <Field label="Password" name="password" type="password" autoComplete="current-password" required errors={errorsOf(state, 'password')} />
      <SubmitButton pending="Signing in…">Sign in here</SubmitButton>
    </Form>
  );
}

/** The landing page: what it is, a board drawn in CSS, and for visitors a sign-in that stays on the page. */
export default function Landing() {
  const user = useUser();
  return (
    <main className="mx-auto grid max-w-6xl gap-16 px-6 pt-16 pb-24 lg:grid-cols-[1.1fr_1fr] lg:items-center">
      <section className="grid gap-6">
        <p className="label text-signal">React pages · ASP.NET Core · Postgres</p>
        <h1 className="text-[clamp(2.4rem,1.6rem+3.4vw,4.2rem)]/[1.02] font-semibold tracking-[-0.03em]">
          Plan the week.
          <br />
          <span className="text-ink-400">Ship the week.</span>
        </h1>
        <p className="max-w-[34rem] text-lg text-ink-300">
          A small tracker for small teams: projects, a board, comments and a history of what happened. Every page is
          server-rendered by Netjes and every button is a C# server action.
        </p>
        <div className="flex flex-wrap gap-3">
          <Link href="/register" className="btn px-5 py-2.5 text-base">
            Create a free board
          </Link>
          <Link href="/login" className="btn btn-ghost px-5 py-2.5 text-base">
            Sign in
          </Link>
        </div>
        {!user && <SignInHere />}
      </section>
      <section aria-hidden="true" className="panel grid grid-cols-3 gap-3 p-4 shadow-[0_30px_80px_-30px_rgb(200_240_49/0.25)]">
        {columns.map((column) => (
          <div key={column.name} className="grid content-start gap-2">
            <p className="flex items-center gap-2 text-[12px] font-medium text-ink-300">
              <span className={`size-2 rounded-full ${column.color}`} />
              {column.name}
            </p>
            {column.cards.map((card) => (
              <div key={card} className="rounded-lg border border-ink-700 bg-ink-850 p-2.5 text-[12px] text-ink-200">
                <span className="key block">{card.split(' ')[0]}</span>
                {card.split(' ').slice(1).join(' ')}
              </div>
            ))}
          </div>
        ))}
      </section>
    </main>
  );
}
