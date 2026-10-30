import { Form, Link, useAction } from 'netjes';
import { type TaskCard as TaskCardData, type TaskState, serverActions } from '../generated/netjes.ts';
import { stateLabels, stateOrder, taskKey } from '../lib/project.ts';
import { Avatar } from './brand.tsx';
import { errorsOf } from './forms.tsx';
import { PriorityIcon } from './task-bits.tsx';

/** Move a task one column left or right: two tiny forms, each a server action that revalidates the board. */
function MoveButtons({ slug, number, state }: { slug: string; number: number; state: TaskState }) {
  const [, formAction, pending] = useAction(serverActions['tasks.move']);
  const index = stateOrder.indexOf(state);
  const targets = [
    { to: stateOrder[index - 1], symbol: '←' },
    { to: stateOrder[index + 1], symbol: '→' },
  ].filter((target): target is { to: TaskState; symbol: string } => target.to !== undefined);
  return (
    <div className="flex gap-1">
      {targets.map((target) => (
        <Form key={target.to} action={formAction}>
          <input type="hidden" name="projectSlug" value={slug} />
          <input type="hidden" name="number" value={number} />
          <input type="hidden" name="state" value={target.to} />
          <button
            type="submit"
            disabled={pending}
            aria-label={`Move ${taskKey(slug, number)} to ${stateLabels[target.to]}`}
            className="grid size-6 cursor-pointer place-items-center rounded-md text-ink-400 hover:bg-ink-700 hover:text-ink-100 disabled:opacity-40"
          >
            {target.symbol}
          </button>
        </Form>
      ))}
    </div>
  );
}

export function TaskCard({ slug, task, state }: { slug: string; task: TaskCardData; state: TaskState }) {
  return (
    <li className="group grid gap-2 rounded-lg border border-ink-800 bg-ink-850 p-3 transition-colors hover:border-ink-600">
      <div className="flex items-center justify-between gap-2">
        <span className="key">{taskKey(slug, task.number)}</span>
        <PriorityIcon priority={task.priority} />
      </div>
      <Link href={`/projects/${slug}/tasks/${task.number}`} className="font-medium leading-snug hover:text-signal">
        {task.title}
      </Link>
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-2 text-[12px] text-ink-400">
          {task.assignee ? <Avatar name={task.assignee.name} /> : <span className="size-6 rounded-full border border-dashed border-ink-600" />}
          {task.comments > 0 && <span aria-label={`${task.comments} comments`}>💬 {task.comments}</span>}
        </span>
        <MoveButtons slug={slug} number={task.number} state={state} />
      </div>
    </li>
  );
}

/** Quick add at the top of a column. React resets the field once the action succeeds. */
export function NewTaskForm({ slug, state }: { slug: string; state: TaskState }) {
  const [result, formAction, pending] = useAction(serverActions['tasks.create']);
  const errors = errorsOf(result, 'title');
  return (
    <Form action={formAction} className="grid gap-1.5">
      <input type="hidden" name="projectSlug" value={slug} />
      <input type="hidden" name="state" value={state} />
      <label htmlFor={`new-${state}`} className="sr-only">
        New task in {stateLabels[state]}
      </label>
      <div className="flex gap-1.5">
        <input
          id={`new-${state}`}
          name="title"
          placeholder="Add a task…"
          aria-invalid={errors.length > 0 || undefined}
          className="field min-w-0 py-1.5 text-sm"
        />
        <button type="submit" className="btn px-2.5 py-1.5" disabled={pending} aria-label={`Add task to ${stateLabels[state]}`}>
          +
        </button>
      </div>
      {errors.length > 0 && <p className="text-[12px] text-danger">{errors[0]}</p>}
    </Form>
  );
}
