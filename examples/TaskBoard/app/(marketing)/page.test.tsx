import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import Landing from './page.tsx';

describe('the landing page', () => {
  it('signs a visitor in where they are: the form asks the action to stay', async () => {
    const app = createTestApp({ url: '/', shared: { user: null } });
    render(<Landing />, { wrapper: app.Wrapper });
    const form = screen.getByRole('form', { name: 'Sign in here' });

    await userEvent.type(within(form).getByLabelText('Email'), 'ada@example.test');
    await userEvent.type(within(form).getByLabelText('Password'), 'correct horse battery');
    await userEvent.click(within(form).getByRole('button', { name: 'Sign in here' }));

    await waitFor(() => expect(app.navigations.some((n) => n.type === 'action')).toBe(true));
    const call = app.navigations.find((n) => n.type === 'action');
    const fields = call?.type === 'action' ? Object.fromEntries(call.input as FormData) : {};
    expect(fields).toMatchObject({ email: 'ada@example.test', password: 'correct horse battery', stay: 'true' });
  });

  it('shows a signed-in user no sign-in form', () => {
    const app = createTestApp({
      url: '/',
      shared: { user: { id: '6f1c2b1e-4c4e-4b8a-9d1a-2f3e4d5c6b7a', name: 'Ada', email: 'ada@example.test' } },
    });

    render(<Landing />, { wrapper: app.Wrapper });

    expect(screen.queryByRole('form', { name: 'Sign in here' })).toBeNull();
  });
});
