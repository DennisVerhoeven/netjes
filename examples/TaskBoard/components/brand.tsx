import { type Href, Link } from 'netjes';

/** The wordmark: a signal-coloured square with three bars, a board seen from far away. */
export function Logo({ href = '/' }: { href?: Href }) {
  return (
    <Link href={href} className="inline-flex items-center gap-2.5 font-semibold tracking-tight text-ink-100">
      <span aria-hidden="true" className="grid size-6 grid-cols-3 gap-[2px] rounded-[6px] bg-signal p-[5px]">
        <span className="rounded-[1px] bg-signal-ink" />
        <span className="mt-1 rounded-[1px] bg-signal-ink/70" />
        <span className="mt-2 rounded-[1px] bg-signal-ink/40" />
      </span>
      TaskBoard
    </Link>
  );
}

/** Initials in a circle; the hue comes from the name, so people keep their colour. */
export function Avatar({ name, size = 'sm' }: { name: string; size?: 'sm' | 'md' }) {
  const initials = name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]!.toUpperCase())
    .join('');
  let hue = 0;
  for (const char of name) hue = (hue * 31 + char.charCodeAt(0)) % 360;
  return (
    <span
      title={name}
      aria-hidden="true"
      style={{ backgroundColor: `hsl(${hue} 45% 32%)` }}
      className={`inline-grid shrink-0 place-items-center rounded-full font-semibold text-ink-100 ${size === 'sm' ? 'size-6 text-[10px]' : 'size-8 text-xs'}`}
    >
      {initials || '?'}
    </span>
  );
}
