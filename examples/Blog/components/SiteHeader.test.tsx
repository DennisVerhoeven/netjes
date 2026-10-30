import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import { SiteHeader } from './SiteHeader.tsx';

const shared = {
  siteName: 'Netjes Blog',
  navigation: [
    { href: '/blog', label: 'Blog' },
    { href: '/about', label: 'About' },
  ],
};

describe('SiteHeader', () => {
  it('marks the current section and navigates on the client', async () => {
    const app = createTestApp({ url: '/blog', shared });
    render(<SiteHeader />, { wrapper: app.Wrapper });

    expect(screen.getByRole('link', { name: 'Blog', current: 'page' })).toBeDefined();
    await userEvent.click(screen.getByRole('link', { name: 'About' }));

    expect(app.navigations).toEqual([{ type: 'push', href: '/about' }]);
    expect(screen.getByRole('link', { name: 'About', current: 'page' })).toBeDefined();
  });
});
