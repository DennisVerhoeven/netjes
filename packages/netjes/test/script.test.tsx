import { act, render } from '@testing-library/react';
import type { ReactNode } from 'react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NonceProvider } from '../src/nonce.tsx';
import { Script, adoptServerScripts } from '../src/script.tsx';

const scripts = (src: string) => [...document.querySelectorAll(`script[src="${src}"]`)] as HTMLScriptElement[];

/** A server-rendered document around `body`. */
const page = (body: ReactNode) => (
  <html lang="en">
    <head />
    <body>{body}</body>
  </html>
);

/** The document `html` as the browser's parser builds it, without running its scripts. */
const parsed = (html: string) => new DOMParser().parseFromString(html, 'text/html');

/** Loads `html` as the browser does: the parser runs each inline script where it stands, before what follows it. */
function load(html: string) {
  document.open();
  document.write(html);
  document.close();
}

/**
 * Where inline code in these tests leaves a trace: the document object, which a page's scripts share with the test (not
 * its globals), and whose properties outlast a render in the browser, which resets the attributes of `<html>`.
 */
const traces = () => document as unknown as Record<string, unknown>;

let root: Root | undefined;

afterEach(async () => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  await act(async () => root?.unmount());
  root = undefined;
});

describe('Script', () => {
  it('loads after hydration by default, once however often it is rendered', () => {
    render(
      <>
        <Script src="/a.js" />
        <Script src="/a.js" />
      </>,
    );

    expect(scripts('/a.js')).toHaveLength(1);
    expect(scripts('/a.js')[0]?.parentElement).toBe(document.body);
  });

  it('calls onLoad when the script loaded, also for later instances', () => {
    const first = vi.fn();
    const second = vi.fn();
    render(<Script src="/b.js" onLoad={first} />);

    act(() => {
      scripts('/b.js')[0]?.dispatchEvent(new Event('load'));
    });
    render(<Script src="/b.js" onLoad={second} />);

    expect(first).toHaveBeenCalledTimes(1);
    expect(second).toHaveBeenCalledTimes(1);
  });

  it('calls onError when the script failed', () => {
    const onError = vi.fn();
    render(<Script src="/c.js" onError={onError} />);

    act(() => {
      scripts('/c.js')[0]?.dispatchEvent(new Event('error'));
    });

    expect(onError).toHaveBeenCalledTimes(1);
  });

  it('waits for the browser to be idle with lazyOnload', () => {
    vi.useFakeTimers();
    render(<Script src="/d.js" strategy="lazyOnload" />);

    expect(scripts('/d.js')).toHaveLength(0);
    act(() => {
      vi.runAllTimers();
    });

    expect(scripts('/d.js')).toHaveLength(1);
  });

  it('runs inline scripts once, keyed by id', () => {
    render(
      <>
        <Script id="analytics">{'window.__inline = (window.__inline ?? 0) + 1;'}</Script>
        <Script id="analytics">{'window.__inline = (window.__inline ?? 0) + 1;'}</Script>
      </>,
    );

    const inline = document.querySelectorAll('script#analytics');
    expect(inline).toHaveLength(1);
    expect(inline[0]?.textContent).toContain('window.__inline');
  });

  it('carries the CSP nonce, from useNonce() unless given', () => {
    render(
      <NonceProvider value="n0nce">
        <Script src="/f.js" />
        <Script id="inline-f">{'1'}</Script>
      </NonceProvider>,
    );

    expect(scripts('/f.js')[0]?.nonce).toBe('n0nce');
    expect((document.getElementById('inline-f') as HTMLScriptElement).nonce).toBe('n0nce');
  });

  it('renders nothing where it is placed', () => {
    const { container } = render(<Script src="/e.js" />);

    expect(container.innerHTML).toBe('');
  });
});

