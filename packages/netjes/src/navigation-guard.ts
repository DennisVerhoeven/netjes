/** How a navigation a guard holds would go: adding or replacing an entry, or through history, either way. */
export type NavigationAttemptType = 'push' | 'replace' | 'back' | 'forward';

/** A navigation `useNavigationGuard` holds: where it goes, how, and the two answers to it. */
export type NavigationAttempt = {
  /** Where it goes: the path, query and fragment, without the base path, as `usePathname()` and the routes read them. */
  to: string;
  type: NavigationAttemptType;
  /** Lets it go on: to the next guard that holds, if any, then to the page. */
  proceed(): void;
  /** Keeps the page on screen: the navigation does not happen. */
  stay(): void;
};

/** What `useNavigationGuard` takes. */
export type NavigationGuardOptions = {
  /** Whether to hold navigations now: a form with changes that are not saved, files on their way up. */
  when: boolean;
  /** Called with each navigation held, which waits until it calls `proceed()` or `stay()`. */
  onAttempt(attempt: NavigationAttempt): void;
};

type Guard = { read(): NavigationGuardOptions };

/**
 * The guards of one router: the ones that hold now, in the order they started to, and the attempt being asked about.
 * Each navigation a guard holds asks the guards in turn, the one that started to hold last first, and runs once every
 * one of them has let it proceed. A newer attempt takes the place of one still being asked about, so a guard that never
 * answers keeps no navigation from being asked again.
 */
export class NavigationGuards {
  private readonly guards: Guard[] = [];
  /** The attempt being asked about: dropping it ends it, as a stay does. */
  private asking: { drop(): void } | null = null;

  /** Whether any guard holds now. */
  get holding(): boolean {
    return this.guards.length > 0;
  }

  /** Holds navigations until the function it returns is called; `read` gives the latest options. */
  register(read: () => NavigationGuardOptions): () => void {
    const guard: Guard = { read };
    this.guards.push(guard);
    return () => {
      const at = this.guards.indexOf(guard);
      if (at >= 0) this.guards.splice(at, 1);
    };
  }

  /**
   * Runs `go` at once while no guard holds; else asks the guards that hold, newest first, and runs it once each has
   * let it proceed. Resolves with `go`'s promise, or once a guard keeps the page or a newer attempt takes its place.
   */
  attempt(to: string, type: NavigationAttemptType, go: () => Promise<void>): Promise<void> {
    if (this.guards.length === 0) return go();
    this.asking?.drop();
    return new Promise<void>((resolve) => {
      const queue = [...this.guards].reverse();
      const asking = {
        drop: () => {
          if (this.asking === asking) this.asking = null;
          resolve();
        },
      };
      this.asking = asking;
      const next = () => {
        if (this.asking !== asking) return;
        // A guard that stopped holding while an earlier one was asked has nothing to say any more.
        let guard = queue.shift();
        while (guard && !this.guards.includes(guard)) guard = queue.shift();
        if (!guard) {
          this.asking = null;
          resolve(go());
          return;
        }
        let answered = false;
        guard.read().onAttempt({
          to,
          type,
          proceed: () => {
            if (answered) return;
            answered = true;
            next();
          },
          stay: () => {
            if (answered) return;
            answered = true;
            asking.drop();
          },
        });
      };
      next();
    });
  }
}
