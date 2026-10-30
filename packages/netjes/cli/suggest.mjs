// @ts-check

/**
 * Edit distance with adjacent transpositions (optimal string alignment), so `nwe` is one edit from `new`.
 * @param {string} a
 * @param {string} b
 */
function distance(a, b) {
  /** @type {number[][]} */
  const d = Array.from({ length: a.length + 1 }, (_, i) => Array.from({ length: b.length + 1 }, (_, j) => (i === 0 ? j : j === 0 ? i : 0)));
  for (let i = 1; i <= a.length; i++) {
    for (let j = 1; j <= b.length; j++) {
      const row = /** @type {number[]} */ (d[i]);
      const above = /** @type {number[]} */ (d[i - 1]);
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      row[j] = Math.min(/** @type {number} */ (above[j]) + 1, /** @type {number} */ (row[j - 1]) + 1, /** @type {number} */ (above[j - 1]) + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) {
        row[j] = Math.min(/** @type {number} */ (row[j]), /** @type {number} */ (d[i - 2]?.[j - 2]) + 1);
      }
    }
  }
  return /** @type {number} */ (d[a.length]?.[b.length]);
}

/**
 * The candidate someone most likely meant by `input`, or undefined: the same word in another case, a close spelling,
 * or the only candidate that starts with or contains it.
 * @param {string} input
 * @param {Iterable<string>} candidates
 * @returns {string | undefined}
 */
export function suggest(input, candidates) {
  const typed = input.toLowerCase();
  const all = [...candidates];
  const exact = all.find((candidate) => candidate.toLowerCase() === typed);
  if (exact) return exact;

  const limit = typed.length <= 4 ? 1 : 2;
  const close = all
    .map((candidate) => ({ candidate, edits: distance(typed, candidate.toLowerCase()) }))
    .filter(({ edits }) => edits <= limit)
    .sort((a, b) => a.edits - b.edits);
  if (close[0]) return close[0].candidate;

  if (typed.length < 2) return undefined;
  const starting = all.filter((candidate) => candidate.toLowerCase().startsWith(typed));
  if (starting.length === 1) return starting[0];
  const containing = typed.length >= 3 ? all.filter((candidate) => candidate.toLowerCase().includes(typed)) : [];
  return containing.length === 1 ? containing[0] : undefined;
}
