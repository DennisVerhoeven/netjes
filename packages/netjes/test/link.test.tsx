import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Link } from '../src/link.tsx';
import { createTestApp } from '../src/testing.tsx';

/** Renders a link to each href on the page at `url`, and reads each one's aria-current. */
function currentAt(url: string, hrefs: string[]) {
  const { unmount } = render(
    <nav>
      {hrefs.map((href) => (
        <Link key={href} href={href}>
          {href}
        </Link>
      ))}
    </nav>,
    { wrapper: createTestApp({ url }).Wrapper },
  );
  const current = Object.fromEntries(hrefs.map((href) => [href, screen.getByRole('link', { name: href }).getAttribute('aria-current')]));
  unmount();
  return current;
}

describe('Link', () => {
  it('does not mark a link to a place in the current page as the current page', () => {
    expect(currentAt('/', ['/', '/#features', '#features', '#', '/#'])).toEqual({
      '/': 'page',
      '/#features': null,
      '#features': null,
      '#': null,
      '/#': null,
    });
  });

  it('marks a link with a query only when the page has the same values for the parameters it names', () => {
    expect(currentAt('/search?q=cache', ['/search?q=other', '/search?q=cache', '?q=cache'])).toEqual({
      '/search?q=other': null,
      '/search?q=cache': 'page',
      '?q=cache': 'page',
    });
  });

  it('ignores parameters the link does not name', () => {
    expect(currentAt('/blog?tag=react&utm_source=nl', ['/blog?tag=react', '/blog', '/blog?tag=vue'])).toEqual({
      '/blog?tag=react': 'page',
      '/blog': 'page',
      '/blog?tag=vue': null,
    });
  });

  it('compares decoded values, in any order, with repeated keys', () => {
    expect(currentAt('/s?a=1&b=2', ['/s?b=2&a=1'])).toEqual({ '/s?b=2&a=1': 'page' });
    expect(currentAt('/s?t=x&t=y', ['/s?t=y&t=x', '/s?t=x'])).toEqual({ '/s?t=y&t=x': 'page', '/s?t=x': null });
    expect(currentAt('/s?q=a+b', ['/s?q=a%20b'])).toEqual({ '/s?q=a%20b': 'page' });
  });

  it('treats one trailing slash as the same page', () => {
    expect(currentAt('/docs/', ['/docs'])).toEqual({ '/docs': 'page' });
    expect(currentAt('/docs', ['/docs/'])).toEqual({ '/docs/': 'page' });
    expect(currentAt('/', ['/', '/docs', '/docs/'])).toEqual({ '/': 'page', '/docs': null, '/docs/': null });
  });

  it('lets an aria-current prop win', () => {
    render(
      <>
        <Link href="/docs/intro" aria-current={undefined}>
          Intro
        </Link>
        <Link href="/docs" aria-current="true">
          Docs
        </Link>
      </>,
      { wrapper: createTestApp({ url: '/docs/intro' }).Wrapper },
    );

    expect(screen.getByRole('link', { name: 'Intro' }).getAttribute('aria-current')).toBeNull();
    expect(screen.getByRole('link', { name: 'Docs' }).getAttribute('aria-current')).toBe('true');
  });
});
