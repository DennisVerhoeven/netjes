import { Link } from 'netjes';
import type { ItemSummary } from '../generated/netjes.ts';

const price = (cents: number) => `€${(cents / 100).toFixed(2)}`;

export function ItemTable({ items }: { items: ItemSummary[] }) {
  return (
    <table data-bench="items">
      <thead>
        <tr>
          <th>Item</th>
          <th>Summary</th>
          <th>Tags</th>
          <th>Price</th>
        </tr>
      </thead>
      <tbody>
        {items.map((item) => (
          <tr key={item.id}>
            <td>
              <Link href={`/items/${item.id}`}>{item.name}</Link>
            </td>
            <td>{item.summary}</td>
            <td>{item.tags.join(', ')}</td>
            <td>{price(item.priceCents)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
