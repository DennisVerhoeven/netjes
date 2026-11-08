import { Link } from 'netjes';
import { marker } from '../src/index.ts';

export default function InstalledHome() {
  return (
    <div className="text-installed bg-[#0a1b2c]">
      <p>Installed home {marker.id}</p>
      <Link href="/">Home</Link>
    </div>
  );
}
