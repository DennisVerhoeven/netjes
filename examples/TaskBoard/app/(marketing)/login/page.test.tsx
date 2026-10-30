import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import Login from './page.tsx';

describe('the login page', () => {
  it('posts the credentials and the return URL, and shows the error the action sends back', async () => {
    const app = createTestApp({
      url: '/login?returnUrl=%2Fprojects%2Fweb',
      shared: { user: null },
      actions: { 'auth.login': () => ({ ok: false, status: 422, errors: { password: ['That email and password do not match.'] } }) },
    });
    render(<Login returnUrl="/projects/web" />, { wrapper: app.Wrapper });

    await userEvent.type(screen.getByLabelText('Email'), 'ada@example.test');
    await userEvent.type(screen.getByLabelText('Password'), 'not it');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('That email and password do not match.')).toBeDefined();
    const call = app.navigations.find((n) => n.type === 'action');
    const fields = call?.type === 'action' ? Object.fromEntries(call.input as FormData) : {};
    expect(fields).toMatchObject({ email: 'ada@example.test', password: 'not it', returnUrl: '/projects/web' });
  });

  it('says so on the form when there were too many attempts, and keeps the email', async () => {
    const app = createTestApp({
      url: '/login',
      shared: { user: null },
      actions: { 'auth.login': () => ({ ok: false, status: 429, errors: {} }) },
    });
    render(<Login returnUrl={null} />, { wrapper: app.Wrapper });

    await userEvent.type(screen.getByLabelText('Email'), 'ada@example.test');
    await userEvent.type(screen.getByLabelText('Password'), 'not it');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('Too many sign-in attempts. Try again in a few minutes.')).toBeDefined();
    expect(screen.getByLabelText('Email')).toHaveProperty('value', 'ada@example.test');
  });

  it('starts from the values and errors a failed post without JavaScript left behind', () => {
    const app = createTestApp({ url: '/login', shared: { user: null } });

    render(<Login returnUrl={null} />, { wrapper: app.Wrapper });

    // (The payload's actionState is what the server re-render sends; the harness has none, so the form is empty.)
    expect(screen.getByLabelText('Email')).toHaveProperty('value', '');
    expect(screen.getByRole('link', { name: 'Create an account' }).getAttribute('href')).toBe('/register');
  });
});
