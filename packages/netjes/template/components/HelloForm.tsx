import { Form, useAction } from 'netjes';
import { serverActions } from '../generated/netjes.ts';

/**
 * A form posting to a C# server action (Features/Home/HomePage.cs). It shows validation errors from C#, and works
 * without JavaScript too: then the page is rendered again with the errors.
 */
export function HelloForm() {
  const [state, formAction, pending] = useAction(serverActions['home.greet']);
  const error = state?.ok === false ? state.errors['name']?.[0] : undefined;
  return (
    <Form action={formAction} className="space-y-3">
      <label className="block space-y-1">
        <span className="font-medium">Your name</span>
        <input
          name="name"
          defaultValue={state?.ok === false ? String(state.values?.['name'] ?? '') : ''}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? 'name-error' : undefined}
          className="block w-full rounded-lg border border-slate-300 px-3 py-2 aria-invalid:border-red-600"
        />
      </label>
      {error && (
        <p id="name-error" className="text-sm text-red-700">
          {error}
        </p>
      )}
      <button type="submit" disabled={pending} className="cursor-pointer rounded-full bg-slate-900 px-4 py-2 font-medium text-white disabled:opacity-60">
        {pending ? 'Saying hello…' : 'Say hello'}
      </button>
      {state?.ok && <p role="status">{state.result.text}</p>}
    </Form>
  );
}
