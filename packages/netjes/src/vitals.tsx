// netjes/vitals: Core Web Vitals, measured with the web-vitals package (an optional peer dependency of netjes).
import { useContext, useEffect, useRef } from 'react';
import { type Metric, onCLS, onFCP, onINP, onLCP, onTTFB } from 'web-vitals';
import { usePagePayload } from './router.tsx';
import { VitalsEndpointContext } from './vitals-config.ts';

/** One measurement, as `web-vitals` reports it, with the route pattern of the page it was measured on. */
export type WebVitalsMetric = Pick<Metric, 'name' | 'value' | 'rating' | 'delta' | 'id' | 'navigationType'> & {
  route: string | null;
};

type Subscriber = (metric: WebVitalsMetric) => void;

const subscribers = new Set<Subscriber>();
const reported: WebVitalsMetric[] = [];
let started = false;

/** Web vitals describe the page load, so they are measured once, for the route the document was loaded on. */
function start(route: string | null) {
  if (started) return;
  started = true;
  const report = ({ name, value, rating, delta, id, navigationType }: Metric) => {
    const metric: WebVitalsMetric = { name, value, rating, delta, id, navigationType, route };
    reported.push(metric);
    for (const subscriber of subscribers) subscriber(metric);
  };
  onCLS(report);
  onFCP(report);
  onINP(report);
  onLCP(report);
  onTTFB(report);
}

/** Like Next.js's `useReportWebVitals`: calls back with each Core Web Vital of this page load (also ones measured earlier). */
export function useReportWebVitals(callback: (metric: WebVitalsMetric) => void) {
  const { route } = usePagePayload();
  const latest = useRef(callback);
  latest.current = callback;

  useEffect(() => {
    const subscriber: Subscriber = (metric) => latest.current(metric);
    for (const metric of reported) subscriber(metric);
    subscribers.add(subscriber);
    start(route);
    return () => {
      subscribers.delete(subscriber);
    };
    // Subscribes once; the route is the one the page was loaded on.
  }, []);
}

/** The measurements a `<WebVitals />` has sent, so one that mounts again does not send the replayed ones twice. */
const sent = new WeakSet<WebVitalsMetric>();

/**
 * Sends Core Web Vitals to the server, which records them as `netjes.web_vitals.*` histograms (turn on
 * `NetjesOptions.CollectWebVitals`). Render it once, e.g. in the root layout.
 */
export function WebVitals() {
  const endpoint = useContext(VitalsEndpointContext);
  useReportWebVitals((metric) => {
    if (!endpoint || sent.has(metric)) return;
    sent.add(metric);
    const { name, value, rating, route, navigationType } = metric;
    navigator.sendBeacon(endpoint, JSON.stringify({ name, value, rating, route, navigationType }));
  });
  return null;
}
