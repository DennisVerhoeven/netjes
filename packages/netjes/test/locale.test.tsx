import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useLocale } from '../src/locale.ts';
import { parseNavigationPayload } from '../src/payload.ts';
import { createTestApp } from '../src/testing.tsx';

function Price() {
  const locale = useLocale();
  return <p>{new Intl.NumberFormat(locale ?? 'en', { style: 'currency', currency: 'EUR' }).format(1.5)}</p>;
}

const page = { kind: 'page', route: '/', page: 'app/page.tsx', segments: [], props: {}, params: {}, shared: null, status: 200, url: '/', version: 'v' };

describe('useLocale', () => {
  it('is the request culture the server used', () => {
    render(<Price />, { wrapper: createTestApp({ locale: 'nl' }).Wrapper });

    expect(screen.getByText('€ 1,50')).toBeDefined();
  });

  it('is null without localization', () => {
    function Raw() {
      return <p>{String(useLocale())}</p>;
    }

    render(<Raw />, { wrapper: createTestApp().Wrapper });

    expect(screen.getByText('null')).toBeDefined();
  });

  it('is parsed from the payload', () => {
    expect(parseNavigationPayload({ ...page, locale: 'nl' })).toMatchObject({ locale: 'nl' });
  });
});
