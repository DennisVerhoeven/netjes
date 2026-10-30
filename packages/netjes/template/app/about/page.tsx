import type { Metadata } from 'netjes';

export const metadata: Metadata = { title: 'About' };

// No loader is mapped for this page, so it renders with empty props.
export default function About() {
  return (
    <article className="space-y-4">
      <h1 className="text-3xl font-bold tracking-tight">About</h1>
      <p className="text-slate-600">
        Pages are React components in <code>app/</code>, as in the Next.js App Router. ASP.NET Core routes every
        request, runs the page's C# loader and renders the page on the server; after that, links navigate in the
        browser, and forms call C# server actions.
      </p>
    </article>
  );
}
