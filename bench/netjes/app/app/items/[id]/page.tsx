import { definePage } from 'netjes';
import { Suspense, use } from 'react';
import { type Stock, pages } from '../../../generated/netjes.ts';

function StockLine({ stock }: { stock: PromiseLike<Stock> }) {
  const { available, warehouse } = use(stock);
  return <p data-bench="stock">{`${available} in stock in ${warehouse}`}</p>;
}

/** Sent before the stock lookup (Page.Defer in C#) is done; the stock line streams in after it. */
export default definePage(pages['/items/[id]'], function ItemPage({ item, stock }) {
  return (
    <article>
      <h1>{item.name}</h1>
      {item.description.map((paragraph) => (
        <p key={paragraph}>{paragraph}</p>
      ))}
      <p>{item.tags.join(', ')}</p>
      <Suspense fallback={<p>Checking stock…</p>}>
        <StockLine stock={stock} />
      </Suspense>
    </article>
  );
});
