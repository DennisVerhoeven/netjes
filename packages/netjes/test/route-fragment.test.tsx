import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { buildHref } from '../src/href.ts';
import { Link } from '../src/link.tsx';
import { createModuleLoader } from '../src/modules.ts';
import type { PagePayload } from '../src/payload.ts';
import { RouterProvider } from '../src/router.tsx';

// Element ids with characters that buildHref encodes in a fragment: a space, a non-ASCII letter, and '/', '?' and ':'.
const ids = ['keys', 'a b', 'über uns', 'a/b?c:d'];

function Guide() {
  return (
    <main>
      <nav>
        {ids.map((id) => (
          <Link key={id} href={buildHref('/guide', {}, undefined, { hash: id })}>
            {`to ${id}`}
          </Link>
        ))}
      </nav>
      {ids.map((id) => (
        <h2 key={id} id={id}>
          {id}
        </h2>
      ))}
    </main>
  );
}

const initial: PagePayload = {
  kind: 'page',
  route: '/guide',
  page: 'app/guide/page.tsx',
  segments: [],
  props: {},
  params: {},
  shared: {},
  status: 200,
  url: '/guide',
  version: 'v1',
};

let scrolledTo: Element[] = [];

beforeEach(() => {
  scrolledTo = [];
  // jsdom does no layout and leaves scrollIntoView out.
  Object.defineProperty(Element.prototype, 'scrollIntoView', {
    configurable: true,
    value(this: Element) {
      scrolledTo.push(this);
    },
  });
});

afterEach(() => {
  Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
});

describe('a link to a fragment built by buildHref', () => {
  it('scrolls to the element the fragment names, also when its id has characters the fragment encodes', async () => {
    window.history.replaceState(null, '', '/guide');
    const loader = createModuleLoader({ '/app/guide/page.tsx': async () => ({ default: Guide }) }, { nameFailedImports: true });
    const initialModules = await loader.loadRoute(initial);
    render(
      <RouterProvider
        initial={initial}
        initialModules={initialModules}
        loader={loader}
        errorPage={null}
        hardNavigate={vi.fn()}
        showDocument={vi.fn()}
        download={vi.fn()}
      />,
    );
    const user = userEvent.setup();

    for (const id of ids) {
      await user.click(screen.getByRole('link', { name: `to ${id}` }));

      expect(window.location.pathname + window.location.hash, id).toBe(`/guide#${encodeURIComponent(id)}`);
      expect(scrolledTo.at(-1), id).toBe(document.getElementById(id));
    }
    expect(scrolledTo).toHaveLength(ids.length);
  });
});
