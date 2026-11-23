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
 *
 * While any guard holds, `beforeunload` has the browser ask its own question before the document unloads: a reload,
 * closing the tab, typing an address, a link to another site. The listener is there only then, as browsers keep no
 * page with one in their back-forward cache.
 */
export class NavigationGuards {
  private readonly guards: Guard[] = [];
  /** The attempt being asked about: dropping it ends it, as a stay does. */
  private asking: { drop(): void } | null = null;
  /** The next unload is a full page load the guards let through: the browser does not ask about it again. */
  private unloadLetThrough = false;

  private readonly onBeforeUnload = (event: BeforeUnloadEvent) => {
    if (this.unloadLetThrough) {
      this.unloadLetThrough = false;
      return;
    }
    event.preventDefault();
    // What browsers from before preventDefault() asked for.
    event.returnValue = true;
  };

  /** Whether any guard holds now. */
  get holding(): boolean {
    return this.guards.length > 0;
  }

  /** Holds navigations until the function it returns is called; `read` gives the latest options. */
  register(read: () => NavigationGuardOptions): () => void {
    const guard: Guard = { read };
    this.guards.push(guard);
    if (this.guards.length === 1) {
      this.unloadLetThrough = false;
      window.addEventListener('beforeunload', this.onBeforeUnload);
    }
    return () => {
      const at = this.guards.indexOf(guard);
      if (at < 0) return;
      this.guards.splice(at, 1);
      if (this.guards.length === 0) window.removeEventListener('beforeunload', this.onBeforeUnload);
    };
  }

  /**
   * A navigation the guards let through is about to load a document (the router hands it to the browser): the
   * `beforeunload` that follows does not ask again. Only that one.
   */
  letUnloadThrough(): void {
    if (this.guards.length > 0) this.unloadLetThrough = true;
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
