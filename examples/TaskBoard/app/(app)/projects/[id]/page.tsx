import { Form, Link, definePage, useParams, useSearchParams } from 'netjes';
import { z } from 'zod';
import { NewTaskForm, TaskCard } from '../../../../components/board.tsx';
import { StateDot } from '../../../../components/task-bits.tsx';
import { TaskPriority, pages, route } from '../../../../generated/netjes.ts';

// The filters live in the URL. Values a visitor mistypes (?priority=whatever) are dropped, so the defaults apply.
const filters = z.object({
  q: z.string().optional(),
  priority: TaskPriority.optional(),
  mine: z.literal('true').optional(),
  done: z.coerce.number().int().positive().optional(),
});

const params = z.object({ id: z.string() });

/** BoardEndpoints.cs: the board. Filtering is a GET form, so it works without JavaScript and links can be shared. */
export default definePage(pages['/projects/[id]'], function Board({ columns, donePage, doneHasMore }) {
  const { id } = useParams(params);
  const search = useSearchParams(filters);
  const filtered = Boolean(search.q || search.priority || search.mine);
  const doneHref = (page: number) =>
    route('/projects/[id]', { id }, { q: search.q || undefined, priority: search.priority, mine: search.mine, done: page > 1 ? page : undefined });

  return (
    <div className="grid gap-5">
      <Form method="get" aria-label="Filter tasks" className="flex flex-wrap items-end gap-3">
        <div className="grid gap-1">
          <label htmlFor="q" className="label">
            Search
          </label>
          <input id="q" name="q" type="search" defaultValue={search.q ?? ''} placeholder="Title contains…" className="field w-64 py-1.5" />
        </div>
        <div className="grid gap-1">
          <label htmlFor="priority" className="label">
            Priority
          </label>
          <select id="priority" name="priority" defaultValue={search.priority ?? ''} className="field w-36 py-1.5">
            <option value="">Any</option>
            {TaskPriority.options.map((option) => (
              <option key={option} value={option}>
                {option[0]!.toUpperCase() + option.slice(1)}
              </option>
            ))}
          </select>
        </div>
        <label className="flex items-center gap-2 pb-2 text-sm text-ink-300">
          <input type="checkbox" name="mine" value="true" defaultChecked={search.mine === 'true'} className="accent-signal" />
          Assigned to me
        </label>
        <button type="submit" className="btn btn-ghost py-1.5">
          Apply
        </button>
        {filtered && (
          <Link href={`/projects/${id}`} className="pb-2 text-sm text-ink-400 hover:text-ink-100">
            Clear filters
          </Link>
        )}
      </Form>

      <div className="grid gap-4 lg:grid-cols-4">
        {columns.map((column) => (
          <section key={column.state} aria-labelledby={`column-${column.state}`} className="grid content-start gap-3 rounded-card bg-ink-900/60 p-3">
            <h2 id={`column-${column.state}`} className="flex items-center gap-2 text-sm font-medium text-ink-200">
              <StateDot state={column.state} />
              {column.label}
              <span className="key ml-auto">{column.total}</span>
            </h2>
            {column.state !== 'done' && <NewTaskForm slug={id} state={column.state} />}
            <ul aria-label={column.label} className="grid gap-2">
              {column.tasks.map((task) => (
                <TaskCard key={task.number} slug={id} task={task} state={column.state} />
              ))}
            </ul>
            {column.tasks.length === 0 && <p className="px-1 text-[13px] text-ink-500">{filtered ? 'Nothing matches.' : 'Empty.'}</p>}
            {column.state === 'done' && (donePage > 1 || doneHasMore) && (
              <nav aria-label="Done pages" className="flex justify-between text-[13px]">
                {donePage > 1 ? <Link href={doneHref(donePage - 1)}>← Newer</Link> : <span />}
                {doneHasMore && <Link href={doneHref(donePage + 1)}>Older →</Link>}
              </nav>
            )}
          </section>
        ))}
      </div>
    </div>
  );
});
