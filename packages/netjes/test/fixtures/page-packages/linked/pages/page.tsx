import { Link } from 'netjes';
import { z } from 'zod';
import { Badge, marker } from '../lib/index.ts';

const Greeting = z.string().min(1);

export default function LinkedHome() {
  return (
    <div>
      <p>{Greeting.parse('Linked home')} {marker.id}</p>
      <Badge />
      <Link href="/">Home</Link>
    </div>
  );
}
