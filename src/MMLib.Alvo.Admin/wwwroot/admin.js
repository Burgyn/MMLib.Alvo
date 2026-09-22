/* ===========================================================================
   The bridge between alvo.js's keyboard map and the Blazor circuit.

   alvo.js owns the map and dispatches `alvo:*` events on the document; it knows
   nothing about Blazor, which is what lets it run before hydration. This module
   is the other half: it forwards those events into .NET and carries the two
   imperative gestures a component cannot express declaratively — moving focus,
   and reading whether the viewport is the phone one.

   It is an ES module, imported by the component that needs it and disposed with
   that component, so a circuit that never opens the palette never loads it.
   =========================================================================== */

const handlers = new Map();

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

/** Forwards one `alvo:<name>` document event to a .NET object's method. */
export function subscribe(name, target, method) {
  const handler = (event) => target.invokeMethodAsync(method, event.detail?.key ?? null);
  document.addEventListener(`alvo:${name}`, handler);
  handlers.set(`${name}:${method}`, handler);
}

/** Removes a subscription. Called from the component's DisposeAsync. */
export function unsubscribe(name, method) {
  const key = `${name}:${method}`;
  const handler = handlers.get(key);
  if (handler) {
    document.removeEventListener(`alvo:${name}`, handler);
    handlers.delete(key);
  }
}

/** Moves focus to an element, after the render that created it. */
export function focus(element) {
  if (element && typeof element.focus === 'function') {
    element.focus();
  }
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
