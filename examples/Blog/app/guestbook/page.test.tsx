import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import Guestbook from './page.tsx';

describe('the guestbook page', () => {
  it('shows the validation errors the action sent back, next to their fields', () => {
    const app = createTestApp({ url: '/guestbook' });

    render(<Guestbook entries={[]} errors={{ name: 'Tell us your name.' }} values={{ name: '', message: 'Hi' }} />, {
      wrapper: app.Wrapper,
    });

    expect(screen.getByLabelText('Name').getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByText('Tell us your name.')).toBeDefined();
    expect(screen.getByLabelText('Message')).toHaveProperty('value', 'Hi');
  });

  it('posts the form to the page action', async () => {
    const app = createTestApp({ url: '/guestbook' });
    // What the C# loader sends before anyone posts: GuestbookProps(entries) with null errors and values.
    render(<Guestbook entries={[]} errors={null} values={null} />, { wrapper: app.Wrapper });

    await userEvent.type(screen.getByLabelText('Name'), 'Ada');
    await userEvent.type(screen.getByLabelText('Message'), 'Hello');
    await userEvent.click(screen.getByRole('button', { name: 'Sign the guestbook' }));

    const [submission] = app.navigations;
    expect(submission?.type === 'submit' && Object.fromEntries(submission.data)).toMatchObject({ name: 'Ada', message: 'Hello' });
  });

  it('shows the error page when the server sends entries it does not understand', () => {
    expect(() => render(<Guestbook entries="nope" />, { wrapper: createTestApp().Wrapper })).toThrow(/Invalid props: entries/);
  });
});
