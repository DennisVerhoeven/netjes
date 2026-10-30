/** Inside the project layout while a project page streams in: the header stays, the columns shimmer. */
export default function ProjectLoading() {
  return (
    <div aria-busy="true" aria-label="Loading" className="grid animate-pulse gap-4 lg:grid-cols-4">
      {[0, 1, 2, 3].map((column) => (
        <div key={column} className="grid content-start gap-2 rounded-card bg-ink-900/60 p-3">
          <div className="h-4 w-24 rounded bg-ink-800" />
          <div className="h-20 rounded-lg bg-ink-850" />
          <div className="h-20 rounded-lg bg-ink-850" />
        </div>
      ))}
    </div>
  );
}
