import { Link, definePage, useHistoryState, useParams } from 'netjes';
import { Suspense } from 'react';
import { z } from 'zod';
import { ActivityFeed, Comments, TaskForm, formatTime } from '../../../../../../components/task-page.tsx';
import { Href, pages } from '../../../../../../generated/netjes.ts';
import { taskKey } from '../../../../../../lib/project.ts';
import { useUser } from '../../../../../../lib/session.ts';

const params = z.object({ id: z.string() });
const boardHref = Href.nullable();

/**
 * TaskEndpoints.cs: one task. The title comes from C# (.WithMetadata) through the root layout's title template; the
 * activity feed streams in after the page (Page.Defer), validated when it arrives.
 */
export default definePage(pages['/projects/[id]/tasks/[taskId]'], function TaskPage({ task, members, comments, activity }) {
  const { id } = useParams(params);
  const user = useUser();
  // The board as it was filtered when its card opened this task, kept with the history entry (useHistoryState), so it
  // is still the way back after Back, Forward and a reload. Opened another way: the board without filters.
  const [opener] = useHistoryState('board', null, boardHref);
  const board = `/projects/${id}` as const;
  const back = opener === board || opener?.startsWith(`${board}?`) ? opener : board;
  return (
    <div className="grid gap-8 lg:grid-cols-[minmax(0,1fr)_18rem]">
      <article className="grid content-start gap-8">
        <div className="grid gap-1">
          <p className="flex items-center gap-2 text-[13px] text-ink-400">
            <Link href={back} className="hover:text-ink-100">
              ← Board
            </Link>
            <span className="key">{taskKey(id, task.number)}</span>
          </p>
          <p className="text-[12px] text-ink-500">
            Opened by {task.createdBy.name} · {formatTime(task.createdAt)}
          </p>
        </div>
        <TaskForm slug={id} task={task} members={members} />
        {user && <Comments slug={id} number={task.number} comments={comments} me={{ id: user.id, name: user.name }} />}
      </article>
      <aside aria-labelledby="activity" className="panel grid content-start gap-3 p-4">
        <h2 id="activity" className="label">
          Activity
        </h2>
        <Suspense fallback={<p className="animate-pulse text-[13px] text-ink-500">Loading the history…</p>}>
          <ActivityFeed activity={activity} />
        </Suspense>
      </aside>
    </div>
  );
});
