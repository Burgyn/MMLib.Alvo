/* ===========================================================================
   The bridge between alvo.js's keyboard map and the Blazor circuit.

   alvo.js owns the map and dispatches `alvo:*` events on the document; it knows
   nothing about Blazor, which is what lets it run before hydration. This module
   is the other half: it forwards those events into .NET and carries the two
   imperative gestures a component cannot express declaratively — moving focus,
   and reading whether the viewport is the phone one.

   It is an ES module, imported once per circuit by AdminInterop — the one .NET
   path into it — on the first call that needs it, and released with the circuit.
   A module is evaluated once per document, so the state below (the handlers, the
   scroll-lock count) is one per page however many components use it.
   =========================================================================== */

const handlers = new Map();
let nextToken = 0;

/**
 * Marks the keyboard as live.
 *
 * `window.Blazor` appears as soon as the circuit connects, which is BEFORE a component's
 * `OnAfterRenderAsync` has imported this module and subscribed. A keystroke in that window is
 * swallowed — the map is running, nothing is listening. So the palette says when it is actually
 * wired, and anything waiting on the keyboard waits on this rather than on the framework.
 *
 * It is a readiness signal, not a test hook: a person debugging "the shortcut did nothing" asks
 * exactly this question in the console.
 */
export function markKeyboardReady() {
  document.documentElement.dataset.alvoKeyboard = 'ready';
}

/**
 * Keeps the arrow keys on an entity's tab strip, and on a one-of chip group, from also scrolling
 * the page.
 *
 * Blazor cannot prevent a default for some keys and not others — `@onkeydown:preventDefault` would
 * swallow Tab and Enter too — so the strip's or the group's own handler moves the choice and this
 * stops the browser from moving the page as well. A radio group takes the vertical arrows too,
 * because its chips wrap onto several lines. Registered once, when the module is first imported —
 * which the command palette's keyboard wiring does in the admin layout, so on every screen a ChipGroup
 * can render on.
 */
const ROVING_KEYS = {
  '[role="tab"]': ['ArrowLeft', 'ArrowRight', 'Home', 'End'],
  '[role="radio"]': ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'],
};

document.addEventListener('keydown', (event) => {
  if (!(event.target instanceof Element)) {
    return;
  }

  for (const [selector, keys] of Object.entries(ROVING_KEYS)) {
    if (keys.includes(event.key) && event.target.closest(selector)) {
      event.preventDefault();
      return;
    }
  }
});

/**
 * Forwards one `alvo:<name>` document event to a .NET object's method, and answers a token.
 *
 * The token, not the event name, is what `unsubscribe` takes: two components may listen to the same
 * event, and a key built from the name would let the second replace the first — and let the first,
 * disposing, remove the second.
 */
export function subscribe(name, target, method) {
  const handler = (event) =>
    target.invokeMethodAsync(method, event.detail?.key ?? event.detail?.value ?? null);
  document.addEventListener(`alvo:${name}`, handler);
  nextToken += 1;
  handlers.set(nextToken, { name, handler });
  return nextToken;
}

/** Removes one subscription by the token `subscribe` answered. Called from the component's DisposeAsync. */
export function unsubscribe(token) {
  const subscription = handlers.get(token);
  if (subscription) {
    document.removeEventListener(`alvo:${subscription.name}`, subscription.handler);
    handlers.delete(token);
  }
}

/**
 * Moves focus to the selected row inside a container and brings it into view — `j`/`k` on the data grid.
 *
 * Read from the rendered `aria-selected` rather than passed an element: the row is whichever one .NET just
 * drew as selected, and there is one reference to hold (the table body) instead of one per row.
 */
export function focusSelected(container) {
  const row = container?.querySelector?.('[aria-selected="true"]');
  if (row instanceof HTMLElement) {
    row.focus();
    row.scrollIntoView({ block: 'nearest' });
  }
}

/**
 * Holds the page still while a sheet or the palette is over it. Without this a drag near the panel's edge
 * scrolls the list underneath and the sheet appears to float over a page that is still moving — the one
 * thing that makes a bottom sheet read as a web page rather than a control.
 *
 * Counted rather than boolean: two overlays can be open at once (a sheet over the palette), and the first to
 * close must not release the page for the second.
 */
let scrollLocks = 0;

export function lockScroll(locked) {
  scrollLocks = Math.max(0, scrollLocks + (locked ? 1 : -1));
  document.body.style.overflow = scrollLocks > 0 ? 'hidden' : '';
}

/** Selects the text in an input, so a re-opened palette replaces rather than appends. */
export function focusAndSelect(element) {
  if (element && typeof element.select === 'function') {
    element.focus();
    element.select();
  }
}

/** The stored theme, applied by alvo.js before paint; read back for the toggle's label. */
export function theme() {
  return window.alvo?.resolvedTheme() ?? 'light';
}

/** Flips the theme and answers the new one. */
export function toggleTheme() {
  return window.alvo?.toggleTheme() ?? 'light';
}

/** Flips the density and answers the new one. */
export function toggleDensity() {
  return window.alvo?.toggleDensity() ?? 'comfortable';
}

/** Downloads text as a file — the descriptor export, which has no server round trip. */
export function download(name, text) {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
