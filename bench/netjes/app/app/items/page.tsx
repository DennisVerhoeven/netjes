import { definePage } from 'netjes';
import { ItemTable } from '../../components/ItemTable.tsx';
import { pages } from '../../generated/netjes.ts';

export const metadata = { title: 'Items' };

export default definePage(pages['/items'], function Items({ items }) {
  return (
    <>
      <h1>Items</h1>
      <ItemTable items={items} />
    </>
  );
});
