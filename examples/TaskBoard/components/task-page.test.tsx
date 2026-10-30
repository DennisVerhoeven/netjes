import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { Suspense } from 'react';
import { describe, expect, it } from 'vitest';
import { ActivityFeed, Comments, TaskForm } from './task-page.tsx';

const me = { id: 'u1', name: 'Ada Lovelace' };

describe('Comments', () => {
  it('shows a new comment while it is being sent, then the saved list', async () => {
    let release: () => void = () => {};
    const sent = new Promise<void>((resolve) => {
      release = resolve;
    });
    const app = createTestApp({
      actions: {
        'comments.add': async () => {
          await sent;
          return { ok: true, result: null };
        },
      },
    });
    render(<Comments slug="web" number={3} comments={[]} me={me} />, { wrapper: app.Wrapper });

    await userEvent.type(screen.getByLabelText('Your comment'), 'Looks good');
    await userEvent.click(screen.getByRole('button', { name: 'Comment' }));

    expect(await screen.findByText('Looks good')).toBeDefined();
    expect(screen.getByText(/sending…/)).toBeDefined();
    await act(async () => release());
    expect(screen.queryByText(/sending…/)).toBeNull();
  });
});

describe('TaskForm', () => {
  it('shows field errors, e.g. an assignee who is not a member', async () => {
    const app = createTestApp({
      actions: { 'tasks.update': () => ({ ok: false, status: 422, errors: { assigneeId: ['Only members of the project can be assigned.'] } }) },
    });
    render(
      <TaskForm slug="web" task={{ number: 3, title: 'Launch post', description: '', state: 'todo', priority: 'none', assignee: null }} members={[me]} />,
      { wrapper: app.Wrapper },
    );

    await userEvent.selectOptions(screen.getByLabelText('Assignee'), 'Ada Lovelace');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Only members of the project can be assigned.')).toBeDefined();
    const call = app.navigations.find((n) => n.type === 'action');
    expect(call?.type === 'action' && (call.input as FormData).get('assigneeId')).toBe('u1');
  });

  it('keeps every field as typed when the save fails', async () => {
    const app = createTestApp({
      actions: { 'tasks.update': () => ({ ok: false, status: 422, errors: { title: ['Give the task a title.'] } }) },
    });
    render(
      <TaskForm slug="web" task={{ number: 3, title: 'Launch post', description: 'One page.', state: 'todo', priority: 'none', assignee: null }} members={[me]} />,
      { wrapper: app.Wrapper },
    );

    await userEvent.clear(screen.getByLabelText('Title'));
    await userEvent.type(screen.getByLabelText('Title'), '   ');
    await userEvent.clear(screen.getByLabelText('Description'));
    await userEvent.type(screen.getByLabelText('Description'), 'Two pages, not one.');
    await userEvent.selectOptions(screen.getByLabelText('State'), 'inProgress');
    await userEvent.selectOptions(screen.getByLabelText('Priority'), 'urgent');
    await userEvent.selectOptions(screen.getByLabelText('Assignee'), 'Ada Lovelace');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Give the task a title.')).toBeDefined();
    const value = (label: string) => (screen.getByLabelText(label) as HTMLInputElement).value;
    expect([value('Title'), value('Description'), value('State'), value('Priority'), value('Assignee')]).toEqual([
      '   ',
      'Two pages, not one.',
      'inProgress',
      'urgent',
      'u1',
    ]);
  });
});

describe('ActivityFeed', () => {
  it('waits for the streamed activity inside its Suspense boundary', async () => {
    let resolve: (value: { id: number; summary: string; actor: string; at: string }[]) => void = () => {};
    const activity = new Promise<{ id: number; summary: string; actor: string; at: string }[]>((r) => {
      resolve = r;
    });
    await act(async () => {
      render(
        <Suspense fallback={<p>Loading the history…</p>}>
          <ActivityFeed activity={activity} />
        </Suspense>,
      );
    });

    expect(screen.getByText('Loading the history…')).toBeDefined();
    await act(async () => resolve([{ id: 1, summary: 'created WEB-3', actor: 'Ada', at: '2026-10-28T12:00:00Z' }]));

    expect(screen.getByRole('list', { name: 'Activity' }).textContent).toContain('Ada created WEB-3');
  });
});
