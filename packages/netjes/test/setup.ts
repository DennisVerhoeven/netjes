import { cleanup } from '@testing-library/react';
import { afterEach, vi } from 'vitest';

afterEach(() => {
  cleanup();
});

if (typeof window !== 'undefined') {
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;
  // jsdom lays nothing out and has no scrollIntoView, which the router calls on a fragment's target.
  Element.prototype.scrollIntoView = vi.fn();
}
