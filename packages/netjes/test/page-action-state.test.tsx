import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { renderToString } from 'react-dom/server';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { usePageActionState } from '../src/actions.tsx';
import { Form } from '../src/form.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

/** A page whose form posts to its page action, and shows what a failed post handed back. */
function Guestbook(props: Record<string, unknown>) {
  const failed = usePageActionState();
  const entries = (props.entries as string[] | undefined) ?? [];
  return (
    <main>
      <Form method="post">
        <label>
          Name <input name="name" defaultValue={String(failed?.values.name ?? '')} />
        </label>
        {failed?.errors.name && <p role="alert">{failed.errors.name[0]}</p>}
        <label>
          Message <textarea name="message" defaultValue={String(failed?.values.message ?? '')} />
        </label>
        <button type="submit">Sign</button>
      </Form>
      <ul aria-label="Entries">{entries.map((entry) => <li key={entry}>{entry}</li>)}</ul>
      <output>{failed ? `failed ${failed.status}` : 'none'}</output>
    </main>
  );
}

const modules = { '/app/guestbook/page.tsx': async () => ({ default: Guestbook }) };

const antiforgery = { formFieldName: '__RequestVerificationToken', headerName: 'RequestVerificationToken', requestToken: 'tok' };

function payload(overrides: Partial<PagePayload> = {}): PagePayload {
  return {
    kind: 'page',
    route: '/guestbook',
    page: 'app/guestbook/page.tsx',
    segments: [],
    props: { entries: ['First!'] },
    params: {},
    shared: null,
    status: 200,
    url: '/guestbook',
    version: 'v1',
    antiforgery,
    ...overrides,
  };
}

/** What the server sends after a page action's input failed its checks: the page from its loader, with the failure. */
const failedPost = payload({
  status: 422,
  props: { entries: ['First!', 'Second'] },
  actionState: { action: '/guestbook', errors: { name: ['Tell us who you are.'] }, values: { name: '', message: 'Hi' } },
});

async function renderApp(initial: PagePayload) {
  window.history.replaceState(null, '', initial.url);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  await act(async () => {
    render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} />);
  });
  return userEvent.setup();
}

describe('usePageActionState', () => {
  it("is null until the page action's input fails its checks", async () => {
    await renderApp(payload());

    expect(screen.getByRole('status').textContent).toBe('none');
  });

  it('shows the errors and the values after a post with JavaScript, on the page its loader rendered again', async () => {
    let posted: URLSearchParams | undefined;
    server.use(
      http.post(`${origin}/guestbook`, async ({ request }) => {
        posted = new URLSearchParams(await request.text());
        return HttpResponse.json(failedPost, { status: 422 });
      }),
    );
    const user = await renderApp(payload());

    await user.type(screen.getByLabelText('Message'), 'Hi');
    await user.click(screen.getByRole('button', { name: 'Sign' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Tell us who you are.');
    expect(screen.getByRole('status').textContent).toBe('failed 422');
    expect(screen.getByLabelText('Message')).toHaveProperty('value', 'Hi');
    expect(screen.getAllByRole('listitem').map((item) => item.textContent)).toEqual(['First!', 'Second']);
    expect(posted?.get('message')).toBe('Hi');
  });

  it('starts from the failure a post without JavaScript left in the page', async () => {
    await renderApp(failedPost);

    expect(screen.getByRole('alert').textContent).toBe('Tell us who you are.');
    expect(screen.getByLabelText('Message')).toHaveProperty('value', 'Hi');
  });

  it('is in the server render, so the page shows the errors before any script runs', async () => {
    const loader = createModuleLoader(modules);
    const initialModules = await loader.loadRoute(failedPost);

    const html = renderToString(<RouterProvider initial={failedPost} initialModules={initialModules} loader={loader} errorPage={null} />);

    expect(html).toContain('Tell us who you are.');
    expect(html).toContain('>Hi</textarea>');
  });

  it("leaves a server action's failure to useAction", async () => {
    await renderApp(
      payload({ status: 422, actionState: { action: 'guestbook.sign', errors: { name: ['Tell us your name.'] }, values: { name: 'Ad' } } }),
    );

    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByRole('status').textContent).toBe('none');
  });
});
