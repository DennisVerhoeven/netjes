import { usePathname } from 'netjes';

export default function NotFound() {
  return <h1>{`Nothing at ${usePathname()}`}</h1>;
}
