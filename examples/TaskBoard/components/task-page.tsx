import { Form, useAction } from 'netjes';
import { use, useOptimistic } from 'react';
import { type ActivityItem, type CommentItem, type Person, type TaskDetail, TaskPriority, serverActions } from '../generated/netjes.ts';
import { stateLabels, stateOrder } from '../lib/project.ts';
import { Avatar } from './brand.tsx';
import { Field, SubmitButton, errorsOf, valueOf } from './forms.tsx';

const timeFormat = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' });

export function formatTime(iso: string) {
  return `${timeFormat.format(new Date(iso))} UTC`;
}

/**
 * Every field of the task in one form; the C# action records what changed in the activity feed. React resets the form
 * after a save, so after a failed one (also a post without JavaScript) each field starts from what was sent.
 */
export function TaskForm({ slug, task, members }: { slug: string; task: Pick<TaskDetail, 'number' | 'title' | 'description' | 'state' | 'priority' | 'assignee'>; members: Person[] }) {
  const [state, formAction] = useAction(serverActions['tasks.update']);
  const saved = state?.ok === true;
  const sent = (field: string, stored: string) => valueOf(state, field) ?? stored;
  const initial = {
    title: sent('title', task.title),
    description: sent('description', task.description),
    state: sent('state', task.state),
    priority: sent('priority', task.priority),
    assigneeId: sent('assigneeId', task.assignee?.id ?? ''),
  };
  return (
    <Form action={formAction} aria-label="Edit task" className="grid gap-4">
      <input type="hidden" name="projectSlug" value={slug} />
      <input type="hidden" name="number" value={task.number} />
      <Field label="Title" name="title" required defaultValue={initial.title} errors={errorsOf(state, 'title')} />
      <Field label="Description" name="description" multiline rows={6} defaultValue={initial.description} errors={errorsOf(state, 'description')} />
      <div className="grid gap-4 sm:grid-cols-3">
        <div className="grid gap-1.5">
          <label htmlFor="state" className="label">
            State
          </label>
          {/* A select applies its defaultValue only when it mounts: keyed on it, it starts again from a new one. */}
          <select key={initial.state} id="state" name="state" defaultValue={initial.state} className="field">
            {stateOrder.map((value) => (
              <option key={value} value={value}>
                {stateLabels[value]}
              </option>
            ))}
          </select>
        </div>
        <div className="grid gap-1.5">
          <label htmlFor="priority" className="label">
            Priority
          </label>
          <select key={initial.priority} id="priority" name="priority" defaultValue={initial.priority} className="field">
            {TaskPriority.options.map((value) => (
              <option key={value} value={value}>
                {value[0]!.toUpperCase() + value.slice(1)}
              </option>
            ))}
          </select>
        </div>
        <div className="grid gap-1.5">
          <label htmlFor="assigneeId" className="label">
            Assignee
          </label>
          <select
            key={initial.assigneeId}
            id="assigneeId"
            name="assigneeId"
            defaultValue={initial.assigneeId}
            aria-invalid={errorsOf(state, 'assigneeId').length > 0 || undefined}
            className="field"
          >
            <option value="">Nobody</option>
            {members.map((member) => (
              <option key={member.id} value={member.id}>
                {member.name}
              </option>
            ))}
          </select>
          {errorsOf(state, 'assigneeId').map((error) => (
            <p key={error} className="text-[13px] text-danger">
              {error}
            </p>
          ))}
        </div>
      </div>
      <div className="flex items-center gap-3">
        <SubmitButton pending="Saving…">Save changes</SubmitButton>
        <p role="status" className="text-[13px] text-ink-400">
          {saved ? 'Saved.' : ''}
        </p>
      </div>
    </Form>
  );
}

type CommentData = CommentItem & { pending?: boolean };

/** Comments, with the new one shown the moment it is sent (useOptimistic through useAction's onSubmit). */
export function Comments({ slug, number, comments, me }: { slug: string; number: number; comments: CommentItem[]; me: Person }) {
  const [shown, addPending] = useOptimistic<CommentData[], CommentData>(comments, (current, next) => [...current, next]);
  const [state, formAction] = useAction(serverActions['comments.add'], {
    onSubmit: (data) =>
      addPending({ id: `pending-${shown.length}`, body: String(data.get('body') ?? ''), author: me, createdAt: new Date().toISOString(), pending: true }),
  });
  return (
    <section aria-labelledby="comments" className="grid gap-4">
      <h2 id="comments" className="label">
        Comments
      </h2>
      <ol aria-label="Comments" className="grid gap-3">
        {shown.map((comment) => (
          <li key={comment.id} aria-busy={comment.pending || undefined} className={`flex gap-3 ${comment.pending ? 'opacity-60' : ''}`}>
            <Avatar name={comment.author.name} size="md" />
            <div className="panel min-w-0 flex-1 px-4 py-3">
              <p className="mb-1 text-[12px] text-ink-400">
                <span className="font-medium text-ink-200">{comment.author.name}</span> · {comment.pending ? 'sending…' : formatTime(comment.createdAt)}
              </p>
              <p className="whitespace-pre-wrap">{comment.body}</p>
            </div>
          </li>
        ))}
        {shown.length === 0 && <li className="text-ink-500">No comments yet.</li>}
      </ol>
      <Form action={formAction} aria-label="Add a comment" className="grid gap-3">
        <input type="hidden" name="projectSlug" value={slug} />
        <input type="hidden" name="number" value={number} />
        <Field label="Your comment" name="body" multiline rows={3} required errors={errorsOf(state, 'body')} />
        <SubmitButton pending="Sending…" className="btn w-fit">
          Comment
        </SubmitButton>
      </Form>
    </section>
  );
}

/** Reads the deferred feed (Page.Defer in C#); the Suspense boundary around it shows a placeholder until it streams in. */
export function ActivityFeed({ activity }: { activity: PromiseLike<ActivityItem[]> }) {
  const items = use(activity);
  return (
    <ol aria-label="Activity" className="grid gap-2.5">
      {items.map((item) => (
        <li key={item.id} className="grid gap-0.5 border-l-2 border-ink-700 pl-3 text-[13px]">
          <span>
            <span className="font-medium text-ink-200">{item.actor}</span> <span className="text-ink-300">{item.summary}</span>
          </span>
          <span className="text-[12px] text-ink-500">{formatTime(item.at)}</span>
        </li>
      ))}
    </ol>
  );
}
