// Unit tests for the VS gate plugin.
//
// The proposal reconstruction decides what the reviewer sees, and a wrong answer there is
// worse than no gate at all: the diff looks plausible, the reviewer approves, and the tool
// writes something else. So every branch is asserted against the bytes the harness would
// actually write.
//
// The plugin's helpers are not exported, so they are exercised through the public seam:
// apply() captures the `tools/pre-execute` listener it registers, and a stub bridge records
// what that listener asks to be reviewed.
//
// Run: node tests/plugin-rebuild.test.mjs

import { createServer } from 'node:http';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pluginUrl = pathToFileURL(join(here, '..', 'src', 'DeepSeekHarness.DshPlugin', 'lib', 'index.js')).href;

const workDir = mkdtempSync(join(tmpdir(), 'vs-gate-test-'));

let failures = 0;
let checks = 0;

function check(label, actual, expected) {
  checks += 1;
  if (actual === expected) {
    console.log(`  [PASS] ${label}`);
    return;
  }
  failures += 1;
  console.log(`  [FAIL] ${label}`);
  console.log(`         expected: ${JSON.stringify(expected)}`);
  console.log(`         actual  : ${JSON.stringify(actual)}`);
}

/** Loads the plugin and returns the pre-execute listener apply() registered. */
async function loadListener(module) {
  const { apply } = await import(`${pluginUrl}?v=${Math.random()}`);
  let captured = null;
  const ctx = {
    effect(fn) {
      // Drive the generator to completion so ctx.on(...) runs and the listener is captured.
      // No value is passed back: the plugin's generator only calls ctx.on and then ends.
      for (const _ of fn()) { /* the effect body registers its own listeners */ }
    },
    on(event, listener) {
      if (event === 'tools/pre-execute') captured = listener;
      return () => {};
    },
    logger: { warn() {} }
  };
  apply(ctx);
  if (captured === null) throw new Error('apply() did not register a tools/pre-execute listener');
  return captured;
}

