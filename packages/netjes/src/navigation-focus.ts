/** Input types the user does not type into: the focus can leave them when the page they are on is replaced. */
const NOT_TYPED_IN = new Set(['button', 'checkbox', 'color', 'file', 'hidden', 'image', 'radio', 'range', 'reset', 'submit']);

/** Whether the user types into the element, or picks a value in it from the keyboard: a text field, a select, editable content. */
export function isEditable(element: Element): boolean {
  if (element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement) return true;
  if (element instanceof HTMLInputElement) return !NOT_TYPED_IN.has(element.type);
  return element instanceof HTMLElement && element.isContentEditable;
}

/**
 * Focuses an element without scrolling to it. One that takes no focus of its own, such as a heading or `main`, gets
 * `tabindex="-1"` for it, which goes again when it loses the focus. Returns whether the element has the focus.
 */
export function focusElement(element: HTMLElement): boolean {
  element.focus({ preventScroll: true });
  if (document.activeElement === element) return true;
  // A tabindex of the app's own did not help (a hidden or inert element): leave it as it is.
  if (element.hasAttribute('tabindex')) return false;
  element.setAttribute('tabindex', '-1');
  element.focus({ preventScroll: true });
  if (document.activeElement !== element) {
    element.removeAttribute('tabindex');
    return false;
  }
  element.addEventListener('blur', () => element.removeAttribute('tabindex'), { once: true });
  return true;
}

/**
 * Starts the focus over at the top of the document, as a page load does: the next Tab goes to the first element that
 * takes the focus, such as a skip link. `body` takes the focus for this only, without scrolling, and gets its own
 * `tabindex` back, or none.
 */
export function resetFocus() {
  const body = document.body;
  const own = body.getAttribute('tabindex');
  body.tabIndex = -1;
  body.focus({ preventScroll: true, focusVisible: false });
  if (own === null) body.removeAttribute('tabindex');
  else body.setAttribute('tabindex', own);
}

export type NavigationFocus = {
  /** The element the URL's fragment names, which the router scrolled to; null without a fragment, or when none matched. */
  target: HTMLElement | null;
  /** The page on screen is another one: its page key changed. */
  replaced: boolean;
  /** The navigation keeps the scroll position: `scroll={false}`. */
  keep: boolean;
  /** What had the focus when the navigation started. */
  focusedAtStart: Element | null;
};

/**
 * Moves the focus after a navigation, once the router has scrolled, by the first of these rules that applies:
 *
 * 1. The fragment's target takes it, on this page or on another. One that cannot (a hidden element) leaves it to the
 *    rules below.
 * 2. It stays on the same page: a query that changed, a refresh, a post that stays on its page, a modal opening or
 *    closing over the page under it.
 * 3. It stays where it went while the navigation ran: to the new page (`autoFocus`), or where the user or a handler
 *    of the app (a menu that closes) put it.
 * 4. It stays on what had it when the navigation started, with `scroll={false}` or in a field the user types into,
 *    such as a search box in a layout that navigates as the user types.
 * 5. Otherwise it starts over at the top of the document, as after a page load.
 */
export function moveFocusAfterNavigation({ target, replaced, keep, focusedAtStart }: NavigationFocus) {
  if (target && focusElement(target)) return;
  if (!replaced) return;
  const focused = document.activeElement;
  if (focused && focused !== document.body && focused !== focusedAtStart) return;
  if (focused && focused === focusedAtStart && (keep || isEditable(focused))) return;
  resetFocus();
}
