/**
 * Token contrast test.
 *
 * docs/05-ux-design.md §9 commits to WCAG 2.2 AA contrast in BOTH themes. A
 * commitment nobody measures is a wish, so this parses tokens.css, resolves the
 * variables per theme, and asserts the ratios.
 *
 * Deliberately dependency-free — it runs with plain `node`, which means it works
 * in CI before anyone has installed a frontend toolchain.
 *
 *   node frontend/packages/ui/contrast.test.mjs
 *
 * Exits non-zero on any failure.
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const css = readFileSync(join(here, 'tokens.css'), 'utf8');

/* ---------------------------------------------------------------- parsing -- */

/** Extract `--name: value;` declarations from a block of CSS text. */
function declarations(block) {
  const out = {};
  for (const m of block.matchAll(/(--[a-z0-9_-]+)\s*:\s*([^;]+);/gi)) {
    out[m[1]] = m[2].trim();
  }
  return out;
}

/**
 * Walk the top-level rules, returning [selector, body] pairs.
 *
 * Brace-counting rather than regex. @-blocks are skipped wholesale: the
 * prefers-color-scheme block duplicates the explicit [data-theme='dark'] rule
 * by design, so reading the explicit rule is sufficient and unambiguous.
 *
 * tokens.css declares :root more than once — the palette, then the aging and
 * status scales further down. Every one of them must be collected, or a token
 * defined in a later block reads as "unresolved" and the test lies about
 * coverage rather than failing loudly.
 */
function topLevelRules(source) {
  const rules = [];
  let i = 0;
  while (i < source.length) {
    const open = source.indexOf('{', i);
    if (open === -1) break;

    const selector = source.slice(i, open).replace(/\/\*[\s\S]*?\*\//g, '').trim();

    let depth = 0;
    let close = -1;
    for (let j = open; j < source.length; j++) {
      if (source[j] === '{') depth++;
      else if (source[j] === '}') {
        depth--;
        if (depth === 0) {
          close = j;
          break;
        }
      }
    }
    if (close === -1) throw new Error(`unbalanced braces after: ${selector}`);

    if (!selector.startsWith('@')) {
      rules.push([selector, source.slice(open + 1, close)]);
    }
    i = close + 1;
  }
  return rules;
}

const rules = topLevelRules(css);
const merge = (predicate) =>
  rules
    .filter(([selector]) => predicate(selector))
    .reduce((acc, [, body]) => ({ ...acc, ...declarations(body) }), {});

const isRoot = (s) => /(^|,)\s*:root\b/.test(s);
const base = merge((s) => isRoot(s) && !s.includes('data-theme'));
const light = { ...base, ...merge((s) => s.includes("data-theme='light'")) };
const dark = { ...base, ...merge((s) => s.includes("data-theme='dark'")) };

if (Object.keys(base).length < 50) {
  throw new Error(`parsed only ${Object.keys(base).length} base tokens — the parser is wrong`);
}

/* ------------------------------------------------------------- colour maths */

/** Resolve a token to a concrete colour, following var() chains. */
function resolve(theme, value, seen = 0) {
  if (seen > 12) throw new Error(`var() chain too deep: ${value}`);
  const v = String(value).trim();
  const varMatch = v.match(/^var\((--[a-z0-9_-]+)\)$/i);
  if (varMatch) {
    const next = theme[varMatch[1]];
    if (next === undefined) throw new Error(`unresolved token: ${varMatch[1]}`);
    return resolve(theme, next, seen + 1);
  }
  return v;
}

/** Parse #rgb, #rrggbb, or rgb(r g b / a) into {r,g,b,a} with 0-255 channels. */
function parseColor(input) {
  const s = input.trim();

  if (s.startsWith('#')) {
    const hex = s.slice(1);
    const full = hex.length === 3 ? hex.split('').map((c) => c + c).join('') : hex;
    return {
      r: parseInt(full.slice(0, 2), 16),
      g: parseInt(full.slice(2, 4), 16),
      b: parseInt(full.slice(4, 6), 16),
      a: 1,
    };
  }

  const rgb = s.match(/^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)\s*(?:\/\s*([\d.]+)\s*)?\)$/i);
  if (rgb) {
    return {
      r: Number(rgb[1]),
      g: Number(rgb[2]),
      b: Number(rgb[3]),
      a: rgb[4] === undefined ? 1 : Number(rgb[4]),
    };
  }

  throw new Error(`unparseable colour: ${input}`);
}

/** Composite a possibly-translucent colour over an opaque backdrop. */
function flatten(fg, bg) {
  if (fg.a >= 1) return fg;
  return {
    r: fg.r * fg.a + bg.r * (1 - fg.a),
    g: fg.g * fg.a + bg.g * (1 - fg.a),
    b: fg.b * fg.a + bg.b * (1 - fg.a),
    a: 1,
  };
}