describe('Script with beforeInteractive', () => {
  beforeEach(() => {
    load('<!DOCTYPE html><html><head></head><body></body></html>');
  });

  it('renders an inline beforeInteractive script where it stands, with the nonce and the head marker', () => {
    const html = renderToString(
      page(
        <NonceProvider value="n0nce">
          <p>before</p>
          <Script id="js-class" strategy="beforeInteractive">{"document.documentElement.classList.add('js')"}</Script>
          <main>after</main>
        </NonceProvider>,
      ),
    );

    const server = parsed(html);
    expect(server.head.querySelector('meta[name="netjes-before-interactive"]')).not.toBeNull();
    const script = server.getElementById('js-class') as HTMLScriptElement;
    expect(script.parentElement).toBe(server.body);
    expect(script.previousElementSibling?.outerHTML).toBe('<p>before</p>');
    expect(script.nextElementSibling?.outerHTML).toBe('<main>after</main>');
    expect(script.nonce).toBe('n0nce');
    expect(script.hasAttribute('src')).toBe(false);
    expect(script.textContent).toBe("document.documentElement.classList.add('js')");

    // The parser runs it before it reaches what follows.
    load(html);
    expect(document.documentElement.classList.contains('js')).toBe(true);
  });

  it('escapes script tags in inline beforeInteractive code, and hydrates it', async () => {
    const code = "document.escaped = '</script><b>bold</b><script>';";
    const element = page(
      <>
        <Script id="escaped" strategy="beforeInteractive">{code}</Script>
        <p>after</p>
      </>,
    );
    const html = renderToString(element);

    load(html);
    // The code neither closed its element nor opened another, and means what it said.
    expect(document.querySelector('b')).toBeNull();
    expect(document.querySelectorAll('script')).toHaveLength(1);
    expect(traces().escaped).toBe('</script><b>bold</b><script>');
    expect(document.getElementById('escaped')?.nextElementSibling?.outerHTML).toBe('<p>after</p>');

    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    const recoverable = vi.fn();
    await act(async () => {
      root = hydrateRoot(document, element, { onRecoverableError: recoverable });
    });

    expect(recoverable).not.toHaveBeenCalled();
    expect(errors).not.toHaveBeenCalled();
    // Once hydrated, it keeps the markup it hydrated.
    expect(document.getElementById('escaped')?.nextElementSibling?.outerHTML).toBe('<p>after</p>');
    expect(document.head.querySelector('meta[name="netjes-before-interactive"]')).not.toBeNull();
  });

  it('renders an external beforeInteractive script as an inert entry where it stands, with a preload in the head', () => {
    const html = renderToString(
      page(
        <NonceProvider value="n0nce">
          <p>before</p>
          <Script src="/polyfills.js?v=</script>" strategy="beforeInteractive" crossOrigin="anonymous" integrity="sha384-abc" />
          <main>after</main>
        </NonceProvider>,
      ),
    );

    const server = parsed(html);
    // Nothing in the document runs it: no script has a src, and the entry is data.
    expect(server.querySelector('script[src]')?.outerHTML).toBeUndefined();
    const preload = server.head.querySelector('link[rel="preload"][as="script"]') as HTMLLinkElement;
    expect(preload.getAttribute('href')).toBe('/polyfills.js?v=</script>');
    expect(preload.nonce).toBe('n0nce');
    expect(preload.hasAttribute('crossorigin')).toBe(true);
    expect(preload.getAttribute('integrity')).toBe('sha384-abc');
    expect(server.head.querySelector('meta[name="netjes-before-interactive"]')).not.toBeNull();
    const [entry, ...others] = server.querySelectorAll('script');
    expect(others).toHaveLength(0);
    expect(entry?.type).toBe('application/json');
    expect(entry?.nonce).toBe('n0nce');
    expect(entry?.previousElementSibling?.outerHTML).toBe('<p>before</p>');
    expect(entry?.nextElementSibling?.outerHTML).toBe('<main>after</main>');
    // The src cannot end the element it sits in.
    expect(entry?.textContent).not.toContain('<');
    expect(JSON.parse(entry?.textContent ?? '')).toMatchObject({ src: '/polyfills.js?v=</script>' });
  });

  it('preloads an external beforeInteractive script in the CORS mode it runs in, an empty crossOrigin included', () => {
    const server = parsed(renderToString(page(<Script src="/anonymous.js" strategy="beforeInteractive" crossOrigin="" />)));

    expect(server.head.querySelector('link[rel="preload"][href="/anonymous.js"]')?.getAttribute('crossorigin')).toBe('');
  });

  it("adoptServerScripts runs external entries in order with the page's nonce and records their outcome", async () => {
    document.body.innerHTML = renderToString(
      <>
        <NonceProvider value="n0nce">
          <Script src="/first.js" strategy="beforeInteractive" crossOrigin="anonymous" data-site="blog" />
          <Script src="/second.js" strategy="beforeInteractive" />
        </NonceProvider>
        {/* Not the page's nonce: markup that did not come from the page's own render. */}
        <NonceProvider value="injected">
          <Script src="/third.js" strategy="beforeInteractive" />
        </NonceProvider>
      </>,
    );
    let settled = false;
    const adopted = adoptServerScripts('n0nce').then(() => {
      settled = true;
    });

    expect([...document.head.querySelectorAll('script')].map((script) => script.getAttribute('src'))).toEqual(['/first.js', '/second.js']);
    const [first, second] = [scripts('/first.js')[0], scripts('/second.js')[0]];
    expect(first?.async).toBe(false);
    expect(second?.async).toBe(false);
    expect(first?.nonce).toBe('n0nce');
    expect(second?.nonce).toBe('n0nce');
    expect(first?.getAttribute('crossorigin')).toBe('anonymous');
    expect(first?.getAttribute('data-site')).toBe('blog');
    expect(scripts('/third.js')).toHaveLength(0);

    // It waits until each one has loaded or failed.
    first?.dispatchEvent(new Event('load'));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(settled).toBe(false);
    second?.dispatchEvent(new Event('error'));
    await adopted;
    expect(settled).toBe(true);

    // The components report the outcome, and add neither script again.
    const onLoad = vi.fn();
    const onError = vi.fn();
    render(
      <>
        <Script src="/first.js" strategy="beforeInteractive" onLoad={onLoad} />
        <Script src="/second.js" strategy="beforeInteractive" onError={onError} />
      </>,
    );
    expect(onLoad).toHaveBeenCalledTimes(1);
    expect(onError).toHaveBeenCalledTimes(1);
    expect(scripts('/first.js')).toHaveLength(1);
    expect(scripts('/second.js')).toHaveLength(1);
  });

  it('neither preloads nor waits for a beforeInteractive script the browser does not run', async () => {
    const html = renderToString(
      page(
        <NonceProvider value="n0nce">
          <Script src="/legacy.js" strategy="beforeInteractive" noModule />
          {/* A consent manager's: it turns the type into JavaScript once the visitor agrees. */}
          <Script src="/consented.js" strategy="beforeInteractive" type="text/plain" data-category="analytics" />
          <Script src="/blank.js" strategy="beforeInteractive" type=" " />
          <Script src="/runs.js" strategy="beforeInteractive" type=" Text/JavaScript " />
          <Script src="/module.js" strategy="beforeInteractive" type="MODULE" />
        </NonceProvider>,
      ),
    );

    // Fetching it would contact a host the visitor has not agreed to.
    const server = parsed(html);
    expect([...server.head.querySelectorAll('link[rel="preload"]')].map((link) => link.getAttribute('href'))).toEqual(['/runs.js', '/module.js']);

    load(html);
    let settled = false;
    const adopted = adoptServerScripts('n0nce').then(() => {
      settled = true;
    });
    // Each is in the head, where a consent manager finds its own, but only those the browser runs are waited for.
    expect([...document.head.querySelectorAll('script')].map((script) => script.getAttribute('src'))).toEqual([
      '/legacy.js',
      '/consented.js',
      '/blank.js',
      '/runs.js',
      '/module.js',
    ]);
    expect(scripts('/legacy.js')[0]?.hasAttribute('nomodule')).toBe(true);
    expect(scripts('/consented.js')[0]?.type).toBe('text/plain');
    expect(scripts('/consented.js')[0]?.getAttribute('data-category')).toBe('analytics');

    scripts('/runs.js')[0]?.dispatchEvent(new Event('load'));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(settled).toBe(false);
    scripts('/module.js')[0]?.dispatchEvent(new Event('load'));
    await Promise.race([adopted, new Promise((resolve) => setTimeout(resolve, 50))]);
    expect(settled).toBe(true);
  });

  it("keeps an external beforeInteractive script's id, from the server's document as on a client render", () => {
    // A third-party loader that finds its own element by id.
    document.body.innerHTML = renderToString(<Script id="consent-manager" src="/cmp.js" strategy="beforeInteractive" data-settings="abc" />);
    expect(document.querySelectorAll('[id="consent-manager"]')).toHaveLength(0);

    void adoptServerScripts(undefined);

    const adopted = document.querySelectorAll<HTMLScriptElement>('[id="consent-manager"]');
    expect(adopted).toHaveLength(1);
    expect(adopted[0]?.parentElement).toBe(document.head);
    expect(adopted[0]?.getAttribute('src')).toBe('/cmp.js');
    expect(adopted[0]?.getAttribute('data-settings')).toBe('abc');

    render(<Script id="client-manager" src="/client-cmp.js" strategy="beforeInteractive" />);
    expect(document.getElementById('client-manager')?.getAttribute('src')).toBe('/client-cmp.js');
  });

  it('adoptServerScripts takes no entry without the page\'s nonce, and from an entry only a src and plain attributes', async () => {
    const entry = (json: string, nonce?: string) =>
      `<script type="application/json" data-netjes-script="${JSON.parse(json).src ?? 'x'}"${nonce ? ` nonce="${nonce}"` : ''}>${json}</script>`;
    document.body.innerHTML = [
      // Markup without the nonce, and with another one in another case.
      entry('{"src":"/unsigned.js","attributes":{}}'),
      entry('{"src":"/cased.js","attributes":{}}', 'N0NCE'),
      // Not an external script's entry.
      entry('{"src":1,"attributes":{}}', 'n0nce'),
      entry('{"attributes":{}}', 'n0nce'),
      // Event handlers, odd names, a nonce of its own and values that are not strings are left out.
      entry('{"src":"/plain.js","attributes":{"onload":"alert(1)","ONERROR":"alert(2)","a b":"c","nonce":"other","data-n":5,"data-ok":"yes","defer":true}}', 'n0nce'),
    ].join('');

    void adoptServerScripts('n0nce');

    expect([...document.querySelectorAll('script[src]')].map((script) => script.getAttribute('src'))).toEqual(['/plain.js']);
    const plain = scripts('/plain.js')[0];
    expect(plain?.getAttributeNames().filter((name) => name !== 'nonce').sort()).toEqual(['data-ok', 'defer', 'src']);
    expect(plain?.nonce).toBe('n0nce');
  });

  it('takes over an entry that arrives after the others were, as its component hydrates, and calls onLoad', async () => {
    const onLoad = vi.fn();
    // As in a Suspense boundary that streams in after the client entry took over the shell's entries.
    const element = page(<Script src="/late.js" strategy="beforeInteractive" onLoad={onLoad} />);
    load(renderToString(element));

    await act(async () => {
      root = hydrateRoot(document, element);
    });

    const [late, ...others] = scripts('/late.js');
    expect(others).toHaveLength(0);
    expect(document.querySelector('script[type="application/json"][data-netjes-script="/late.js"]')).not.toBeNull();
    expect(late?.parentElement).toBe(document.head);
    expect(late?.async).toBe(false);
    act(() => {
      late?.dispatchEvent(new Event('load'));
    });
    expect(onLoad).toHaveBeenCalledTimes(1);
  });

  it('does not add a server-rendered beforeInteractive script again after a hydration error', async () => {
    const Page = ({ text }: { text: string }) =>
      page(
        <div>
          <p>{text}</p>
          <Script id="counted" strategy="beforeInteractive">
            {'document.runs = (document.runs ?? 0) + 1;'}
          </Script>
        </div>,
      );
    load(renderToString(<Page text="from the server" />));
    await adoptServerScripts(undefined);

    vi.spyOn(console, 'error').mockImplementation(() => {});
    const recoverable = vi.fn();
    // The text differs, so React gives up on the server's DOM and renders the page in the browser.
    await act(async () => {
      root = hydrateRoot(document, <Page text="in the browser" />, { onRecoverableError: recoverable });
    });

    expect(recoverable).toHaveBeenCalled();
    expect(document.querySelector('p')?.textContent).toBe('in the browser');
    expect(traces().runs).toBe(1);
    expect(document.querySelector('body > script#counted')).toBeNull();
  });

  it('adds a beforeInteractive script once on a client render, as afterInteractive does, and calls onLoad', () => {
    const onLoad = vi.fn();
    render(
      <>
        <Script src="/client.js" strategy="beforeInteractive" onLoad={onLoad} />
        <Script src="/client.js" strategy="beforeInteractive" />
        <Script id="client-inline" strategy="beforeInteractive">
          {'document.clientRuns = (document.clientRuns ?? 0) + 1;'}
        </Script>
      </>,
    );

    expect(scripts('/client.js')).toHaveLength(1);
    act(() => {
      scripts('/client.js')[0]?.dispatchEvent(new Event('load'));
    });
    expect(onLoad).toHaveBeenCalledTimes(1);
    expect(traces().clientRuns).toBe(1);
    expect(document.querySelectorAll('meta[name="netjes-before-interactive"]')).toHaveLength(0);
  });

  it("writes inline beforeInteractive code on a client render as the server's document carries it, so one hash allows both", () => {
    const code = "document.tag = '</script>';";
    const server = parsed(renderToString(page(<Script id="tag-server" strategy="beforeInteractive">{code}</Script>)));

    render(<Script id="tag-client" strategy="beforeInteractive">{code}</Script>);

    expect(document.getElementById('tag-client')?.textContent).toBe(server.getElementById('tag-server')?.textContent);
    expect(traces().tag).toBe('</script>');
  });

  it('warns in development about attributes that would change when a beforeInteractive script runs', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    renderToString(
      <>
        <Script src="/async.js" strategy="beforeInteractive" async />
        <Script src="/async.js" strategy="beforeInteractive" async />
        <Script id="module" strategy="beforeInteractive" type="module">{'1'}</Script>
        <Script src="/plain.js" strategy="beforeInteractive" />
        <Script src="/after.js" async />
      </>,
    );

    expect(warn).toHaveBeenCalledTimes(2);
    expect(String(warn.mock.calls[0]?.[0])).toContain('/async.js');
    expect(String(warn.mock.calls[0]?.[0])).toContain('async');
    expect(String(warn.mock.calls[1]?.[0])).toContain('type="module"');
  });
});
