import type { ButtonHTMLAttributes, FormHTMLAttributes, SubmitEvent } from 'react';
import { RETURN_FIELD, actionMetaOf } from './actions.tsx';
import { encodeForm } from './form-data.ts';
import type { Href } from './href.ts';
import { useBasePath, useInternalRouter, usePagePayload, withBasePath } from './router.tsx';

/** A React form action, e.g. the `formAction` from `useAction(serverAction('name'))`. */
export type FormAction = (formData: FormData) => void | Promise<void>;

/** The props of `<Form>`: a form's own, with client navigation and server action support. */
export type FormProps = Omit<FormHTMLAttributes<HTMLFormElement>, 'method' | 'action'> & {
  method?: 'get' | 'post';
  /**
   * Where to submit. Default: the current URL, which is where `MapPageAction` handlers for this page listen. Or a
   * form action function: a server action from `useAction` (a real form post without JavaScript), or any function.
   */
  action?: string | FormAction;
  replace?: boolean;
  /**
   * Let the browser submit it the classic way, with a full page load: for responses that are not pages, such as
   * large file downloads, which the browser then streams to disk instead of Netjes buffering them.
   */
  reloadDocument?: boolean;
};

function formDataOf(form: HTMLFormElement, submitter: HTMLElement | null): FormData {
  try {
    return new FormData(form, submitter);
  } catch {
    const data = new FormData(form);
    if (submitter instanceof HTMLButtonElement && submitter.name) data.append(submitter.name, submitter.value);
    return data;
  }
}

/**
 * A `<form>` that submits without a full page load and handles the page action's result like a navigation:
 * a redirect is followed, re-rendered props replace the page. Without JavaScript it is a normal form post.
 * POST forms include the antiforgery token when the server has antiforgery enabled.
 *
 * With a function `action` React runs it (so `useFormStatus` works). For server actions the server renders a real
 * `action`/`method`, the token and the return URL, so the same form also posts without JavaScript.
 */
export function Form({ method = 'get', action, replace = false, reloadDocument = false, onSubmit, children, ...rest }: FormProps) {
  const router = useInternalRouter();
  const payload = usePagePayload();
  const basePath = useBasePath();
  const isFunction = typeof action === 'function';
  const serverAction = actionMetaOf(action);
  const actionUrl = typeof action === 'string' ? withBasePath(action, basePath) : undefined;
  const token = method === 'post' || serverAction ? payload.antiforgery : undefined;
  const hidden = (
    <>
      {token?.requestToken && <input type="hidden" name={token.formFieldName} value={token.requestToken} />}
      {serverAction && <input type="hidden" name={RETURN_FIELD} value={payload.url} />}
    </>
  );

  if (isFunction) {
    // React renders function actions as an inert placeholder on the server; a server action gets its real URL
    // there instead. React keeps the server's attributes when hydrating a function action.
    const onServer = typeof document === 'undefined';
    return (
      <form
        {...rest}
        action={onServer && serverAction ? withBasePath(serverAction.url, basePath) : action}
        method={onServer && serverAction ? 'post' : undefined}
        onSubmit={onSubmit}
      >
        {hidden}
        {children}
      </form>
    );
  }

  function handleSubmit(event: SubmitEvent<HTMLFormElement>) {
    onSubmit?.(event);
    if (event.defaultPrevented || reloadDocument) return;
    const form = event.currentTarget;
    const submitter = event.submitter;
    const target = submitter?.getAttribute('formtarget') ?? form.getAttribute('target');
    if (target && target !== '_self') return;
    const formAction = submitter?.getAttribute('formaction');
    const url = new URL(formAction ? withBasePath(formAction, basePath) : (actionUrl ?? window.location.href), window.location.href);
    if (url.origin !== window.location.origin) return;
    event.preventDefault();

    const data = formDataOf(form, submitter);
    const submitMethod = (submitter?.getAttribute('formmethod') ?? method).toLowerCase();
    if (submitMethod === 'post') {
      void router.submit(url, encodeForm(data, form.enctype === 'multipart/form-data'));
      return;
    }
    const query = new URLSearchParams();
    for (const [name, value] of data.entries()) if (typeof value === 'string') query.append(name, value);
    url.search = query.toString();
    url.hash = '';
    // Built from the form's own action, which is not one of the app's typed routes.
    const href = url.href as Href;
    void (replace ? router.replace(href) : router.push(href));
  }

  return (
    <form method={method} action={actionUrl} {...rest} onSubmit={handleSubmit}>
      {hidden}
      {children}
    </form>
  );
}

/** The props of `<SubmitButton>`: a button's own, except `type`. */
export type SubmitButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'type'>;

/**
 * A submit button whose `formAction` is rendered under the base path, so it posts to the right place with and
 * without JavaScript (a plain `<button formAction="/x">` would miss the base without JavaScript).
 */
export function SubmitButton({ formAction, ...rest }: SubmitButtonProps) {
  const basePath = useBasePath();
  const action = typeof formAction === 'string' ? withBasePath(formAction, basePath) : formAction;
  return <button type="submit" formAction={action} {...rest} />;
}
