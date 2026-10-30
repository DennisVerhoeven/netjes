import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import { HelloForm } from './HelloForm.tsx';

describe('HelloForm', () => {
  it('shows the greeting the server action returns', async () => {
    const app = createTestApp({ actions: { 'home.greet': () => ({ ok: true, result: { text: 'Hello, Ada!' } }) } });
    render(<HelloForm />, { wrapper: app.Wrapper });

    await userEvent.type(screen.getByLabelText('Your name'), 'Ada');
    await userEvent.click(screen.getByRole('button', { name: 'Say hello' }));

    expect((await screen.findByRole('status')).textContent).toBe('Hello, Ada!');
  });

  it('shows the validation error from C# next to the field', async () => {
    const app = createTestApp({ actions: { 'home.greet': () => ({ ok: false, status: 422, errors: { name: ['Tell us your name.'] } }) } });
    render(<HelloForm />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'Say hello' }));

    expect(await screen.findByText('Tell us your name.')).toBeDefined();
    expect(screen.getByLabelText('Your name').getAttribute('aria-invalid')).toBe('true');
  });
});
