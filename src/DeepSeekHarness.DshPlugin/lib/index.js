/**
 * Visual Studio review gate for DeepSeek Harness.
 *
 * Intercepts the file-writing tools before their body runs and asks the Visual Studio
 * extension to show the change in its own diff window. The verdict becomes this call's
 * pre-execute decision: accept writes the file, reject fails the call with the reviewer's
 * reason attached, so the model sees why.
 *
 * This is a DeepSeek Harness plugin, not a Claude Code hook. The earlier arrangement
 * borrowed `@deepseek-ai/dsh-hooks-claude-code` and spoke Claude Code's
 * `hookSpecificOutput` contract through a PowerShell script; that package would not load
 * from outside the dsh installation, and the failure was silent, so the gate was simply
 * absent while every other part of the integration looked healthy. Speaking the harness's
 * own `tools/pre-execute` contract removes the translation layer and the silent failure
 * with it.
 *
 * Running in-process is the other reason this is a plugin. The script could only guess at
 * the result of an edit from the tool's arguments, which is why reconstructing
 * `str_replace_editor` and CRLF handling produced so many near-misses. A plugin reads the
 * file itself, so the diff is built from the same bytes the tool will write.
 *
 * @module @deepseek-harness-vs/dsh-plugin-vs-gate
 */

import { readFileSync } from 'node:fs';
import { request } from 'node:http';

/** Cordis plugin name, used by loader diagnostics. */
const name = 'vs-gate';

/**
 * Services required before this plugin may load.
 *
 * `tools` is the waterfall this hooks. Nothing else is needed: the proposal is computed
 * from the call's own arguments and the file on disk, and the verdict is a return value
 * rather than an approval request, so this plugin never touches the approval channel and
 * cannot disturb the user's approval policy.
 */
const inject = ['tools'];

/** Tools whose body writes a file, and which therefore need review. */
const GATED_TOOLS = new Set(['write', 'edit', 'str_replace_editor']);

/**
 * Commands of `str_replace_editor` that do not write. `view` reads a file; gating it
 * would put a diff in front of the user for a read, and rejecting that diff would deny a
 * read. DSH's own permission handling covers reads.
 */
const READ_ONLY_COMMANDS = new Set(['view']);

/**
 * Decision when the extension cannot be reached at all.
 *
 * `ask` rather than `allow`. The hook this replaces failed open - a missing bridge meant
 * every edit landed unreviewed and nothing said so - and that is the one failure mode this
 * whole component exists to prevent. `ask` hands the call back to the harness's own
 * permission flow, which is the honest answer to "I cannot show you this change".
 */
const UNAVAILABLE = Object.freeze({ kind: 'ask', reason: 'the Visual Studio review window is unavailable' });

/** How long to wait for a verdict before giving up on the user having seen it. */
const REVIEW_TIMEOUT_MS = 24 * 60 * 60 * 1000;

function apply(ctx) {
	ctx.effect(function* () {
		yield ctx.on('tools/pre-execute', async (exec, next) => {
			const decision = await review(ctx, exec);
			// Undecided means this call is not ours to judge: let the rest of the waterfall
			// decide, exactly as if this listener were not mounted.
			if (decision === undefined) return next();
			return decision;
		});
	}, 'vs-gate lifecycle');
}

/**
 * The decision for one call, or undefined when this plugin has no opinion.
 */
async function review(ctx, exec) {
	if (!GATED_TOOLS.has(exec.name)) return undefined;

	const args = exec.arguments;
	if (args === null || typeof args !== 'object') return undefined;

	// `str_replace_editor` addresses its target as `path`; `write` and `edit` use `file_path`.
	const filePath = typeof args.file_path === 'string' ? args.file_path : args.path;
	if (typeof filePath !== 'string' || filePath.length === 0) return undefined;

	if (exec.name === 'str_replace_editor' && READ_ONLY_COMMANDS.has(args.command)) return undefined;

	const target = locateBridge(ctx);
	if (target === undefined) return UNAVAILABLE;

	// Read the file the tool is about to change. Failure here is not fatal: a file that
	// does not exist yet is the normal case for a create, and the proposal is still worth
	// showing against empty content.
	let current = '';
	try {
		current = readFileSync(filePath, 'utf8');
	} catch {
		current = '';
	}

	const proposed = rebuild(exec.name, args, current);
	if (proposed === undefined) {
		// This plugin cannot describe the change faithfully. Saying nothing and letting the
		// harness ask is strictly better than showing a diff the reviewer might approve
		// without seeing the real edit.
		return UNAVAILABLE;
	}

	try {
		const verdict = await ask(target, {
			filePath,
			currentContents: current,
			newContents: proposed,
			toolName: exec.name,
			callId: exec.callId,
			cwd: process.cwd()
		}, exec.signal);

		if (verdict.accept === true) return { kind: 'allow' };
		return { kind: 'deny', reason: verdict.reason || 'Rejected in the Visual Studio diff' };
	} catch (error) {
		ctx.logger?.warn?.(`vs-gate: review request failed: ${error?.message ?? error}`);
		return UNAVAILABLE;
	}
}

// --- the proposal ----------------------------------------------------------------

/**
 * The bytes the tool is about to write, or undefined when they cannot be worked out.
 *
 * Undefined is a real answer, not a failure to paper over: every branch below mirrors what
 * the harness itself does, and a branch that cannot be reproduced must not be approximated.
 */
