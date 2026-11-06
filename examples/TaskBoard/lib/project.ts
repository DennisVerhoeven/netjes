import { useQuery } from '@tanstack/react-query';
import { useHref } from 'netjes';
import { type ProjectStats, type TaskState, api, endpointUrl } from '../generated/netjes.ts';

export const stateLabels: Record<TaskState, string> = { backlog: 'Backlog', todo: 'Todo', inProgress: 'In progress', done: 'Done' };
/** Board order, as TaskStates.All in C#. */
export const stateOrder: TaskState[] = ['backlog', 'todo', 'inProgress', 'done'];

/** Query keys, matching ProjectLayout.StatsKey in C# (the layout loader seeds it, task actions invalidate it). */
export const projectKeys = {
  stats: (slug: string) => ['projects', slug, 'stats'] as const,
};

async function fetchStats(url: string): Promise<ProjectStats> {
  const response = await fetch(url, { credentials: 'same-origin' });
  if (!response.ok) throw new Error(`Stats request failed with ${response.status}`);
  return api['GET /api/projects/{slug}/stats'].response.parse(await response.json());
}

/** Live counts for a project: server-rendered from the seeded query, then kept fresh by TanStack Query. */
export function useProjectStats(slug: string) {
  // The endpoint's URL from the generated file, under the app's base path.
  const url = useHref(endpointUrl('GET /api/projects/{slug}/stats', { slug }));
  return useQuery({ queryKey: projectKeys.stats(slug), queryFn: () => fetchStats(url), refetchInterval: 30_000 });
}

export function taskKey(slug: string, number: number) {
  return `${slug.toUpperCase()}-${number}`;
}
