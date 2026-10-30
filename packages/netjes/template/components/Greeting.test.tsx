import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import { Greeting } from './Greeting.tsx';

describe('Greeting', () => {
  it('shows the message from the loader and counts clicks', async () => {
    render(<Greeting message="Hello" renderedAt="2026-10-28T12:00:00Z" />, { wrapper: createTestApp().Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'Clicked 0 times' }));

    expect(screen.getByRole('heading', { name: 'Hello' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Clicked 1 time' })).toBeDefined();
  });

  it('links to the about page without a full page load', async () => {
    const app = createTestApp();
    render(<Greeting message="Hello" renderedAt="2026-10-28T12:00:00Z" />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('link', { name: 'About this app' }));

    expect(app.navigations).toEqual([{ type: 'push', href: '/about' }]);
  });
});
