import { useMutation } from '@tanstack/react-query';
import { type FormEvent, useId } from 'react';
import { endpointUrl, MessageReceipt, useHref } from '../generated/netjes.ts';

const field = 'w-full rounded-lg border border-help-line bg-transparent px-3 py-2 font-sans text-base text-help-ink';

/**
 * A message to the help desk, sent as JSON to the endpoint MapHelpCenter maps through the mount (POST /messages).
 * endpointUrl gives its URL below the mount, and the package's useHref puts the mount and the base path in front.
 */
export function MessageForm() {
  const url = useHref(endpointUrl('POST /messages'));
  const id = useId();
  const send = useMutation({
    mutationFn: async (message: { email: string; message: string }) => {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(message),
      });
      if (!response.ok) throw new Error(`The message was not sent (${response.status}).`);
      return MessageReceipt.parse(await response.json());
    },
  });

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    send.mutate({ email: String(form.get('email') ?? ''), message: String(form.get('message') ?? '') });
  };

  if (send.data) {
    return (
      <p role="status" className="text-help-accent">
        Thanks, we have your message. Its number is {send.data.number}.
      </p>
    );
  }

  return (
    <form onSubmit={submit} className="grid gap-2" aria-labelledby={`${id}-heading`}>
      <h2 id={`${id}-heading`} className="text-base text-help-ink">
        Write to us
      </h2>
      <label className="grid gap-1 text-sm font-semibold">
        Your email
        <input name="email" type="email" required maxLength={254} className={field} />
      </label>
      <label className="grid gap-1 text-sm font-semibold">
        Message
        <textarea name="message" rows={3} required maxLength={2000} className={field} />
      </label>
      {send.error && (
        <p role="alert" className="m-0 text-sm text-[#b42318]">
          {send.error.message}
        </p>
      )}
      <button
        type="submit"
        disabled={send.isPending}
        className="cursor-pointer justify-self-start rounded-full border-0 bg-help-accent px-4 py-2 text-sm font-semibold text-help-accent-ink disabled:cursor-progress disabled:opacity-60"
      >
        Send message
      </button>
    </form>
  );
}
