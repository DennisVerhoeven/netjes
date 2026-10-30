import { definePage } from 'netjes';
import { pages } from '../../../generated/netjes.ts';

/** One page for every URL under /docs. The title comes from C# (.WithMetadata) through the layouts' templates. */
export default definePage(pages['/docs/[...path]'], function DocPage({ doc }) {
  return (
    <article className="prose">
      <h1>{doc.title}</h1>
      {doc.blocks.map((block, i) =>
        block.kind === 'code' ? (
          <pre key={i}>
            <code>{block.text}</code>
          </pre>
        ) : (
          <p key={i}>{block.text}</p>
        ),
      )}
    </article>
  );
});
