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
 * Marks the shell as rendered over the circuit, with the component library's providers mounted.
 *
 * The keyboard mark says the palette is listening; this one says a popover or a dialog can open. They are two
 * marks because they are two components' first renders, and a scenario that waited on the first and then opened a
 * select raced the second.
 */
export function markShellReady() {
  document.documentElement.dataset.alvoShell = 'ready';
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
 * A snackbar is announced by the region it appears in, not by itself (spec §3.3; batch-B re-review N2). The dashboard's
 * provider (`.a-snackbars`) is the polite live region, on the page before any message, which is what a screen reader
 * needs to announce what is added to it. The library writes `role="alert"` and `aria-live` on each snackbar as well,
 * which would make it a second region, assertive and inserted with its content; both are taken off as it is added,
 * so the one region reads it once. No error is ever a snackbar, so nothing here needed to interrupt.
 */
const quietSnackbar = (element) => {
  if (!(element instanceof Element) || !element.closest('.a-snackbars')) {
    return;
  }

  for (const bar of [element, ...element.querySelectorAll('[role="alert"]')]) {
    if (bar.getAttribute('role') === 'alert') {
      bar.removeAttribute('role');
      bar.removeAttribute('aria-live');
    }
  }
};

new MutationObserver((records) => {
  for (const record of records) {
    record.addedNodes.forEach(quietSnackbar);
  }
}).observe(document.body, { childList: true, subtree: true });

/**
 * The set-password page (design §1.3): its link carries the address and the credential token in the fragment,
 * which no browser sends to the server, to a proxy's log or in `Referer`. This fills the two boxes from it, makes
 * the address read-only and hides the token's box, and then takes the fragment out of the address bar and the
 * history, so the bearer credential does not stay in either. It runs once, when the page loads this module; on any
 * other page there is no such form and it does nothing.
 *
 * `URLSearchParams` reads a `+` as a space, and the dashboard escapes every value with `Uri.EscapeDataString`, so
 * a `+` in the token arrives as `%2B` and comes back intact. The endpoint also turns a stray space back into `+`,
 * for a token that was pasted or re-encoded on the way.
 *
 * Also the one client-side hint the form has: two passwords that differ are said at the second box before the
 * post, in the endpoint's own words. The endpoint checks again, because this runs only when JavaScript does.
 */
const fillSetPasswordForm = () => {
  const form = document.querySelector('form[data-alvo-set-password]');
  if (!form) {
    return;
  }

  const values = new URLSearchParams(window.location.hash.slice(1));
  const email = values.get('email');
  const token = values.get('token');
  if (email && token) {
    form.elements.email.value = email;
    form.elements.email.readOnly = true;
    form.elements.token.value = token;
    form.querySelector('[data-alvo-token-field]')?.setAttribute('hidden', '');
    form.elements.password.focus();
  }

  if (window.location.hash) {
    history.replaceState(history.state, '', window.location.pathname + window.location.search);
  }

  const { password, repeat } = form.elements;
  const hint = () =>
    repeat.setCustomValidity(repeat.value && repeat.value !== password.value ? 'The two passwords are not the same.' : '');
  password.addEventListener('input', hint);
  repeat.addEventListener('input', hint);
};

fillSetPasswordForm();

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
 * Focuses the first of the selectors that names an element on the page which can take focus: where focus goes after
 * a confirm or an editor closed (spec §3.2). The trigger comes first; when it is gone (the item was removed, the
 * editor that held it closed), the row that took the item's place, then the list's own create action. Never <body>:
 * a keyboard user's next Tab would start again from the top of the page.
 */
export function focusFirst(selectors) {
  for (const selector of selectors ?? []) {
    for (const element of document.querySelectorAll(selector)) {
      if (element instanceof HTMLElement && !element.matches(':disabled') && element.getClientRects().length > 0) {
        /* Tried rather than judged: an element that cannot take focus leaves it where it was, and the next is asked. */
        element.focus();
        if (document.activeElement === element) {
          return true;
        }
      }
    }
  }

  return false;
}

/**
 * `focusFirst`, as soon as one of the selectors names something that can take focus: inside a dialog, the library
 * draws the content a render after the component that owns it, so what was asked for is not on screen yet when the
 * owner's render completes. Polled on a timer and given up after two seconds, like `focusFirstOnceClosed`.
 */
export function focusFirstOnceShown(selectors) {
  const deadline = Date.now() + 2000;
  const attempt = () => {
    if (!focusFirst(selectors) && Date.now() < deadline) {
      setTimeout(attempt, 20);
    }
  };
  attempt();
}

/**
 * `focusFirst`, once no dialog is over the page: the order spec §3.2 names, where the confirm closes, the result is
 * drawn, and only then does focus move. The call arrives before the render that closes the confirm, and the library
 * gives focus back to whatever had it as the dialog goes, so moving focus any earlier would be undone. Polled on a
 * timer, not an animation frame, which a page in the background does not get; given up after two seconds, so a dialog
 * that stays open (another one opened) does not have focus pulled out from under it.
 */
export function focusFirstOnceClosed(selectors) {
  const deadline = Date.now() + 2000;
  const attempt = () => {
    if (document.querySelector('[aria-modal="true"]') === null) {
      focusFirst(selectors);
    } else if (Date.now() < deadline) {
      setTimeout(attempt, 20);
    }
  };
  attempt();
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
  /* The shell's content pane is what scrolls, and the body under a bare page such as sign-in. */
  document.body.style.overflow = scrollLocks > 0 ? 'hidden' : '';
  document.documentElement.toggleAttribute('data-scroll-locked', scrollLocks > 0);
}

/** The threads a follow is already asked for, to run on the next frame. */
const followPending = new WeakSet();

/**
 * Keeps a growing list's newest item in view, unless the operator has scrolled up to read something older; see
 * `follow`. Coalesced to one per frame (final review M16): a streamed answer redraws the thread for every chunk, and
 * each redraw asks. A page that is not drawn gets no frames, so it gets a timer instead.
 */
export function followNewest(element) {
  if (!(element instanceof HTMLElement) || followPending.has(element)) {
    return;
  }

  followPending.add(element);
  const run = () => {
    followPending.delete(element);
    follow(element);
  };
  if (document.visibilityState === 'visible') {
    requestAnimationFrame(run);
  } else {
    setTimeout(run, 16);
  }
}

/**
 * "Scrolled up" is remembered from the operator's own scrolling, not measured after the list grew: once the new
 * turn is in, every list is "not at the bottom", and a check made then would never follow.
 */
function follow(element) {
  if (!element.dataset.alvoFollow) {
    element.dataset.alvoFollow = 'on';
    element.addEventListener('scroll', () => {
      const gap = element.scrollHeight - element.scrollTop - element.clientHeight;
      element.dataset.alvoFollow = gap < 48 ? 'on' : 'off';
    }, { passive: true });
  }

  if (element.dataset.alvoFollow === 'on') {
    element.scrollTop = element.scrollHeight;
  }

  /* The height it last decided on, whether it followed or not: a list that grew and a thread that did not follow
     look alike from outside until this says the decision was made on the grown one. */
  element.dataset.alvoFollowedAt = String(element.scrollHeight);
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

/**
 * Copies text to the clipboard: the credential token, which is shown once and has to leave this page intact.
 * A snackbar is never its only copy (spec §3.3); this is the operator's second one.
 */
export async function copyText(text) {
  await navigator.clipboard.writeText(text);
}

/**
 * The browser's offset from UTC, in minutes east: what a time the server renders needs to read in the operator's own
 * zone, since the server's is nobody's. The sign is flipped from getTimezoneOffset, which counts minutes west.
 */
export function utcOffsetMinutes() {
  return -new Date().getTimezoneOffset();
}

/**
 * The caret of a text box as [start, end] — kept by the input after a button takes focus — or null when the box is
 * not on the page. Insert writes a function call there (spec §9.2).
 */
export function caret(id) {
  const box = document.getElementById(id);
  return box && typeof box.selectionStart === 'number' ? [box.selectionStart, box.selectionEnd] : null;
}

/**
 * Focuses a text box and selects a range in it: the placeholder an inserted call asks the operator to replace. Only
 * once the box holds `text`, the value the range was computed in — the sheet is drawn by the dialog provider, so the
 * render that wrote the value may reach the page after this call. Polled on a timer like `focusFirstOnceClosed`, and
 * given up after two seconds: a box that never holds the text (the operator typed on) is left as it is.
 */
export function selectRange(id, start, length, text) {
  const deadline = Date.now() + 2000;
  const attempt = () => {
    const box = document.getElementById(id);
    if (box && box.value === text) {
      box.focus();
      box.setSelectionRange(start, start + length);
    } else if (Date.now() < deadline) {
      setTimeout(attempt, 20);
    }
  };
  attempt();
}
