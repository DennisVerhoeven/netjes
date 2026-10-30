import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Form } from '../src/form.tsx';
import { Link } from '../src/link.tsx';
import { createTestApp } from '../src/testing.tsx';

describe('reloadDocument', () => {
  it('leaves a form post to the browser, which streams downloads, and still sends the antiforgery token', () => {
    const app = createTestApp({ url: '/export' });
    render(
      <Form method="post" action="/export/csv" reloadDocument>
        <button type="submit">Download</button>
      </Form>,
      { wrapper: app.Wrapper },
    );
    const form = screen.getByRole('button', { name: 'Download' }).closest('form')!;

    const notPrevented = fireEvent.submit(form);

    expect(notPrevented).toBe(true);
    expect(app.navigations).toEqual([]);
    expect(form.querySelector('input[name="__RequestVerificationToken"]')?.getAttribute('value')).toBe('test-token');
  });

  it('leaves a link to the browser', () => {
    const app = createTestApp();
    render(
      <Link href="/report.pdf" reloadDocument>
        Report
      </Link>,
      { wrapper: app.Wrapper },
    );

    const notPrevented = fireEvent.click(screen.getByRole('link', { name: 'Report' }));

    expect(notPrevented).toBe(true);
    expect(app.navigations).toEqual([]);
  });

  it('is off by default', () => {
    const app = createTestApp({ url: '/export' });
    render(
      <Form method="post" action="/export/csv">
        <button type="submit">Download</button>
      </Form>,
      { wrapper: app.Wrapper },
    );

    expect(fireEvent.submit(screen.getByRole('button', { name: 'Download' }).closest('form')!)).toBe(false);
  });
});