/** WCAG relative luminance. */
function luminance({ r, g, b }) {
  const channel = (v) => {
    const c = v / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

/**
 * Contrast between two tokens as they will actually render.
 *
 * The background is composited over an opaque base first. Several dark-theme
 * fills are translucent tints (`rgb(34 197 94 / 0.16)`); measuring one of those
 * as if it were opaque reports the contrast of a bright green rather than the
 * dark tinted green a user sees, which understates the ratio by a factor of
 * four and would have failed perfectly good tokens.
 */
function contrast(theme, fgToken, bgToken) {
  const base = parseColor(resolve(theme, 'var(--bg-surface)'));
  const bg = flatten(parseColor(resolve(theme, `var(${bgToken})`)), base);
  const fg = flatten(parseColor(resolve(theme, `var(${fgToken})`)), bg);
  const [hi, lo] = [luminance(fg), luminance(bg)].sort((a, b) => b - a);
  return (hi + 0.05) / (lo + 0.05);
}

/* ----------------------------------------------------------------- the spec */

// [foreground, background, minimum ratio, what it is]
const CASES = [
  ['--text-primary',   '--bg-canvas',   4.5, 'body text on canvas'],
  ['--text-primary',   '--bg-surface',  4.5, 'body text on surface'],
  ['--text-primary',   '--bg-raised',   4.5, 'body text on raised surface'],
  ['--text-primary',   '--bg-inset',    4.5, 'body text on inset'],
  ['--text-secondary', '--bg-surface',  4.5, 'secondary text on surface'],
  ['--text-secondary', '--bg-canvas',   4.5, 'secondary text on canvas'],
  ['--text-tertiary',  '--bg-surface',  3.0, 'tertiary text (large/meta only)'],

  ['--text-on-accent', '--accent-bg',   4.5, 'label on the primary button'],

  // -text tokens carry meaning through TYPE, so they are held to the body-text
  // threshold on both grounds a word can land on.
  ['--success-text',   '--bg-surface',  4.5, 'success word on card'],
  ['--warning-text',   '--bg-surface',  4.5, 'warning word on card'],
  ['--danger-text',    '--bg-surface',  4.5, 'danger word on card'],
  ['--info-text',      '--bg-surface',  4.5, 'info word on card'],
  ['--success-text',   '--bg-canvas',   4.5, 'success word on canvas'],
  ['--warning-text',   '--bg-canvas',   4.5, 'warning word on canvas'],
  ['--danger-text',    '--bg-canvas',   4.5, 'danger word on canvas'],
  ['--accent-fg',      '--bg-surface',  4.5, 'link on card'],

  // -mark tokens are dots, rules, bars and icons: non-text, so 3:1 (WCAG
  // 1.4.11). This is exactly why they are a step lighter than the -text pair —
  // #16A34A and #D97706 land near 3.2:1 and would fail as body text.
  ['--success-mark',   '--bg-surface',  3.0, 'success dot on card'],
  ['--warning-mark',   '--bg-surface',  3.0, 'warning dot on card'],
  ['--danger-mark',    '--bg-surface',  3.0, 'danger dot on card'],
  ['--info-mark',      '--bg-surface',  3.0, 'info dot on card'],
  ['--success-mark',   '--bg-canvas',   3.0, 'success dot on canvas'],
  ['--warning-mark',   '--bg-canvas',   3.0, 'warning dot on canvas'],
  ['--danger-mark',    '--bg-canvas',   3.0, 'danger dot on canvas'],

  // Status dots replace the filled pills. Each must clear 3:1 on a row.
  ['--status-available-dot',    '--bg-surface', 3.0, 'status dot: available'],
  ['--status-in_recon-dot',     '--bg-surface', 3.0, 'status dot: in recon'],
  ['--status-pending_sale-dot', '--bg-surface', 3.0, 'status dot: pending sale'],
  ['--status-acquired-dot',     '--bg-surface', 3.0, 'status dot: acquired'],
  ['--status-sold-dot',         '--bg-surface', 3.0, 'status dot: sold'],

  // Non-text UI: WCAG 1.4.11 requires 3:1 for boundaries that convey meaning.
  // --border-subtle and --border-default are decorative dividers and are
  // deliberately exempt; --border-control is the token for anything that tells
  // a user where an interactive control begins, and it is not exempt.
  ['--border-control', '--bg-surface',  3.0, 'control border on surface'],
  ['--border-control', '--bg-canvas',   3.0, 'control border on canvas'],
  ['--border-focus',   '--bg-surface',  3.0, 'focus ring on surface'],
  ['--border-focus',   '--bg-canvas',   3.0, 'focus ring on canvas'],

  // Aging bars carry meaning, so they must clear the non-text threshold too.
  ['--aging-watch-bar',    '--bg-surface', 3.0, 'aging bar: watch'],
  ['--aging-stale-bar',    '--bg-surface', 3.0, 'aging bar: stale'],
  ['--aging-critical-bar', '--bg-surface', 3.0, 'aging bar: critical'],

  // The aging day count is type, so the two buckets that take colour are held
  // to the text threshold.
  ['--aging-stale-text',    '--bg-surface', 4.5, 'aging count: stale'],
  ['--aging-critical-text', '--bg-surface', 4.5, 'aging count: critical'],
];

const themes = [
  ['light', light],
  ['dark', dark],
];

let failures = 0;
let checks = 0;

for (const [themeName, theme] of themes) {
  console.log(`\n  ${themeName} theme`);
  for (const [fg, bg, min, label] of CASES) {
    checks++;
    let ratio;
    try {
      ratio = contrast(theme, fg, bg);
    } catch (err) {
      failures++;
      console.log(`  ✗ ${label.padEnd(34)} ${err.message}`);
      continue;
    }
    const pass = ratio >= min;
    if (!pass) failures++;
    const mark = pass ? '✓' : '✗';
    console.log(
      `  ${mark} ${label.padEnd(34)} ${ratio.toFixed(2)}:1 (min ${min.toFixed(1)})`,
    );
  }
}

console.log(`\n  ${checks - failures}/${checks} contrast checks passed`);

if (failures > 0) {
  console.error(`\n  FAILED: ${failures} token pair(s) below the WCAG 2.2 AA threshold.`);
  console.error('  Fix the token, do not lower the threshold.\n');
  process.exit(1);
}

console.log('  All token pairs meet WCAG 2.2 AA.\n');
