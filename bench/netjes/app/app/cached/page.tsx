import { definePage } from 'netjes';
import { ItemTable } from '../../components/ItemTable.tsx';
import { pages } from '../../generated/netjes.ts';

export const metadata = { title: 'Items (cached)' };

/** The list page again, output-cached by CachePage in C#. */
export default definePage(pages['/cached'], function CachedItems({ items }) {
  return (
    <>
      <h1>Items (cached)</h1>
      <ItemTable items={items} />
    </>
  );
});
