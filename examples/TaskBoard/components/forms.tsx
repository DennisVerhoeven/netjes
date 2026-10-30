import { type ActionState, type ServerAction, ServerActionError } from 'netjes';
import type { InputHTMLAttributes, ReactNode, TextareaHTMLAttributes } from 'react';
import { useFormStatus } from 'react-dom';

/** The errors of a failed action for one field (C# keys arrive camelCased, like the field names here). */
export function errorsOf(state: ActionState<unknown> | null, field: string): string[] {
  return state && !state.ok ? (state.errors[field] ?? []) : [];
}

/** What the visitor typed into a form that failed without JavaScript, so it can be shown again. */
export function valueOf(state: ActionState<unknown> | null, field: string): string | undefined {
  if (!state || state.ok || !state.values) return undefined;
  const value = state.values[field] ?? state.values[field[0]!.toUpperCase() + field.slice(1)];
  return Array.isArray(value) ? value[0] : value;
}

type FieldProps = {
  label: string;
  name: string;
  errors?: string[];
  hint?: ReactNode;
} & (({ multiline?: false } & InputHTMLAttributes<HTMLInputElement>) | ({ multiline: true } & TextareaHTMLAttributes<HTMLTextAreaElement>));

/**
 * `action`, with a 429 (past the sign-in or sign-up limits) shown on the form as `message` on `field`, and what was typed
 * kept, instead of the error page. The page cannot read the answer's Retry-After, so `message` says roughly how long.
 */
export function withTooManyAttempts<TResult>(
  action: ServerAction<FormData, TResult>,
  field: string,
  message: string,
): ServerAction<FormData, TResult> {
  async function call(input: FormData): Promise<ActionState<TResult>> {
    try {
      return await action(input);
    } catch (error) {
      if (!(error instanceof ServerActionError) || error.status !== 429) throw error;
      const values: Record<string, string> = {};
      for (const [key, value] of input.entries()) {
        if (typeof value === 'string' && !key.startsWith('__')) values[key] = value;
      }
      return { ok: false, status: 429, errors: { [field]: [message] }, values };
    }
  }
  return Object.assign(call, { actionName: action.actionName, url: action.url });
}

/** A labelled input with its errors, wired for screen readers (aria-invalid, aria-describedby). */
export function Field({ label, name, errors = [], hint, multiline, ...input }: FieldProps) {
  const describedBy = errors.length > 0 ? `${name}-error` : hint ? `${name}-hint` : undefined;
  const common = { id: name, name, 'aria-invalid': errors.length > 0 || undefined, 'aria-describedby': describedBy, className: 'field' };
  return (
    <div className="grid gap-1.5">
      <label htmlFor={name} className="label">
        {label}
      </label>
      {multiline ? (
        <textarea rows={4} {...common} {...(input as TextareaHTMLAttributes<HTMLTextAreaElement>)} />
      ) : (
        <input {...common} {...(input as InputHTMLAttributes<HTMLInputElement>)} />
      )}
      {errors.length > 0 ? (
        <p id={`${name}-error`} className="text-[13px] text-danger">
          {errors.join(' ')}
        </p>
      ) : (
        hint && (
          <p id={`${name}-hint`} className="text-[13px] text-ink-400">
            {hint}
          </p>
        )
      )}
    </div>
  );
}

/** A submit button that knows its form is posting (useFormStatus works with Netjes server actions). */
export function SubmitButton({ children, pending, className = 'btn' }: { children: ReactNode; pending?: ReactNode; className?: string }) {
  const status = useFormStatus();
  return (
    <button type="submit" className={className} disabled={status.pending}>
      {status.pending ? (pending ?? children) : children}
    </button>
  );
}
