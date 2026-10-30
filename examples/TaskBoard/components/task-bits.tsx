import type { TaskPriority, TaskState } from '../generated/netjes.ts';

const stateColors: Record<TaskState, string> = {
  backlog: 'bg-state-backlog',
  todo: 'bg-state-todo',
  inProgress: 'bg-state-progress',
  done: 'bg-state-done',
};

export function StateDot({ state }: { state: TaskState }) {
  return <span aria-hidden="true" className={`inline-block size-2 shrink-0 rounded-full ${stateColors[state]}`} />;
}

const priorityBars: Record<TaskPriority, number> = { none: 0, low: 1, medium: 2, high: 3, urgent: 4 };

/** Signal-strength bars; urgent is a filled square with an exclamation mark. */
export function PriorityIcon({ priority }: { priority: TaskPriority }) {
  const label = `${priority[0]!.toUpperCase()}${priority.slice(1)} priority`;
  if (priority === 'urgent') {
    return (
      <span role="img" aria-label={label} title={label} className="inline-grid size-4 place-items-center rounded-[3px] bg-danger text-[10px] font-bold text-ink-950">
        !
      </span>
    );
  }
  const bars = priorityBars[priority];
  return (
    <span role="img" aria-label={label} title={label} className="inline-flex h-4 items-end gap-[2px]">
      {[1, 2, 3].map((bar) => (
        <span key={bar} className={`w-[3px] rounded-[1px] ${bar <= bars ? 'bg-ink-200' : 'bg-ink-700'}`} style={{ height: `${4 + bar * 3}px` }} />
      ))}
    </span>
  );
}
