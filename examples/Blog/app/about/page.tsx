import type { Metadata } from 'netjes';

export const metadata: Metadata = { title: 'About' };

// No loader is mapped for this page: it renders with empty props.
export default function About() {
  return (
    <article className="prose">
      <h1>About</h1>
      <p>
        This site is the example app for Netjes. It has no Node web server: ASP.NET Core receives every request, runs the
        C# loader for the page, and asks a small Node sidecar to render the React tree to HTML.
      </p>
      <p>
        After the first load the browser takes over. Links fetch the next page's props as JSON and React swaps the page in
        place, keeping layouts (and their state) mounted.
      </p>
    </article>
  );
}