/** Runs one review against a loopback bridge that records payloads and answers `verdict`. */
async function review(listener, toolName, args, verdict = { accept: true }) {
  const received = [];
  const server = createServer((req, res) => {
    let raw = '';
    req.setEncoding('utf8');
    req.on('data', (chunk) => { raw += chunk; });
    req.on('end', () => {
      received.push(raw);
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify(verdict));
    });
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  process.env.DSH_VS_BRIDGE_PORT = String(server.address().port);
  process.env.DSH_VS_BRIDGE_TOKEN = 'test-token';

  let decision;
  try {
    decision = await listener(
      { name: toolName, arguments: args, callId: 'call-1', signal: undefined },
      async () => ({ kind: 'allow' })
    );
  } finally {
    await new Promise((resolve) => server.close(resolve));
    delete process.env.DSH_VS_BRIDGE_PORT;
    delete process.env.DSH_VS_BRIDGE_TOKEN;
  }

  return { decision, payload: received.length > 0 ? JSON.parse(received[0]) : null };
}

function fileWith(contents) {
  const path = join(workDir, `case-${Math.random().toString(36).slice(2)}.txt`);
  writeFileSync(path, contents, 'utf8');
  return path;
}

console.log('VS gate plugin: proposal reconstruction\n');
const listener = await loadListener();

// --- write -----------------------------------------------------------------------
{
  const path = fileWith('old\n');
  const r = await review(listener, 'write', { file_path: path, content: 'brand new\n' });
  check('write: accepted', r.decision.kind, 'allow');
  check('write: proposed bytes', r.payload.newContents, 'brand new\n');
  check('write: current bytes', r.payload.currentContents, 'old\n');
}

// --- edit, LF --------------------------------------------------------------------
{
  const path = fileWith('aaa\nccc\n');
  const r = await review(listener, 'edit', { file_path: path, old_string: 'aaa', new_string: 'bbb' });
  check('edit lf: proposed', r.payload.newContents, 'bbb\nccc\n');
}

// --- edit, CRLF: the case the script version got wrong ----------------------------
{
  const path = fileWith('aaa\r\nccc\r\n');
  const r = await review(listener, 'edit', { file_path: path, old_string: 'aaa\r\nccc', new_string: 'bbb\r\nccc' });
  check('edit crlf: proposed keeps CRLF', r.payload.newContents, 'bbb\r\nccc\r\n');
}

// --- an edit that cannot match must not become a review ---------------------------
{
  const path = fileWith('aaa\n');
  const r = await review(listener, 'edit', { file_path: path, old_string: 'zzz', new_string: 'yyy' });
  check('edit no match: bridge not asked', r.payload, null);
  check('edit no match: asks the harness', r.decision.kind, 'ask');
}

// --- an ambiguous edit, which the harness refuses without replace_all -------------
{
  const path = fileWith('dup\ndup\n');
  const r = await review(listener, 'edit', { file_path: path, old_string: 'dup', new_string: 'x' });
  check('edit ambiguous: bridge not asked', r.payload, null);
}

// --- str_replace_editor: create ---------------------------------------------------
{
  const path = join(workDir, 'created.txt');
  const r = await review(listener, 'str_replace_editor', { command: 'create', path, file_text: 'fresh\n' });
  check('create: proposed', r.payload.newContents, 'fresh\n');
  check('create: current is empty', r.payload.currentContents, '');
}

// --- str_replace_editor: str_replace ----------------------------------------------
{
  const path = fileWith('one\nthree\n');
  const r = await review(listener, 'str_replace_editor', { command: 'str_replace', path, old_str: 'one', new_str: 'two' });
  check('str_replace: proposed', r.payload.newContents, 'two\nthree\n');
}

// --- str_replace_editor: insert ---------------------------------------------------
{
  const path = fileWith('L1\nL2\n');
  const r = await review(listener, 'str_replace_editor', { command: 'insert', path, insert_line: 1, new_str: 'MID' });
  check('insert: proposed', r.payload.newContents, 'L1\nMID\nL2\n');
}
{
  const path = fileWith('L1\nL2\n');
  const r = await review(listener, 'str_replace_editor', { command: 'insert', path, insert_line: 0, new_str: 'FIRST' });
  check('insert at 0: proposed', r.payload.newContents, 'FIRST\nL1\nL2\n');
}

// --- a deletion -------------------------------------------------------------------
{
  const path = fileWith('keep\ngone\n');
  const r = await review(listener, 'str_replace_editor', { command: 'str_replace', path, old_str: 'gone', new_str: '' });
  check('delete: proposed', r.payload.newContents, 'keep\n\n');
}

// --- view is a read, not a change -------------------------------------------------
{
  const path = fileWith('L1\n');
  const r = await review(listener, 'str_replace_editor', { command: 'view', path });
  check('view: not reviewed', r.payload, null);
  check('view: passes through', r.decision.kind, 'allow');
}

// --- unhandled tool ---------------------------------------------------------------
{
  const r = await review(listener, 'shell', { command: 'dir' });
  check('unhandled tool: not reviewed', r.payload, null);
  check('unhandled tool: passes through', r.decision.kind, 'allow');
}

// --- rejection carries the reviewer's reason --------------------------------------
{
  const path = fileWith('aaa\n');
  const r = await review(listener, 'edit', { file_path: path, old_string: 'aaa', new_string: 'bbb' },
    { accept: false, reason: 'not this way' });
  check('reject: denied', r.decision.kind, 'deny');
  check('reject: reason carried', r.decision.reason, 'not this way');
}

// --- no bridge configured: must not silently allow --------------------------------
{
  const path = fileWith('aaa\n');
  const decision = await listener(
    { name: 'edit', arguments: { file_path: path, old_string: 'aaa', new_string: 'bbb' }, callId: 'call-2', signal: undefined },
    async () => ({ kind: 'allow' })
  );
  check('no bridge: asks instead of allowing', decision.kind, 'ask');
}

console.log('');
if (failures === 0) {
  console.log(`All ${checks} checks passed.`);
  process.exit(0);
}
console.log(`${failures} of ${checks} checks FAILED.`);
process.exit(1);
