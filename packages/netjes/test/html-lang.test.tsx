// <html lang> across client navigations: the page loader's lang, else the innermost layout loader's, else the locale,
// else the lang the app's root gave the document (PageMetadata.Lang, documentLang in src/lang.ts).
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { documentLang } from '../src/lang.ts';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload, SegmentRef } from '../src/payload.ts';
import { RouterProvider } from '../src/router.tsx';

const origin = window.location.origin;
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function Layout({ children }: { children: ReactNode }) {
  return (
    <>
      <nav>
        <Link href="/">Home</Link>
        <Link href="/nl">Dutch</Link>
        <Link href="/team">Team</Link>
        <Link href="/team/settings">Settings</Link>
        <Link href="/team/german">German</Link>
        <Link href="/other">Other</Link>
      </nav>
      {children}
    </>
  );
}

function Page({ title }: { title?: unknown }) {
  return <h1>{String(title)}</h1>;
}

const modules = {
  '/app/layout.tsx': async () => ({ default: Layout }),
  '/app/page.tsx': async () => ({ default: Page }),
  '/app/team/layout.tsx': async () => ({ default: ({ children }: { children: ReactNode }) => <section>{children}</section> }),
  '/app/team/page.tsx': async () => ({ default: Page }),
};

const root: SegmentRef = { id: 'app', layout: 'app/layout.tsx' };
const team = (segment: Partial<SegmentRef> = {}): SegmentRef => ({ id: 'app/team', layout: 'app/team/layout.tsx', props: {}, key: 'team', ...segment });

function payload(url: string, title: string, extra: Partial<PagePayload> = {}): PagePayload {
  const inTeam = url.startsWith('/team');
  return {
    kind: 'page',
    route: url,
    page: inTeam ? 'app/team/page.tsx' : 'app/page.tsx',
    segments: inTeam ? [root, team()] : [root],
    props: { title },
    params: {},
    shared: null,
    status: 200,
    url,
    version: 'v1',
    ...extra,
  };
}

const serve = (...pages: PagePayload[]) => server.use(...pages.map((page) => http.get(`${origin}${page.url}`, () => HttpResponse.json(page))));

/** The router on a document the server rendered for `initial`, whose root gave the document `rootLang`. */
async function renderApp(initial: PagePayload, rootLang = 'en') {
  window.history.replaceState(null, '', initial.url);
  // What the server wrote on <html> for this page (Document's lang).
  document.documentElement.lang = documentLang(initial, rootLang);
  const loader = createModuleLoader(modules);
  const initialModules = await loader.loadRoute(initial);
  await act(async () => {
    render(<RouterProvider initial={initial} initialModules={initialModules} loader={loader} errorPage={null} documentLang={rootLang} />);
  });
  return userEvent.setup();
}

const lang = () => document.documentElement.lang;

async function go(user: ReturnType<typeof userEvent.setup>, link: string, heading: string) {
  await user.click(screen.getByRole('link', { name: link }));
  await screen.findByRole('heading', { name: heading });
}

describe('<html lang> on a client navigation', () => {
  it("takes the next page's lang, and goes back to the document's on a page without one", async () => {
    serve(payload('/nl', 'Welkom', { lang: 'nl' }), payload('/', 'Home'));
    const user = await renderApp(payload('/', 'Home'));
    expect(lang()).toBe('en');

    await go(user, 'Dutch', 'Welkom');
    expect(lang()).toBe('nl');

    await go(user, 'Home', 'Home');
    expect(lang()).toBe('en');
  });

  it('follows Back and Forward', async () => {
    serve(payload('/nl', 'Welkom', { lang: 'nl' }));
    const user = await renderApp(payload('/', 'Home'));
    await go(user, 'Dutch', 'Welkom');

    act(() => window.history.back());
    await screen.findByRole('heading', { name: 'Home' });
    expect(lang()).toBe('en');

    act(() => window.history.forward());
    await screen.findByRole('heading', { name: 'Welkom' });
    expect(lang()).toBe('nl');
  });

  it("takes a layout's lang for the pages below it, keeps it while the layout is reused, and lets a page's win", async () => {
    serve(
      payload('/team/settings', 'Instellingen', { segments: [root, team({ props: undefined, reused: true })] }),
      payload('/team/german', 'Einstellungen', { lang: 'de', segments: [root, team({ props: undefined, reused: true })] }),
      payload('/other', 'Other'),
    );
    const user = await renderApp(payload('/team', 'Team', { segments: [root, team({ lang: 'nl' })] }));
    expect(lang()).toBe('nl');

    await go(user, 'Settings', 'Instellingen');
    expect(lang()).toBe('nl');

    await go(user, 'German', 'Einstellungen');
    expect(lang()).toBe('de');

    await go(user, 'Other', 'Other');
    expect(lang()).toBe('en');
  });

  it('takes the inner layout\'s lang over an outer one\'s', async () => {
    const user = await renderApp(payload('/', 'Home', { lang: 'nl' }));
    serve(payload('/team', 'Team', { segments: [{ ...root, props: {}, lang: 'fr' }, team({ lang: 'de' })] }));

    await go(user, 'Team', 'Team');

    expect(lang()).toBe('de');
  });

  it('follows the locale of the page it goes to, as a fresh load of its URL would', async () => {
    serve(payload('/nl', 'Welkom', { locale: 'nl' }));
    const user = await renderApp(payload('/', 'Home', { locale: 'en' }));

    await go(user, 'Dutch', 'Welkom');

    expect(lang()).toBe('nl');
  });

  it('leaves a lang the app set itself while no page or layout gives one', async () => {
    serve(payload('/other', 'Other'));
    const user = await renderApp(payload('/', 'Home'));
    document.documentElement.lang = 'de';

    await go(user, 'Other', 'Other');

    expect(lang()).toBe('de');
  });
});
