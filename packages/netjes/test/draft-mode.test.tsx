import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useDraftMode } from '../src/draft-mode.ts';
import { parseNavigationPayload } from '../src/payload.ts';
import { createTestApp } from '../src/testing.tsx';

function Banner() {
  const { isEnabled } = useDraftMode();
  return <p>{isEnabled ? 'Previewing a draft' : 'Published'}</p>;
}

const page = { kind: 'page', route: '/', page: 'app/page.tsx', segments: [], props: {}, params: {}, shared: null, status: 200, url: '/', version: 'v' };

describe('useDraftMode', () => {
  it('reports draft mode from the page payload', () => {
    render(<Banner />, { wrapper: createTestApp({ draftMode: true }).Wrapper });

    expect(screen.getByText('Previewing a draft')).toBeDefined();
  });

  it('is off unless the server says otherwise', () => {
    render(<Banner />, { wrapper: createTestApp().Wrapper });

    expect(screen.getByText('Published')).toBeDefined();
  });

  it('is parsed from the payload', () => {
    expect(parseNavigationPayload({ ...page, draftMode: true })).toMatchObject({ draftMode: true });
    expect(() => parseNavigationPayload({ ...page, draftMode: 'yes' })).toThrow();
  });
});
