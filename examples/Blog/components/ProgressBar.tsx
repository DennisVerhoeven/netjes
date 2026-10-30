import { useNavigation } from 'netjes';

/** A thin bar at the top of the window while the next page is loading or a form is submitting. */
export function ProgressBar() {
  const navigation = useNavigation();
  return (
    <div
      data-state={navigation.state}
      aria-hidden="true"
      className="fixed top-0 left-0 z-10 h-[3px] w-0 bg-accent opacity-0 transition-[width,opacity] duration-200 ease-out data-[state=loading]:w-4/5 data-[state=loading]:opacity-100 data-[state=loading]:duration-[8s] data-[state=submitting]:w-4/5 data-[state=submitting]:opacity-100 data-[state=submitting]:duration-[8s]"
    />
  );
}
