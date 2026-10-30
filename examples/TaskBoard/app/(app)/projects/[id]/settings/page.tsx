import { Form, definePage, useAction, useParams } from 'netjes';
import { z } from 'zod';
import { Avatar } from '../../../../../components/brand.tsx';
import { Field, SubmitButton, errorsOf, valueOf } from '../../../../../components/forms.tsx';
import { formatTime } from '../../../../../components/task-page.tsx';
import { pages, serverActions } from '../../../../../generated/netjes.ts';

const params = z.object({ id: z.string() });

/** MembersEndpoints.cs: who is on the project; owners add people by the email they signed up with. */
export default definePage(pages['/projects/[id]/settings'], function Members({ members, canManage }) {
  const { id } = useParams(params);
  const [state, formAction] = useAction(serverActions['projects.addMember']);
  return (
    <div className="grid max-w-2xl gap-8">
      <ul aria-label="Members" className="panel divide-y divide-ink-800">
        {members.map((member) => (
          <li key={member.id} className="flex items-center gap-3 px-4 py-3">
            <Avatar name={member.name} size="md" />
            <div className="min-w-0 flex-1">
              <p className="font-medium">{member.name}</p>
              <p className="truncate text-[13px] text-ink-400">{member.email}</p>
            </div>
            <div className="text-right">
              <p className="label">{member.role}</p>
              <p className="text-[12px] text-ink-500">since {formatTime(member.joinedAt)}</p>
            </div>
          </li>
        ))}
      </ul>
      {canManage ? (
        <section aria-labelledby="add-member" className="grid gap-3">
          <h2 id="add-member" className="text-lg font-semibold">
            Add someone
          </h2>
          <Form action={formAction} className="panel flex flex-wrap items-start gap-3 p-4">
            <input type="hidden" name="projectSlug" value={id} />
            <div className="min-w-64 flex-1">
              <Field label="Email" name="email" type="email" required defaultValue={valueOf(state, 'email')} errors={errorsOf(state, 'email')} />
            </div>
            <div className="pt-6">
              <SubmitButton pending="Adding…">Add member</SubmitButton>
            </div>
          </Form>
          {state?.ok && <p role="status" className="text-[13px] text-state-done">Added.</p>}
        </section>
      ) : (
        <p className="text-ink-400">Only owners can add members.</p>
      )}
    </div>
  );
});
