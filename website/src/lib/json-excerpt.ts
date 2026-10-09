// The subtree of a JSON document at an RFC 6901 pointer, cut out of the source text rather than
// re-serialised: the snippet's own line breaks survive, so a descriptor laid out to fit a narrow pane
// stays laid out that way. An object member is shown with its key ("tickets": { … }); an array
// element is shown alone. A pointer that resolves to nothing throws, which fails the build.

const isSpace = (c: string | undefined) => c === ' ' || c === '\t' || c === '\r' || c === '\n';

function skipSpace(s: string, i: number): number {
  while (i < s.length && isSpace(s[i])) i++;
  return i;
}

function skipString(s: string, i: number): number {
  if (s[i] !== '"') throw new Error(`expected a string at offset ${i}`);
  for (i++; i < s.length; i++) {
    if (s[i] === '\\') i++;
    else if (s[i] === '"') return i + 1;
  }
  throw new Error('unterminated string');
}

function skipValue(s: string, i: number): number {
  i = skipSpace(s, i);
  if (s[i] === '"') return skipString(s, i);
  if (s[i] === '{' || s[i] === '[') {
    let end = i;
    entries(s, i, () => false, (close) => { end = close; });
    return end + 1;
  }
  const literal = /^(?:-?\d[\d.eE+-]*|true|false|null)/.exec(s.slice(i));
  if (!literal) throw new Error(`unexpected "${s[i]}" at offset ${i}`);
  return i + literal[0].length;
}

type Visit = (key: string | number, keyStart: number, valueStart: number) => boolean;

// Walks the members of the object (or the elements of the array) that opens at `open`; `visit` returns
// true to stop. `closed` receives the offset of the closing bracket when the walk reaches it.
function entries(s: string, open: number, visit: Visit, closed: (close: number) => void = () => {}): void {
  const isObject = s[open] === '{';
  const close = isObject ? '}' : ']';
  let i = skipSpace(s, open + 1);
  if (s[i] === close) return closed(i);
  for (let index = 0; ; index++) {
    i = skipSpace(s, i);
    const keyStart = i;
    let key: string | number = index;
    if (isObject) {
      const keyEnd = skipString(s, i);
      key = JSON.parse(s.slice(i, keyEnd));
      i = skipSpace(s, keyEnd);
      if (s[i] !== ':') throw new Error(`expected ":" at offset ${i}`);
      i++;
    }
    const valueStart = skipSpace(s, i);
    if (visit(key, keyStart, valueStart)) return;
    i = skipSpace(s, skipValue(s, valueStart));
    if (s[i] === close) return closed(i);
    if (s[i] !== ',') throw new Error(`expected "," or "${close}" at offset ${i}`);
    i++;
  }
}

function tokens(pointer: string): string[] {
  if (pointer === '') return [];
  if (!pointer.startsWith('/')) throw new Error(`"${pointer}" is not a JSON pointer (RFC 6901)`);
  return pointer.slice(1).split('/').map((t) => t.replaceAll('~1', '/').replaceAll('~0', '~'));
}

function step(s: string, at: number, token: string): { keyStart: number | null; valueStart: number } | null {
  if (s[at] !== '{' && s[at] !== '[') return null;
  let found: { keyStart: number | null; valueStart: number } | null = null;
  const isObject = s[at] === '{';
  entries(s, at, (key, keyStart, valueStart) => {
    if (String(key) !== token) return false;
    found = { keyStart: isObject ? keyStart : null, valueStart };
    return true;
  });
  return found;
}

function dedent(text: string, indent: number): string {
  const strip = new RegExp(`^ {0,${indent}}`);
  return text.split('\n').map((line, i) => (i === 0 ? line : line.replace(strip, ''))).join('\n');
}

export function jsonExcerpt(code: string, pointer: string): string {
  const s = code.replace(/\r\n/g, '\n');
  let at = skipSpace(s, 0);
  let keyStart: number | null = null;
  for (const token of tokens(pointer)) {
    const next = step(s, at, token);
    if (!next) throw new Error(`JsonExcerpt: the pointer "${pointer}" resolves to nothing (no "${token}")`);
    ({ keyStart } = next);
    at = next.valueStart;
  }
  const raw = s.slice(at, skipValue(s, at));
  JSON.parse(raw);
  const from = keyStart ?? at;
  const line = s.slice(s.lastIndexOf('\n', from - 1) + 1, from);
  const indent = line.length - line.trimStart().length;
  return dedent((keyStart === null ? '' : s.slice(keyStart, at)) + raw, indent);
}
