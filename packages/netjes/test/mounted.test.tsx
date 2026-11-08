// mounted() from netjes: the links, forms and router hooks of a page package, which put its mount's URL for the page
// on screen in front of each href, and the base path in front of that.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { type MountedRouter, mounted } from '../src/mounted.tsx';
import { createTestApp } from '../src/testing.tsx';

const NAME = '@acme/help-center';
const help = mounted(NAME);

afterEach(() => {
  vi.restoreAllMocks();
});

describe('mounted', () => {
  it('links below the mount', () => {
    render(<help.Link href="/articles/x">Article</help.Link>, { wrapper: createTestApp({ url: '/help', mounts: { [NAME]: '/help' } }).Wrapper });

    expect(screen.getByRole('link', { name: 'Article' }).getAttribute('href')).toBe('/help/articles/x');
  });

  it("posts a form and a submit button's formaction below the mount", async () => {
    const app = createTestApp({ url: '/acme/help/articles/x', mounts: { [NAME]: '/acme/help' } });
    render(
      <help.Form method="post" action="/feedback">
        <button type="submit">Send</button>
        <help.SubmitButton formAction="/feedback/delete">Delete</help.SubmitButton>
      </help.Form>,
      { wrapper: app.Wrapper },
    );

    expect(screen.getByRole('button', { name: 'Send' }).closest('form')?.getAttribute('action')).toBe('/acme/help/feedback');
    expect(screen.getByRole('button', { name: 'Delete' }).getAttribute('formaction')).toBe('/acme/help/feedback/delete');
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));

    expect(app.navigations.map((n) => (n.type === 'submit' ? new URL(n.action).pathname : n.type))).toEqual([
      '/acme/help/feedback',
      '/acme/help/feedback/delete',
    ]);
  });

  it("navigates below the mount with the router, and gives an href below it for a plain <a> or a fetch", async () => {
    function Buttons() {
      const router = help.useRouter();
      return (
        <>
          <a href={help.useHref('/feed.xml')}>Feed</a>
          <button type="button" onClick={() => void router.push('/articles')}>
            push
          </button>
          <button type="button" onClick={() => void router.replace('/contact?from=x')}>
            replace
          </button>
          <button type="button" onClick={() => router.refresh()}>
            refresh
          </button>
        </>
      );
    }
    const app = createTestApp({ url: '/help', mounts: { [NAME]: '/help' } });
    render(<Buttons />, { wrapper: app.Wrapper });

    expect(screen.getByRole('link', { name: 'Feed' }).getAttribute('href')).toBe('/help/feed.xml');
    await userEvent.click(screen.getByRole('button', { name: 'push' }));
    await userEvent.click(screen.getByRole('button', { name: 'replace' }));
    await userEvent.click(screen.getByRole('button', { name: 'refresh' }));

    expect(app.navigations).toEqual([
      { type: 'push', href: '/help/articles' },
      { type: 'replace', href: '/help/contact?from=x' },
      { type: 'refresh' },
    ]);
  });

  it.each([
    ['/help/articles/x', '/help', '/articles/x'],
    ['/help', '/help', '/'],
    ['/help/', '/help', '/'],
    ['/HELP/Articles', '/help', '/Articles'],
    ['/acme%20co/help/x', '/acme%20co/help', '/x'],
    ["/a'b/help/x", '/a%27b/help', '/x'],
    ['/%C3%BC/help/x', '/%c3%bc/help', '/x'],
    // The server leaves %2F in a value as it is and encodes it again (%252F); both URLs have the same value.
    ['/a%2Fb/help/x', '/a%252Fb/help', '/x'],
    ['/a%252Fb/help/x', '/a%252Fb/help', '/x'],
    ['/a/b/help/x', '/a%252Fb/help', '/a/b/help/x'],
    ['/articles', '/', '/articles'],
    ['/helpx/a', '/help', '/helpx/a'],
    ['/about', '/help', '/about'],
    ['/about', null, '/about'],
  ])('gives the path of %s below the mount %s as %s', (url, mount, expected) => {
    function Path() {
      return <p>{help.usePathname()}</p>;
    }
    render(<Path />, { wrapper: createTestApp({ url, mounts: { [NAME]: mount } }).Wrapper });

    expect(screen.getByText(expected)).toBeDefined();
  });

  // (security) The server encodes a value as one segment, and the link keeps it so: below its own tenant.
  it('keeps an encoded slash, question mark, hash or percent in a value of the mount', () => {
    for (const mount of ['/a%2Fb/help', '/a%3Fb/help', '/a%23b/help', '/100%25/help']) {
      const { unmount } = render(<help.Link href="/x">x</help.Link>, { wrapper: createTestApp({ url: `${mount}/x`, mounts: { [NAME]: mount } }).Wrapper });

      expect(screen.getByRole('link', { name: 'x' }).getAttribute('href')).toBe(`${mount}/x`);
      expect(screen.getByRole('link', { name: 'x' }).getAttribute('aria-current')).toBe('page');
      unmount();
    }
  });

  it("passes a link within the page on screen through, as a list's ?page=2 or a #top", () => {
    render(
      <>
        <help.Link href="?page=2">Next</help.Link>
        <help.Link href="#top">Top</help.Link>
        <help.Link href="/?page=2">First</help.Link>
      </>,
      { wrapper: createTestApp({ url: '/help/articles', mounts: { [NAME]: '/help' } }).Wrapper },
    );

    expect(screen.getByRole('link', { name: 'Next' }).getAttribute('href')).toBe('?page=2');
    expect(screen.getByRole('link', { name: 'Top' }).getAttribute('href')).toBe('#top');
    expect(screen.getByRole('link', { name: 'First' }).getAttribute('href')).toBe('/help?page=2');
  });

  it('leaves hrefs as they are for a package mounted at the root', () => {
    render(<help.Link href="/articles">Articles</help.Link>, { wrapper: createTestApp({ mounts: { [NAME]: '/' } }).Wrapper });

    expect(screen.getByRole('link', { name: 'Articles' }).getAttribute('href')).toBe('/articles');
  });

  it('throws on a page that has no value for the prefix, and in an app that did not mount the package', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const link = <help.Link href="/articles">Articles</help.Link>;

    expect(() => render(link, { wrapper: createTestApp({ mounts: { [NAME]: null } }).Wrapper })).toThrow(
      `netjes: '${NAME}' is mounted at a prefix with a value, and this page has none for it`,
    );
    expect(() => render(link, { wrapper: createTestApp({ mounts: {} }).Wrapper })).toThrow(`netjes: the page package '${NAME}' is not mounted`);
    expect(() => render(link, { wrapper: createTestApp().Wrapper })).toThrow(`netjes: the page package '${NAME}' is not mounted`);
  });

  // (security) A mounted href is a path below the mount. One that names another host or a scheme, or climbs out of the
  // mount with a dot segment (also encoded, or after a backslash, which browsers read as a slash), is refused rather
  // than put below the mount, where a mount at the root would leave it as it is.
  const refused = [
    '//evil.example',
    '/\\evil.example',
    'https://evil.example',
    'HTTPS://evil.example',
    'javascript:alert(1)',
    'JavaScript:alert(1)',
    'mailto:a@b.example',
    'data:text/html,x',
    'articles',
    '',
    ' /articles',
    '/\t/evil.example',
    '/articles\n',
    '/../admin',
    '/a/../../admin',
    '/%2e%2e/admin',
    '/%2E%2e/admin',
    '/.%2e/admin',
    '/./admin',
    '/a\\..\\..\\admin',
    '/..?x',
    '?page=\n2',
    '#top\t',
  ];

  describe.each([['/help'], ['/']])('mounted at %s', (mount) => {
    it.each(refused.map((href) => [href]))('refuses the href %j in every component and hook', (href) => {
      vi.spyOn(console, 'error').mockImplementation(() => {});
      const app = createTestApp({ url: '/help', mounts: { [NAME]: mount } });
      const message = `netjes: '${href}' is no link below the mount of '${NAME}'`;
      let router: MountedRouter | undefined;
      const Router = () => {
        router = help.useRouter();
        return null;
      };
      const Href = () => <a href={help.useHref(href)}>x</a>;

      expect(() => render(<help.Link href={href}>x</help.Link>, { wrapper: app.Wrapper })).toThrow(message);
      expect(() => render(<help.Form action={href} />, { wrapper: app.Wrapper })).toThrow(message);
      expect(() => render(<help.SubmitButton formAction={href} />, { wrapper: app.Wrapper })).toThrow(message);
      expect(() => render(<Href />, { wrapper: app.Wrapper })).toThrow(message);
      render(<Router />, { wrapper: app.Wrapper });
      expect(() => router?.push(href)).toThrow(message);
      expect(() => router?.replace(href)).toThrow(message);
      expect(() => router?.prefetch(href)).toThrow(message);
      expect(app.navigations).toEqual([]);
    });
  });
});
