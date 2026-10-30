import { Link } from 'netjes';
import { useState } from 'react';

export function Greeting({ message, renderedAt }: { message: string; renderedAt: string }) {
  const [clicks, setClicks] = useState(0);
  return (
    <section className="space-y-5">
      <h1 className="text-4xl font-bold tracking-tight">{message}</h1>
      <p className="text-slate-600">
        Rendered by ASP.NET Core at <time dateTime={renderedAt}>{renderedAt}</time>.
      </p>
      <button
        type="button"
        onClick={() => setClicks((count) => count + 1)}
        className="cursor-pointer rounded-full bg-slate-900 px-4 py-2 font-medium text-white hover:bg-slate-700"
      >
        Clicked {clicks} {clicks === 1 ? 'time' : 'times'}
      </button>
      <p>
        <Link href="/about" className="underline">
          About this app
        </Link>
      </p>
    </section>
  );
}
