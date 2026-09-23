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

    switch (event.key) {
      case 'j':
        event.preventDefault();
        emit('move', { by: 1 });
        break;
      case 'k':
        event.preventDefault();
        emit('move', { by: -1 });
        break;
      case 'g':
        awaitingGoto = !underModal;
        break;
      case '/':
        event.preventDefault();
        /* Focused here rather than from .NET: a screen's search box is plain markup, and moving
           focus into it needs no round trip over the circuit — nor a public method on the page. */
        document.querySelector('[data-alvo-search]')?.focus();
        emit('search');
        break;
      case 'Enter':
        emit('open');
        break;
      default:
        break;
    }
  };

  applyStored();
  document.addEventListener('keydown', onKeyDown);

  /* Holds the page still while a sheet is over it. Without this a drag near the panel's edge
     scrolls the list underneath and the sheet appears to float over a page that is still
     moving — the one thing that makes a bottom sheet read as a web page rather than a control.
     Counted rather than boolean: two overlays can be open at once (a sheet over the palette),
     and the first to close must not release the page for the second. */
  let scrollLocks = 0;

  const lockScroll = (locked) => {
    scrollLocks = Math.max(0, scrollLocks + (locked ? 1 : -1));
    document.body.style.overflow = scrollLocks > 0 ? 'hidden' : '';
  };

  window.alvo = { toggleTheme, toggleDensity, resolvedTheme, lockScroll };
})();
