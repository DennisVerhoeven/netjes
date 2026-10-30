import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import { NewTaskForm, TaskCard } from './board.tsx';

const task = { number: 3, title: 'Launch post', priority: 'high' as const, assignee: { id: 'u1', name: 'Ada Lovelace' }, comments: 2 };

describe('TaskCard', () => {
  it('links to the task and moves it one column at a time through the tasks.move action', async () => {
    const app = createTestApp({ url: '/projects/web' });
    render(
      <ul>
        <TaskCard slug="web" task={task} state="todo" />
      </ul>,
      { wrapper: app.Wrapper },
    );

    expect(screen.getByRole('link', { name: 'Launch post' }).getAttribute('href')).toBe('/projects/web/tasks/3');
    expect(screen.getByRole('img', { name: 'High priority' })).toBeDefined();
    await userEvent.click(screen.getByRole('button', { name: 'Move WEB-3 to In progress' }));

    const call = app.navigations.find((n) => n.type === 'action');
    expect(call?.type === 'action' && call.name).toBe('tasks.move');
    const fields = call?.type === 'action' ? Object.fromEntries(call.input as FormData) : {};
    expect(fields).toMatchObject({ projectSlug: 'web', number: '3', state: 'inProgress' });
  });

  it('has no way left from the first column', () => {
    render(
      <ul>
        <TaskCard slug="web" task={task} state="backlog" />
      </ul>,
      { wrapper: createTestApp().Wrapper },
    );

    expect(screen.getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Move WEB-3 to Todo']);
  });
});

describe('NewTaskForm', () => {
  it('shows the error the action sends back', async () => {
    const app = createTestApp({
      actions: { 'tasks.create': () => ({ ok: false, status: 422, errors: { title: ['Give the task a title.'] } }) },
    });
    render(<NewTaskForm slug="web" state="todo" />, { wrapper: app.Wrapper });

    await userEvent.click(screen.getByRole('button', { name: 'Add task to Todo' }));

    expect(await screen.findByText('Give the task a title.')).toBeDefined();
    expect(screen.getByLabelText('New task in Todo').getAttribute('aria-invalid')).toBe('true');
  });
});
