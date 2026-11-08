import type { ReactNode } from 'react';

export default function InstalledLayout({ children }: { children: ReactNode }) {
  return <section data-package="installed">{children}</section>;
}
