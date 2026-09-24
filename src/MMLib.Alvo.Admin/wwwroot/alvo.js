/* ===========================================================================
   Alvo Admin — the three browser concerns the design system owns.

   No framework. Blazor owns rendering and state; this owns only what has to
   exist before hydration (the theme) or below it (the keyboard map).
   =========================================================================== */

(() => {
  'use strict';

  const THEME_KEY = 'alvo.theme';
  const DENSITY_KEY = 'alvo.density';

  /* --- Theme -------------------------------------------------------------
     Applied from storage synchronously, before paint. The stylesheet's
     color-scheme carries the system preference on its own, so a viewer who has
     never chosen gets the right theme with nothing stored and nothing to flash.
     ---------------------------------------------------------------------- */

  const readStored = (key) => {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  };

  const writeStored = (key, value) => {
    try {
      localStorage.setItem(key, value);
    } catch {
      /* Private browsing, blocked site data — the page must work regardless. */
    }
  };

  const applyStored = () => {
    const theme = readStored(THEME_KEY);
    if (theme === 'light' || theme === 'dark') {
      document.documentElement.dataset.theme = theme;
    }

    const density = readStored(DENSITY_KEY);
    if (density === 'comfortable' || density === 'compact') {
      document.documentElement.dataset.density = density;
    }
  };

  const resolvedTheme = () =>
    document.documentElement.dataset.theme ??
    (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');

  /* Announced as well as returned: the palette can flip the theme too, and the header's toggle has
     to redraw its icon for a flip it did not make. `emit` is a hoisted function below. */
  const toggleTheme = () => {
    const next = resolvedTheme() === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    writeStored(THEME_KEY, next);
    emit('theme', { value: next });
    return next;
  };

  const toggleDensity = () => {
    const next =
      document.documentElement.dataset.density === 'comfortable' ? 'compact' : 'comfortable';
    document.documentElement.dataset.density = next;
    writeStored(DENSITY_KEY, next);
    return next;
  };

  /* --- Keyboard ----------------------------------------------------------
     An admin tool is operated by people who live in it. The map is small and
     it is the whole map: anything else belongs to the component that owns it.
     ---------------------------------------------------------------------- */

  const isTypingTarget = (element) =>
    element instanceof HTMLElement &&
    (element.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(element.tagName));

  /* What Enter already means something to. Enter on a button presses it and on a link follows it; the
     grid's `open` is for Enter on a selected row, which is none of these, and must not fire as well. */
  const CONTROL = 'a[href], button, input, select, textarea, summary, [contenteditable], [role="button"], ' +
    '[role="link"], [role="tab"], [role="radio"], [role="option"], [role="checkbox"], [role="switch"], ' +
    '[role="menuitem"]';

  const isControl = (element) => element instanceof Element && element.closest(CONTROL) !== null;

  function emit(name, detail) {
    document.dispatchEvent(new CustomEvent(`alvo:${name}`, { detail, bubbles: true }));
  }

  /* The keys that may follow `g`: the sections' letters, and the comma Settings takes from the
     convention of ⌘, for preferences. Which key reaches which section is AdminNavigation's to say. */
  const GOTO_KEY = /^[a-z,]$/;

  let awaitingGoto = false;

  const onKeyDown = (event) => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      emit('palette');
      return;
    }

    if (event.key === 'Escape') {
      awaitingGoto = false;
      emit('dismiss');
      return;
    }

    /* A chord with a modifier belongs to the browser or the operating system — ⌥D, ⌘R — and a
       half-typed `g` must not turn the next one into a jump. */
    if (isTypingTarget(document.activeElement) || event.metaKey || event.ctrlKey || event.altKey) {
      awaitingGoto = false;
      return;
    }

    /* Nor while a dialog or a sheet is over the page: a jump would navigate out from under it, and
       whatever was half-done in it would be lost to a stray `g`. */
    const underModal = document.querySelector('[aria-modal="true"]') !== null;

    if (awaitingGoto) {
      awaitingGoto = false;
      if (underModal) {
        return;
      }

      if (GOTO_KEY.test(event.key)) {
        event.preventDefault();
        emit('goto', { key: event.key });
      }

      return;
    }

    /* The page's own keys are the page's, not the dialog's: `j` inside the record sheet must not move
       the selection behind it, and Enter there must not open a second record over the first. */
    if (underModal) {
      return;
    }

    switch (event.key) {
      case 'j':
        event.preventDefault();
        emit('move', { value: 'next' });
        break;
      case 'k':
        event.preventDefault();
        emit('move', { value: 'previous' });
        break;
      case 'g':
        awaitingGoto = true;
        break;
      case '/':
        event.preventDefault();
        /* Focused here rather than from .NET: a screen's search box is plain markup, and moving
           focus into it needs no round trip over the circuit — nor a public method on the page. So
           there is no `search` event: nothing on the circuit has anything to do. */
        document.querySelector('[data-alvo-search]')?.focus();
        break;
      case 'Enter':
        if (!isControl(document.activeElement)) {
          emit('open');
        }
        break;
      default:
        break;
    }
  };

  applyStored();
  document.addEventListener('keydown', onKeyDown);

  window.alvo = { toggleTheme, toggleDensity, resolvedTheme };
})();
