/** Search over a mock snapshot and its file contents, per docs/contracts/api-v1.md. */

const MAX_RESULTS = 200;
const SNIPPET = 160;

const cellText = (v) => (v === null || v === undefined ? null : typeof v === 'object' && !Array.isArray(v) && 'text' in v ? v.text : typeof v === 'string' ? v : JSON.stringify(v));

function snippet(text, idx, qlen) {
  if (text.length <= SNIPPET) return text;
  const start = Math.min(Math.max(0, idx - Math.max(0, Math.floor((SNIPPET - qlen) / 2))), text.length - SNIPPET);
  return text.slice(start, start + SNIPPET);
}

/**
 * Case-insensitive substring search over kv keys, values and source labels, table cells, tree labels and file lines.
 * @param {{ snapshot: object, files: Map<string, { content: string }> }} built
 * @param {string} q at least 2 chars
 */
export function search(built, q) {
  const needle = q.toLowerCase();
  const results = [];
  let truncated = false;
  const firstMatch = (fields) => {
    for (const f of fields) {
      if (f === null || f === undefined) continue;
      const idx = f.toLowerCase().indexOf(needle);
      if (idx >= 0) return snippet(f, idx, q.length);
    }
    return null;
  };
  const push = (hit) => {
    if (results.length === MAX_RESULTS) {
      truncated = true;
      return false;
    }
    results.push(hit);
    return true;
  };

  const owner = new Map();
  const claim = (fileId, at) => fileId && !owner.has(fileId) && owner.set(fileId, at);

  for (const section of built.snapshot.sections) {
    for (const block of section.blocks) {
      const at = { view: section.view, collectorId: section.collectorId, blockId: block.id };
      if (block.type === 'kv') {
        for (const row of block.rows) {
          claim(row.source?.fileId, at);
          row.contributions?.forEach((c) => claim(c.source?.fileId, at));
          const s = firstMatch([row.key, cellText(row.value), row.source?.label]);
          if (s !== null && !push({ kind: 'row', ...at, itemId: row.key, snippet: s })) return done();
        }
      } else if (block.type === 'table') {
        for (const row of block.rows) {
          claim(row.fileId, at);
          Object.values(row.cells).forEach((c) => claim(c?.fileId, at));
          const s = firstMatch(block.columns.map((col) => cellText(row.cells[col.key])));
          if (s !== null && !push({ kind: 'row', ...at, itemId: row.id, snippet: s })) return done();
        }
      } else if (block.type === 'tree') {
        const walk = (nodes) => {
          for (const n of nodes) {
            claim(n.fileId, at);
            const s = firstMatch([n.label]);
            if (s !== null && !push({ kind: 'node', ...at, itemId: n.id, snippet: s })) return false;
            if (!walk(n.children ?? [])) return false;
          }
          return true;
        };
        if (!walk(block.nodes)) return done();
      }
    }
  }

  for (const [fileId, file] of built.files) {
    const at = owner.get(fileId);
    if (!at) continue;
    const lines = file.content.split('\n');
    for (let i = 0; i < lines.length; i++) {
      const idx = lines[i].toLowerCase().indexOf(needle);
      if (idx >= 0 && !push({ kind: 'file', ...at, fileId, line: i + 1, snippet: snippet(lines[i], idx, q.length) })) return done();
    }
  }
  return done();

  function done() {
    return { query: q, results, truncated };
  }
}
