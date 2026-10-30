import { render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createTestApp } from '../src/testing.tsx';
import { VitalsEndpointProvider } from '../src/vitals-config.ts';

type Report = (metric: Record<string, unknown>) => void;
const handlers = vi.hoisted(() => ({}) as Record<string, Report>);

vi.mock('web-vitals', () => {
  const on = (name: string) => (report: Report) => {
    handlers[name] = report;
  };
  return { onCLS: on('CLS'), onFCP: on('FCP'), onINP: on('INP'), onLCP: on('LCP'), onTTFB: on('TTFB') };
});

const { WebVitals, useReportWebVitals } = await import('../src/vitals.tsx');

function metric(name: string, value: number) {
  return { name, value, rating: 'good', delta: value, id: `v1-${name}`, navigationType: 'navigate', entries: [] };
}

beforeEach(() => {
  Object.defineProperty(navigator, 'sendBeacon', { value: vi.fn(() => true), configurable: true });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('useReportWebVitals', () => {
  it('reports each Core Web Vital with the route the page was loaded on', () => {
    const seen: unknown[] = [];
    function Reporter() {
      useReportWebVitals((m) => seen.push(m));
      return null;
    }
    render(<Reporter />, { wrapper: createTestApp({ url: '/blog/hello', route: '/blog/[slug]' }).Wrapper });

    handlers.LCP?.(metric('LCP', 1200));
    handlers.CLS?.(metric('CLS', 0.05));

    expect(seen).toEqual([
      { name: 'LCP', value: 1200, rating: 'good', delta: 1200, id: 'v1-LCP', navigationType: 'navigate', route: '/blog/[slug]' },
      { name: 'CLS', value: 0.05, rating: 'good', delta: 0.05, id: 'v1-CLS', navigationType: 'navigate', route: '/blog/[slug]' },
    ]);
  });

  it('replays metrics reported before a later component subscribed', () => {
    const late: unknown[] = [];
    function Late() {
      useReportWebVitals((m) => late.push(m));
      return null;
    }

    render(<Late />, { wrapper: createTestApp().Wrapper });

    expect(late.map((m) => (m as { name: string }).name)).toEqual(['LCP', 'CLS']);
  });
});

describe('WebVitals', () => {
  it('sends every metric to the server beacon when the server collects them', () => {
    render(
      <VitalsEndpointProvider value="/_netjes/vitals">
        <WebVitals />
      </VitalsEndpointProvider>,
      { wrapper: createTestApp().Wrapper },
    );

    handlers.INP?.(metric('INP', 80));

    const beacon = vi.mocked(navigator.sendBeacon);
    // The metrics measured before it mounted go out too, each once.
    expect(beacon.mock.calls.map(([, body]) => JSON.parse(String(body)).name)).toEqual(['LCP', 'CLS', 'INP']);
    const [url, body] = beacon.mock.calls.at(-1) as [string, string];
    expect(url).toBe('/_netjes/vitals');
    expect(JSON.parse(body)).toEqual({ name: 'INP', value: 80, rating: 'good', route: '/blog/[slug]', navigationType: 'navigate' });
  });

  it('sends nothing when the server does not collect them', () => {
    render(<WebVitals />, { wrapper: createTestApp().Wrapper });

    handlers.INP?.(metric('INP', 90));

    expect(vi.mocked(navigator.sendBeacon).mock.calls.some(([, body]) => String(body).includes('"value":90'))).toBe(false);
  });

  it('sends each metric once when it mounts again', () => {
    const ui = (
      <VitalsEndpointProvider value="/_netjes/vitals">
        <WebVitals />
      </VitalsEndpointProvider>
    );
    const first = render(ui, { wrapper: createTestApp().Wrapper });
    handlers.TTFB?.(metric('TTFB', 100));
    const beacon = vi.mocked(navigator.sendBeacon);
    const sent = beacon.mock.calls.length;

    first.unmount();
    render(ui, { wrapper: createTestApp().Wrapper });

    expect(beacon).toHaveBeenCalledTimes(sent);
    expect(beacon.mock.calls.filter(([, body]) => JSON.parse(String(body)).name === 'TTFB')).toHaveLength(1);
  });
});