function rebuild(tool, args, current) {
	if (tool === 'write') {
		return typeof args.content === 'string' ? args.content : undefined;
	}

	if (tool === 'edit') {
		return applyLiteral(current, args.old_string, args.new_string, args.replace_all === true);
	}

	if (tool === 'str_replace_editor') {
		const command = typeof args.command === 'string' ? args.command : 'str_replace';

		if (command === 'create') {
			// `create` refuses to overwrite, so the result is exactly `file_text`.
			return typeof args.file_text === 'string' ? args.file_text : undefined;
		}

		if (command === 'str_replace') {
			const replacement = args.new_str === undefined || args.new_str === null ? '' : args.new_str;
			return applyLiteral(current, args.old_str, replacement, args.replace_all === true);
		}

		if (command === 'insert') {
			if (typeof args.insert_line !== 'number') return undefined;
			const text = args.new_str === undefined || args.new_str === null ? '' : args.new_str;
			return applyInsert(current, args.insert_line, text);
		}

		return undefined;
	}

	return undefined;
}

/** Normalise line endings the way the harness does before matching. */
function toLf(text) {
	return typeof text === 'string' ? text.replaceAll('\r\n', '\n') : '';
}

/**
 * The file's dominant line ending. Mirrors the harness's own detector so the bytes offered
 * for review match the bytes written.
 */
function usesCrlf(raw) {
	const sample = raw.slice(0, 4096);
	const lf = sample.split('\n').length - 1;
	const crlf = sample.split('\r\n').length - 1;
	return crlf > lf - crlf;
}

function restoreLineEndings(text, crlf) {
	return crlf ? text.replaceAll('\n', '\r\n') : text;
}

/**
 * A literal replacement: normalise to LF, replace every occurrence, restore the file's own
 * line endings.
 *
 * Returns undefined when the harness would refuse the edit, so the reviewer is never shown
 * a change that cannot happen, and never asked to approve one that differs from what runs.
 */
function applyLiteral(current, oldText, newText, replaceAll) {
	const oldNorm = toLf(oldText);
	if (oldNorm.length === 0) return undefined;

	const crlf = usesCrlf(current);
	const body = toLf(current);
	const newNorm = toLf(newText);

	const occurrences = body.split(oldNorm).length - 1;
	if (occurrences === 0) return undefined;
	// The harness rejects an ambiguous edit unless replace_all is set, so it would write
	// nothing; there is no change to review.
	if (!replaceAll && occurrences > 1) return undefined;

	return restoreLineEndings(body.replaceAll(oldNorm, newNorm), crlf);
}

/** Insert text after a line, matching how the harness applies `insert_line`. */
function applyInsert(current, insertLine, text) {
	if (insertLine < 0) return undefined;

	const crlf = usesCrlf(current);
	const body = toLf(current);
	const owner = toLf(text);

	const endsWithNewline = body.endsWith('\n');
	const core = endsWithNewline ? body.slice(0, -1) : body;

	// The trailing newline is not a line of its own; splitting without accounting for it
	// pushes the inserted text past the end of the file.
	const lines = core.length === 0 ? [] : core.split('\n');
	if (insertLine > lines.length) return undefined;

	const at = Math.min(insertLine, lines.length);
	const rebuilt = [...lines.slice(0, at), owner, ...lines.slice(at)];
	const joined = rebuilt.join('\n') + (endsWithNewline ? '\n' : '');
	return restoreLineEndings(joined, crlf);
}

// --- the bridge ------------------------------------------------------------------

/**
 * Where the Visual Studio bridge is listening, or undefined when it is not discoverable.
 *
 * The extension injects the port and token when it starts the session, which is the case
 * that matters; holding them in the environment keeps discovery to one lookup and leaves
 * nothing on disk to go stale.
 */
function locateBridge(ctx) {
	const port = Number(process.env.DSH_VS_BRIDGE_PORT);
	const token = process.env.DSH_VS_BRIDGE_TOKEN;
	if (!Number.isInteger(port) || port <= 0 || typeof token !== 'string' || token.length === 0) {
		return undefined;
	}
	return { port, token };
}

/** Ask the extension to review one proposal and resolve to its verdict. */
function ask(target, payload, signal) {
	return new Promise((resolvePromise, rejectPromise) => {
		const body = Buffer.from(JSON.stringify(payload), 'utf8');
		const req = request({
			host: '127.0.0.1',
			port: target.port,
			path: '/permission',
			method: 'POST',
			headers: {
				'content-type': 'application/json; charset=utf-8',
				'content-length': body.length,
				'x-dsh-vs-authorization': target.token
			}
		}, (res) => {
			const chunks = [];
			res.on('data', (chunk) => chunks.push(chunk));
			res.on('end', () => {
				const text = Buffer.concat(chunks).toString('utf8');
				if (res.statusCode !== 200) {
					rejectPromise(new Error(`bridge answered ${res.statusCode}`));
					return;
				}
				try {
					resolvePromise(JSON.parse(text));
				} catch (error) {
					rejectPromise(new Error(`bridge answer was not JSON: ${error?.message ?? error}`));
				}
			});
		});

		// A user reading a diff can take arbitrarily long, so the wait is generous; the
		// caller's signal still wins, because a cancelled call must not hold a request open.
		const timeout = setTimeout(() => {
			req.destroy(new Error('review timed out'));
		}, REVIEW_TIMEOUT_MS);

		const abort = () => req.destroy(new Error('call cancelled'));
		signal?.addEventListener('abort', abort, { once: true });

		req.on('error', (error) => {
			clearTimeout(timeout);
			signal?.removeEventListener('abort', abort);
			rejectPromise(error);
		});
		req.on('close', () => {
			clearTimeout(timeout);
			signal?.removeEventListener('abort', abort);
		});

		req.end(body);
	});
}

export { apply, inject, name };
