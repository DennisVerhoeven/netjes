import { Link, type Metadata, useParams } from 'netjes';
import { z } from 'zod';

const params = z.object({ id: z.string() });

export const metadata: Metadata = { title: 'No such task' };

/** A task number that does not exist: shown inside the project layout, so the header and tabs stay. */
export default function TaskNotFound() {
  const { id } = useParams(params);
  return (
    <section className="grid max-w-lg gap-3 py-6">
      <h2 className="text-lg font-semibold">There is no such task</h2>
      <p className="text-ink-400">It may have been a typo in the link.</p>
      <Link href={`/projects/${id}`} className="btn w-fit">
        Back to the board
      </Link>
    </section>
  );
}
