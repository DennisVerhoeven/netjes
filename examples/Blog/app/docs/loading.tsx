/** Shown inside the docs layout while a docs page is on its way (streamed with Suspense). */
export default function DocsLoading() {
  return (
    <div aria-busy="true" className="grid animate-pulse gap-3">
      <div className="h-10 w-2/3 rounded bg-line" />
      <div className="h-4 rounded bg-line" />
      <div className="h-4 w-5/6 rounded bg-line" />
    </div>
  );
}
