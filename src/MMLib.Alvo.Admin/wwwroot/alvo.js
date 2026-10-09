/* ===========================================================================
   Alvo Admin — the browser concerns the design system owns.

   No framework. Blazor owns rendering and state; this owns only what has to
   exist before hydration (the theme) or below it (the keyboard map).
   =========================================================================== */

(() => {
  'use strict';

  const THEME_KEY = 'alvo.theme';
  const DENSITY_KEY = 'alvo.density';

  /* --- Theme -------------------------------------------------------------
     Applied synchronously, before paint, and ALWAYS resolved to light or dark.
     The component library's dark palette is scoped to [data-theme=dark] (see
     AlvoTheme.razor), and a scoped variable cannot follow prefers-color-scheme on
     its own, so a viewer who never chose gets the system's answer written down.
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

  const DARK = '(prefers-color-scheme: dark)';

  const systemTheme = () => (window.matchMedia(DARK).matches ? 'dark' : 'light');

  const storedTheme = () => {
    const theme = readStored(THEME_KEY);
    return theme === 'light' || theme === 'dark' ? theme : null;
  };

  const applyStored = () => {
    document.documentElement.dataset.theme = storedTheme() ?? systemTheme();

    const density = readStored(DENSITY_KEY);
    if (density === 'comfortable' || density === 'compact') {
      document.documentElement.dataset.density = density;
    }
  };

  /* Nothing stored means "follow the system", and the system can change under an open page. */
  const followSystem = () =>
    window.matchMedia(DARK).addEventListener('change', () => {
      if (storedTheme() === null) {
        document.documentElement.dataset.theme = systemTheme();
        emit('theme', { value: document.documentElement.dataset.theme });
      }
    });

  const resolvedTheme = () => document.documentElement.dataset.theme ?? systemTheme();

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
    '[role="menuitem"], [role="separator"]';

  const isControl = (element) => element instanceof Element && element.closest(CONTROL) !== null;

  function emit(name, detail) {
    document.dispatchEvent(new CustomEvent(`alvo:${name}`, { detail, bubbles: true }));
  }

  /* The keys that may follow `g`: the sections' letters, and the comma Settings takes from the
     convention of ⌘, for preferences. Which key reaches which section is AdminNavigation's to say. */
  const GOTO_KEY = /^[a-z,]$/;

  let awaitingGoto = false;

  /* A dialog or a sheet is over the page. */
  const underModal = () => document.querySelector('[aria-modal="true"]') !== null;

  /* --- A menu item that is a form post -----------------------------------
     The library's menu item is a div it focuses and moves between with the
     arrows, not a button, so it cannot submit the form around it. The sign-out
     must stay a real post (a GET sign-out is triggerable by an <img>), so a
     click, an Enter or a Space on [data-alvo-submits] submits its form here,
     once: the library may answer the same key with a click of its own.
     ---------------------------------------------------------------------- */
  const submitterOf = (target) => (target instanceof Element ? target.closest('[data-alvo-submits]') : null);

  const submitFrom = (item) => {
    const form = item.closest('form');
    if (form && form.dataset.alvoSubmitted === undefined) {
      form.dataset.alvoSubmitted = '';
      form.requestSubmit();
    }
  };

  const onSubmitterClick = (event) => {
    const item = submitterOf(event.target);
    if (item) {
      submitFrom(item);
    }
  };

  /* --- Escape in an editor ----------------------------------------------
     Presses the control that answers it: Cancel, or Keep editing while
     "Discard your changes?" is asked. The library's dialog keeps Escape for
     its own close and never hands it on, and an editor holding unsaved
     changes must ask rather than close. From anywhere in the dialog, its
     frame included, which is where a click on its whitespace leaves focus.
     An open list answers Escape itself, so the editor does not.
     ---------------------------------------------------------------------- */
  const answerEscapeInDialog = (event) => {
    const target = event.target;
    if (event.isComposing || event.defaultPrevented || !(target instanceof Element)
      || target.matches('[aria-expanded="true"]')) {
      return;
    }

    /* The last one: an editor draws one answer at a time (Cancel, or Keep editing over it), and were a
       question ever drawn beside the actions it is drawn after them, as the topmost thing asked. */
    const answers = target.closest('[role="dialog"]')?.querySelectorAll('[data-alvo-escape]') ?? [];
    if (answers.length > 0) {
      answers[answers.length - 1].click();
    }
  };

  const onKeyDown = (event) => {
    /* ⌘K opens the palette, but never over another dialog (spec §3.1: never a dialog over a dialog), and a
       second ⌘K over the palette itself does nothing, so what was typed there stays. */
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      if (!underModal()) {
        emit('palette');
      }
      return;
    }

    if ((event.key === 'Enter' || event.key === ' ') && submitterOf(event.target)) {
      event.preventDefault();
      submitFrom(submitterOf(event.target));
      return;
    }

    /* Ctrl/Cmd+Enter submits the form it is typed in, exactly once: an editor, the assistant's
       question, the import, a rule box. The one mechanism for every multi-line box (spec §3.4),
       so none of them carries a keydown handler of its own. The browser already submits a form on
       Enter in a single-line field, modifier or not. A server-side keydown handler as well would
       make that one chord two submits, and the second would land after an owner that saves
       synchronously had reopened its gate. So the default is cancelled and this is the only path.
       A textarea, where Enter is a newline, gets the same submit. */
    if (event.key === 'Enter' && !event.isComposing && (event.metaKey || event.ctrlKey)
      && event.target instanceof Element) {
      const form = event.target.closest('form[data-alvo-chord-submit]');
      if (form) {
        event.preventDefault();
        form.requestSubmit();
        return;
      }
    }

    /* Enter in an open combobox chooses the option it points at. Inside an editor form it would
       also be the browser's Enter-submit, and the record would be saved on the choice. With the
       list open and nothing highlighted, Enter does nothing at all: the operator is choosing, and a
       save then would be the surprise this guard exists to prevent. An IME's Enter commits the
       composition and is the IME's. */
    if (event.key === 'Enter' && !event.isComposing && event.target instanceof Element
      && event.target.matches('[role="combobox"][aria-expanded="true"]') && event.target.closest('form')) {
      event.preventDefault();
      return;
    }

    if (event.key === 'Escape') {
      awaitingGoto = false;
      answerEscapeInDialog(event);
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
    const modal = underModal();

    if (awaitingGoto) {
      awaitingGoto = false;
      if (modal) {
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
    if (modal) {
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

  /* --- The split's reading pane ------------------------------------------
     A master–detail screen's aside is as wide as the operator drags it. One
     width for every split, applied before paint like the theme so a revision's
     descriptor does not open narrow and then jump. The stylesheet clamps it; this
     only stores what was chosen. Wired by delegation, so a handle Blazor renders
     later needs nothing registered.
     ---------------------------------------------------------------------- */

  const ASIDE_KEY = 'alvo.aside';
  const ASIDE_STEP = 24;

  const setAside = (width) => {
    const px = Math.round(width);
    document.documentElement.style.setProperty('--a-aside-w', `${px}px`);
    writeStored(ASIDE_KEY, String(px));
  };

  const applyStoredAside = () => {
    const stored = Number(readStored(ASIDE_KEY));
    if (Number.isFinite(stored) && stored > 0) {
      document.documentElement.style.setProperty('--a-aside-w', `${stored}px`);
    }
  };

  const asideOf = (handle) => handle.parentElement?.querySelector(':scope > .a-split__aside');

  /* What the pane actually is after the clamp, which is what the separator reports as its value. */
  const syncValue = (handle) => {
    const aside = asideOf(handle);
    if (aside) {
      handle.setAttribute('aria-valuenow', String(Math.round(aside.getBoundingClientRect().width)));
    }
  };

  const onHandleDown = (event) => {
    const handle = event.target instanceof Element ? event.target.closest('.a-split__handle') : null;
    if (!handle || event.button !== 0) {
      return;
    }

    event.preventDefault();
    handle.setPointerCapture(event.pointerId);
    document.documentElement.dataset.resizing = '';
    const right = handle.parentElement.getBoundingClientRect().right;

    const move = (e) => setAside(right - e.clientX);
    const up = () => {
      delete document.documentElement.dataset.resizing;
      handle.removeEventListener('pointermove', move);
      syncValue(handle);
    };

    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up, { once: true });
    handle.addEventListener('pointercancel', up, { once: true });
  };

  /* Left widens the pane — the handle moves the way the arrow points — and a double-click or Enter
     gives the stored width back to the stylesheet's default. */
  const onHandleKey = (event) => {
    const handle = event.target instanceof Element ? event.target.closest('.a-split__handle') : null;
    const aside = handle && asideOf(handle);
    if (!aside) {
      return;
    }

    const width = aside.getBoundingClientRect().width;
    const next = { ArrowLeft: width + ASIDE_STEP, ArrowRight: width - ASIDE_STEP }[event.key];
    if (next !== undefined) {
      event.preventDefault();
      setAside(next);
      syncValue(handle);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      resetAside(handle);
    }
  };

  const resetAside = (handle) => {
    document.documentElement.style.removeProperty('--a-aside-w');
    try {
      localStorage.removeItem(ASIDE_KEY);
    } catch {
      /* Nothing stored to forget. */
    }
    syncValue(handle);
  };

  const onHandleDoubleClick = (event) => {
    const handle = event.target instanceof Element ? event.target.closest('.a-split__handle') : null;
    if (handle) {
      resetAside(handle);
    }
  };

  const onHandleFocus = (event) => {
    if (event.target instanceof Element && event.target.matches('.a-split__handle')) {
      syncValue(event.target);
    }
  };

  /* --- The import box --------------------------------------------------
     A server-interactive box sends its whole text to the circuit on every
     input, and the circuit's hub takes 32 KB a message (#316). A box that
     carries data-alvo-streamed therefore never sends its text over the
     circuit: its input and change events are stopped here, on the window in
     the capture phase, ahead of the framework's own listener on the document,
     and the circuit hears only alvo:measured, "<lines> <1 when it holds more
     than whitespace, else 0>". The text travels once, on submit, as a stream
     the page asks for (streamOf), which Blazor carries in chunks.

     A box that also carries data-alvo-max-chars refuses an input over it:
     the box gets back its last text within the ceiling, and alvo:oversized
     carries the paste's character count to the screen, which draws the
     refusal. A submit over it is refused the same way, and streams nothing. One UTF-16 unit is at most three UTF-8 bytes, so the character
     ceiling bounds the stream's bytes too (ImportLimit says why).
     ---------------------------------------------------------------------- */

  const withinCeiling = new WeakMap();

  const isStreamed = (target) =>
    (target instanceof HTMLTextAreaElement || target instanceof HTMLInputElement) && target.dataset.alvoStreamed !== undefined;

  const maxCharsOf = (box) => (box.dataset.alvoMaxChars === undefined ? Infinity : Number(box.dataset.alvoMaxChars));

  const lineCount = (text) => {
    if (text.length === 0) {
      return 0;
    }

    let lines = 1;
    for (let at = text.indexOf('\n'); at !== -1; at = text.indexOf('\n', at + 1)) {
      lines += 1;
    }

    return lines;
  };

  const measureBox = (box) => `${lineCount(box.value)} ${/\S/.test(box.value) ? 1 : 0}`;

  const refuseOversized = (length) => emit('oversized', { value: String(length) });

  const onBoxFocus = (event) => {
    if (isStreamed(event.target) && !withinCeiling.has(event.target)) {
      withinCeiling.set(event.target, event.target.value);
    }
  };

  const guardStreamedBox = (event) => {
    const box = event.target;
    if (!isStreamed(box)) {
      return;
    }

    event.stopImmediatePropagation();
    if (box.value.length > maxCharsOf(box)) {
      const length = box.value.length;
      box.value = withinCeiling.get(box) ?? '';
      refuseOversized(length);
      return;
    }

    withinCeiling.set(box, box.value);
    emit('measured', { value: measureBox(box) });
  };

  const streamedBox = (id) => {
    const box = document.getElementById(id);
    return isStreamed(box) ? box : null;
  };

  /* What the page reads once its subscription is up, so an input made before it was is not missed. */
  const measureOf = (id) => {
    const box = streamedBox(id);
    return box ? measureBox(box) : null;
  };

  /* The box's text as it is now, for the page to read as a stream (the framework wraps what this answers in a stream
     reference). Empty for a box that is gone, or over its ceiling, which is refused here instead: the page reads an empty
     text as nothing to import. */
  const streamOf = (id) => {
    const box = streamedBox(id);
    if (box && box.value.length > maxCharsOf(box)) {
      refuseOversized(box.value.length);
    }

    const text = box && box.value.length <= maxCharsOf(box) ? box.value : '';
    return new Blob([text], { type: 'text/plain;charset=utf-8' });
  };

  /* --- Tab strip names ---------------------------------------------------
     MudTabs puts its own attributes on its outer frame and draws role=tablist a
     level inside, where no parameter reaches, so the strip would lose the name
     the WAI-ARIA tabs pattern gives it ("work_orders sections"). The frame's
     TablistName component calls this once after it renders.
     ---------------------------------------------------------------------- */

  const nameTablist = (frame, label) => {
    document.querySelector(frame)?.querySelector('[role="tablist"]')?.setAttribute('aria-label', label);
  };

  applyStored();
  followSystem();
  applyStoredAside();
  document.addEventListener('keydown', onKeyDown);
  document.addEventListener('click', onSubmitterClick);
  document.addEventListener('pointerdown', onHandleDown);
  document.addEventListener('keydown', onHandleKey);
  document.addEventListener('dblclick', onHandleDoubleClick);
  document.addEventListener('focusin', onHandleFocus);
  window.addEventListener('focusin', onBoxFocus, true);
  window.addEventListener('input', guardStreamedBox, true);
  window.addEventListener('change', guardStreamedBox, true);

  window.alvo = { toggleTheme, toggleDensity, resolvedTheme, nameTablist, measureOf, streamOf };
})();
