import { useActionState, useMemo } from 'react';
import { encodeForm } from './form-data.ts';
import { ANTIFORGERY_HEADER, type ActionResponse, type AntiforgeryToken, COOKIES_HEADER, DATA_HEADER, parseActionResponse } from './payload.ts';
import { usePagePayload } from './router.tsx';
import { type StandardSchemaV1, parseWith } from './schema.ts';

/**
 * What a server action resolves to: its result, or the validation errors (status 422) per field. An action typed with
 * a result can still answer `ServerAction.Redirect(...)` (then `result` is undefined and the router has already moved
 * on), so code that runs after `await action(...)` in a component that may stay mounted should not assume one.
 */
export type ActionState<TResult> =
  | { ok: true; result: TResult }
  | { ok: false; status: number; errors: Record<string, string[]>; values?: Record<string, string | string[]> };

/**
 * A reference to a C# server action (`app.MapAction(name, ...)`). Call it with a plain object (sent as JSON) or
 * FormData (sent as a form post); use it in a form through {@link useAction}.
 */
export interface ServerAction<TInput, TResult> {
  (input: TInput | FormData): Promise<ActionState<TResult>>;
  readonly actionName: string;
  readonly url: string;
}

/** Thrown into the nearest error boundary when the server could not run the action (not for validation errors). */
export class ServerActionError extends Error {
  override name = 'ServerActionError';
  readonly status: number;
  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

/** Runs action calls for the page: the router in the browser (fetch, then redirect/refresh/query updates), a fake in tests. */
export type ActionClient = {
  invoke(name: string, url: string, input: unknown): Promise<ActionResponse>;
  /** The antiforgery form field, left out of the values a failed call hands back. */
  tokenField?(): string | undefined;
};

let client: ActionClient | null = null;

/**
 * Set from the moment a router renders until it registers: React dispatches the clicks that came in while it hydrated
 * the page before it runs the router's effects, and runs a page's own effects before its router's. Calls made then
 * wait here.
 */
let arriving: { ready: Promise<void>; resolve: () => void } | null = null;

/** A router is mounting (called as it first renders, in the browser): calls from now on wait until it registers. */
export function expectActionClient(): void {
  if (arriving) return;
  let resolve = () => {};
  const ready = new Promise<void>((done) => {
    resolve = done;
  });
  arriving = { ready, resolve };
}

/** The router registers itself; there is one per page in the browser. */
export function setActionClient(next: ActionClient | null): () => void {
  client = next;
  if (next && arriving) {
    arriving.resolve();
    arriving = null;
  }
  return () => {
    if (client === next) client = null;
  };
}

/** How long a call waits for the router on its way; one whose render React threw away never registers. */
const READY_TIMEOUT_MS = 10_000;

/**
 * The registered client, once the router on its way has registered; the error without a router on its way, or when it
 * has not registered in time.
 */
async function readyClient(name: string): Promise<ActionClient> {
  const waitingFor = arriving;
  if (!client && waitingFor) {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timedOut = new Promise<'timeout'>((resolve) => {
      timer = setTimeout(() => resolve('timeout'), READY_TIMEOUT_MS);
    });
    // Given up on, so that later calls fail at once.
    if ((await Promise.race([waitingFor.ready, timedOut])) === 'timeout' && arriving === waitingFor) arriving = null;
    clearTimeout(timer);
  }
  if (!client) throw new Error(`netjes: server action ${name} was called before the page was ready`);
  return client;
}

export const RETURN_FIELD = '__netjes_return';
const ACTION_META = Symbol.for('netjes.action');

type ActionMeta = { url: string; name: string };

/** The action URL and name a form action carries, so `<Form>` can render a real form post on the server. */
export function actionMetaOf(action: unknown): ActionMeta | undefined {
  return typeof action === 'function' ? (action as { [ACTION_META]?: ActionMeta })[ACTION_META] : undefined;
}

/**
 * The fields of a rejected form, as the failure state's `values`: React resets a form after its action runs, so a
 * `defaultValue={state.values.name}` is what keeps what the user typed. Files, the token and Netjes's own fields are
 * left out, and so is anything outside `fields`: the server names the fields a post without JavaScript would echo
 * (those the action declares, none private).
 */
function submittedValues(
  input: unknown,
  tokenField: string | undefined,
  fields: readonly string[] | undefined,
): Record<string, string | string[]> | undefined {
  if (!(input instanceof FormData)) return undefined;
  const values: Record<string, string | string[]> = {};
  for (const [key, value] of input.entries()) {
    if (typeof value !== 'string' || key === tokenField || key.startsWith('__netjes_')) continue;
    if (fields && !fields.includes(key)) continue;
    const existing = values[key];
    values[key] = existing === undefined ? value : Array.isArray(existing) ? [...existing, value] : [existing, value];
  }
  return values;
}

export function actionRequest(input: unknown, token: AntiforgeryToken | undefined, returnUrl: string): RequestInit {
  const headers: Record<string, string> = { [DATA_HEADER]: '1', Accept: 'application/json' };
  if (token?.headerName && token.requestToken) headers[token.headerName] = token.requestToken;
  if (input instanceof FormData) {
    if (!input.has(RETURN_FIELD)) input.append(RETURN_FIELD, returnUrl);
    const encoded = encodeForm(input);
    if (encoded.contentType) headers['Content-Type'] = encoded.contentType;
    return { method: 'POST', headers, body: encoded.body, credentials: 'same-origin' };
  }
  headers['Content-Type'] = 'application/json';
  return { method: 'POST', headers, body: JSON.stringify(input ?? {}), credentials: 'same-origin' };
}

/**
 * The fetch half of a call, for the router's action client. `tokenRefused` says the server refused the call's
 * antiforgery token, so the action did not run; no other answer says that, whatever its status. `onCookiesChanged`
 * runs as soon as the server says the call set or deleted a cookie (signed someone in or out), which script cannot
 * see in the response itself, whatever the rest of the answer turns out to be.
 */
export async function fetchAction(
  name: string,
  url: string,
  init: RequestInit,
  onCookiesChanged?: () => void,
): Promise<{ response: ActionResponse; tokenRefused: boolean }> {
  const response = await fetch(url, init);
  if (response.headers.get(COOKIES_HEADER) === 'changed') onCookiesChanged?.();
  const type = response.headers.get('content-type') ?? '';
  if (!type.includes('application/json')) throw new ServerActionError(`Server action ${name} failed with status ${response.status}`, response.status);
  return { response: parseActionResponse(await response.json()), tokenRefused: response.headers.get(ANTIFORGERY_HEADER) === 'invalid' };
}

/**
 * Declares a server action by name. The result is parsed with `options.result` when given (zod, valibot, ...).
 * `npx netjes generate` writes these for every `MapAction`, with types from the C# signature.
 */
export function serverAction<TInput = FormData, TResult = unknown>(
  name: string,
  options: { result?: StandardSchemaV1<unknown, TResult> } = {},
): ServerAction<TInput, TResult> {
  const url = `/_netjes/actions/${name}`;
  async function call(input: TInput | FormData): Promise<ActionState<TResult>> {
    const active = client ?? (await readyClient(name));
    const answer = await active.invoke(name, url, input);
    if (!answer.ok && answer.status !== 422) {
      throw new ServerActionError(answer.message ?? `Server action ${name} failed with status ${answer.status}`, answer.status);
    }
    if (!answer.ok) {
      const values = submittedValues(input, active.tokenField?.(), answer.fields);
      return { ok: false, status: answer.status, errors: answer.errors ?? {}, ...(values ? { values } : {}) };
    }
    // `ServerAction.Redirect(...)` and `Ok()` send no result, even from an action whose C# type promises one.
    const result =
      options.result && answer.result !== undefined ? parseWith(options.result, answer.result, `result of ${name}`) : (answer.result as TResult);
    return { ok: true, result };
  }
  return Object.assign(call, { actionName: name, url });
}

/** Options for `useAction`. */
export type UseActionOptions<TResult> = {
  /** The state before the first submission. Default null. */
  initialState?: ActionState<TResult> | null;
  /**
   * Called with the form's data inside the action's transition, just before the call to the server: the place for
   * `useOptimistic` updates, which React keeps until the action (and the page refresh after it) finishes.
   */
  onSubmit?: (formData: FormData) => void;
};

/**
 * `useActionState` for a server action: `[state, formAction, isPending]`. Pass `formAction` to `<Form action>`;
 * `useFormStatus` works as usual, and `onSubmit` is where optimistic updates go. After a failed post without
 * JavaScript, the state starts as that failure, with the submitted values.
 */
export function useAction<TInput, TResult>(
  action: ServerAction<TInput, TResult>,
  options: UseActionOptions<TResult> = {},
): [state: ActionState<TResult> | null, formAction: (formData: FormData) => void, isPending: boolean] {
  const { actionState } = usePagePayload();
  const initial: ActionState<TResult> | null =
    actionState?.action === action.actionName
      ? { ok: false, status: 422, errors: actionState.errors, values: actionState.values }
      : (options.initialState ?? null);
  const { onSubmit } = options;
  const [state, dispatch, pending] = useActionState((_previous: ActionState<TResult> | null, formData: FormData) => {
    onSubmit?.(formData);
    return action(formData);
  }, initial);
  if (!(ACTION_META in dispatch)) {
    Object.defineProperty(dispatch, ACTION_META, { value: { url: action.url, name: action.actionName } satisfies ActionMeta });
  }
  return [state, dispatch, pending];
}

/** A page action's failure, in the shape of `useAction`'s failure state. */
export type PageActionFailure = { ok: false; status: number; errors: Record<string, string[]>; values: Record<string, string | string[]> };

/**
 * After the input of the page's action (`MapPageAction`) failed its checks, its DataAnnotations or
 * `IValidatableObject`: the errors per field and the submitted values of the fields the action declares, as the page
 * is rendered again from its loader, with and without JavaScript. Else null, also after a server action's failure,
 * which `useAction` reads.
 */
export function usePageActionState(): PageActionFailure | null {
  const { actionState, route } = usePagePayload();
  return useMemo(
    () =>
      actionState && route !== null && actionState.action === route
        ? { ok: false, status: 422, errors: actionState.errors, values: actionState.values }
        : null,
    [actionState, route],
  );
}
