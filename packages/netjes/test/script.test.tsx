import { act, render } from '@testing-library/react';
import { renderToString } from 'react-dom/server';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { NonceProvider } from '../src/nonce.tsx';
import { Script } from '../src/script.tsx';

const scripts = (src: string) => [...document.querySelectorAll(`script[src="${src}"]`)] as HTMLScriptElement[];

afterEach(() => {
  vi.useRealTimers();
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

  it('puts beforeInteractive scripts in the server-rendered head', () => {
    const html = renderToString(
      <html lang="en">
        <head />
        <body>
          <Script src="/polyfills.js" strategy="beforeInteractive" />
        </body>
      </html>,
    );

    const head = html.slice(html.indexOf('<head>'), html.indexOf('</head>'));
    expect(head).toContain('<script src="/polyfills.js" async=""></script>');
    expect(html.slice(html.indexOf('<body>'))).not.toContain('polyfills');
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
