import type { ReactNode } from 'react';

export default function LinkedLayout({ children }: { children: ReactNode }) {
  return <section data-package="linked">{children}</section>;
}
