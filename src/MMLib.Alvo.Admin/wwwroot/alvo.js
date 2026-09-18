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

  const toggleTheme = () => {
    const next = resolvedTheme() === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    writeStored(THEME_KEY, next);
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

  const emit = (name, detail) =>
    document.dispatchEvent(new CustomEvent(`alvo:${name}`, { detail, bubbles: true }));

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

    if (isTypingTarget(document.activeElement)) {
      return;
    }

    if (awaitingGoto) {
      awaitingGoto = false;
      if (/^[a-z]$/.test(event.key)) {
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
        awaitingGoto = true;
        break;
      case '/':
        event.preventDefault();
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

  window.alvo = { toggleTheme, toggleDensity, resolvedTheme };
})();
