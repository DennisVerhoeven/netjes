import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { NavigationState } from 'netjes';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import { NavigationFailed } from './navigation-failed.tsx';

function renderWith(navigation: NavigationState) {
  const app = createTestApp({ navigation });
  render(<NavigationFailed />, { wrapper: app.Wrapper });
  return app;
}

describe('NavigationFailed', () => {
  it('says a refresh got no answer, and tries it again', async () => {
    const app = renderWith({ state: 'idle', error: { kind: 'refresh', status: null, location: '/projects/web' } });

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('This page could not be brought up to date.');
    expect(alert.textContent).toContain('No answer came.');
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(app.navigations).toEqual([{ type: 'refresh' }]);
  });

  it("says a link's page could not be opened, with the server's status", () => {
    renderWith({ state: 'idle', error: { kind: 'navigate', status: 502, location: '/projects/web/settings' } });

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('That page could not be opened.');
    expect(alert.textContent).toContain('The server answered 502.');
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
  });

  it('leaves a failed form post to the form', () => {
    renderWith({ state: 'idle', error: { kind: 'submit', status: 500, location: '/projects/web' } });
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
