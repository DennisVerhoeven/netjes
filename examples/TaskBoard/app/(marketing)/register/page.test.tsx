import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import Register from './page.tsx';

describe('the sign-up page', () => {
  it('says so on the form when there were too many sign-ups, and keeps what was typed', async () => {
    const app = createTestApp({
      url: '/register',
      shared: { user: null },
      actions: { 'auth.register': () => ({ ok: false, status: 429, errors: {} }) },
    });
    render(<Register returnUrl={null} />, { wrapper: app.Wrapper });

    await userEvent.type(screen.getByLabelText('Name'), 'Ada');
    await userEvent.type(screen.getByLabelText('Email'), 'ada@example.test');
    await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery');
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));

    expect(await screen.findByText('Too many sign-ups from your network. Try again later.')).toBeDefined();
    expect(screen.getByLabelText('Name')).toHaveProperty('value', 'Ada');
    expect(screen.getByLabelText('Email')).toHaveProperty('value', 'ada@example.test');
  });
});
