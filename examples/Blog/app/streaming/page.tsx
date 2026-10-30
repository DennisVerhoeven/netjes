import { definePage } from 'netjes';
import { Suspense, use, useState } from 'react';
import { type SlowReport, pages } from '../../generated/netjes.ts';

export const metadata = { title: 'Streaming' };

function Report({ report }: { report: PromiseLike<SlowReport> }) {
  const { items, generatedBy } = use(report);
  return <p role="status">{`Report ready: ${items} items, from ${generatedBy}.`}</p>;
}

/** Renders at once; the report (Page.Defer in C#) streams in later. The counter works before it arrives. */
export default definePage(pages['/streaming'], function StreamingPage({ title, report }) {
  const [clicks, setClicks] = useState(0);
  return (
    <article className="prose">
      <h1>{title}</h1>
      <p>This page was sent before its slow part was ready. Try the button while the report is still on its way.</p>
      <button type="button" className="button" onClick={() => setClicks((c) => c + 1)}>
        {`clicks ${clicks}`}
      </button>
      <Suspense fallback={<p>Preparing the slow report…</p>}>
        <Report report={report} />
      </Suspense>
    </article>
  );
});
