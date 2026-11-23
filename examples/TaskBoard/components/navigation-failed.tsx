import { useNavigation, useRouter } from 'netjes';

/**
 * A navigation or a refresh that got no page (useNavigation().error): the page stays as it was and says so. A refresh
 * is tried again from here; a link that failed is still on the page to follow again. A failed form post is the form's
 * to show.
 */
export function NavigationFailed() {
  const navigation = useNavigation();
  const router = useRouter();
  const error = navigation.state === 'idle' ? navigation.error : undefined;
  if (!error || error.kind === 'submit') return null;
  const cause = error.status === null ? 'No answer came. Check your connection.' : `The server answered ${error.status}.`;
  return (
    <div role="alert" className="flex flex-wrap items-center gap-3 border-b border-ink-800 bg-ink-850 px-8 py-3 text-sm">
      <p className="text-danger">{error.kind === 'refresh' ? 'This page could not be brought up to date.' : 'That page could not be opened.'}</p>
      <p className="text-ink-300">{cause}</p>
      {error.kind === 'refresh' && (
        <button type="button" className="btn btn-ghost ml-auto py-1" onClick={() => void router.refresh()}>
          Try again
        </button>
      )}
    </div>
  );
}
