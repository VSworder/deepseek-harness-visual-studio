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

import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
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

		yield mountVisualStudioTools(ctx);
	}, 'vs-gate lifecycle');
}

/**
 * Mounts the MCP client that gives the agent the Visual Studio tools, and returns a
 * disposer.
 *
 * The tools are a second plugin - `@deepseek-ai/dsh-mcp-client` pointed at the extension's
 * loopback endpoint - and nothing else mounts it. The extension's generated patch used to
 * carry the entry itself, which meant a session started any other way had the diff gate and
 * none of the tools. Owning it here instead gives every route the same set, and leaves one
 * place that can mount it, so there is no way to end up with each tool registered twice.
 *
 * The client needs a concrete URL up front, so unlike the gate - which rediscovers the
 * bridge on every call - this can only be pointed at a Visual Studio that already exists.
 * Hence the retry: starting the session first and Visual Studio second is an ordinary order
 * to do things in.
 */
function mountVisualStudioTools(ctx) {
	// A context with no service resolver has no loader, and a host with no loader has no way
	// to mount anything. That is a reason to skip this, not to fail the session.
	const loader = ctx.get?.('loader');
	if (loader === undefined || typeof loader.import !== 'function') return () => {};

	let timer;
	let giveUp;
	let settled = false;

	const attempt = async () => {
		if (settled) return;

		// Whichever bridge is best right now. This client needs a concrete URL up front, so
		// unlike the gate - which rediscovers on every call - it can only be pointed at a
		// Visual Studio that already exists.
		const target = bridgeCandidates()[0];
		if (target === undefined) return;

		settled = true;
		clearInterval(timer);
		clearTimeout(giveUp);
		timer = undefined;
		giveUp = undefined;

		try {
			// Loaded through the loader rather than imported here: this file sits outside any
			// node_modules when the extension mounts it, and even a package install would have
			// to rely on hoisting to reach a sibling. The loader resolves plugin names for a
			// living, which is exactly what this is.
			const client = await loader.import('@deepseek-ai/dsh-mcp-client');
			ctx.plugin(client, {
				serverName: 'vs',
				transport: 'streamable-http',
				url: `http://127.0.0.1:${target.port}/mcp`,
				headers: { 'x-dsh-vs-authorization': target.token },
				// Visual Studio can close while the session lives on, and a client that refused
				// to start without its server would take the session down with it.
				failOnStartupError: false
			});
			ctx.logger?.info?.(`vs-gate: Visual Studio tools mounted from port ${target.port}`);
		} catch (error) {
			// Losing the tools is not a reason to lose the session, and it is not something to
			// keep quiet about either. One warning, once.
			ctx.logger?.warn?.(`vs-gate: could not mount the Visual Studio tools: ${error?.message ?? error}`);
		}
	};

	// Visual Studio is allowed to start after the session does. That is an ordinary order to
	// do things in, and the alternative is an agent with no tools until the user thinks to
	// restart. Bounded rather than endless: after ten minutes the session has been running
	// without an IDE, and polling a directory forever is not worth the tidiness.
	attempt();
	timer = setInterval(attempt, 10000);
	giveUp = setTimeout(() => {
		settled = true;
		clearInterval(timer);
		timer = undefined;
	}, 10 * 60 * 1000);

	return () => {
		clearInterval(timer);
		clearTimeout(giveUp);
	};
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

	const candidates = bridgeCandidates();
	if (candidates.length === 0) {
		// No Visual Studio is reachable. Mounted by the extension, this cannot happen; found
		// through a lock file, it means no instance is open. Either way this plugin's whole
		// job is to route a change to that window, and with no window there is nothing to
		// say - so it says nothing and the harness decides, exactly as if it were not
		// installed. Answering `ask` here would put a prompt in front of every edit for a
		// plugin the user installed to review edits, which is worse than useless.
		return undefined;
	}

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
		return undefined;
	}

	const payload = {
		filePath,
		currentContents: current,
		newContents: proposed,
		toolName: exec.name,
		callId: exec.callId,
		cwd: process.cwd()
	};

	// Try each candidate until one answers. Discovery cannot test liveness without a
	// request, so the request is the test: a lock file left by a closed Visual Studio
	// simply fails and the next candidate is tried, and a lock file is therefore never
	// worth deleting to stay correct.
	let lastError;
	for (const target of candidates) {
		try {
			const verdict = await ask(target, payload, exec.signal);
			if (verdict.accept === true) return { kind: 'allow' };
			return { kind: 'deny', reason: verdict.reason || 'Rejected in the Visual Studio diff' };
		} catch (error) {
			lastError = error;
		}
	}

	ctx.logger?.warn?.(`vs-gate: review request failed: ${lastError?.message ?? lastError}`);

	// Every candidate failed. What that means depends on who put this plugin here. The
	// extension started this session expecting a window, so a user who asked for review and
	// silently did not get one must be told - hence `ask`. A standalone install that found a
	// Visual Studio and then lost it is back to "no window", which is not its business.
	return candidates[0].expected === true ? UNAVAILABLE : undefined;
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

// --- finding the bridge ----------------------------------------------------------

/**
 * Every bridge worth trying, best first. Empty means no Visual Studio is reachable.
 *
 * Two ways in, and they exist for different reasons:
 *
 * - The environment, when the extension starts the session itself. That is the ordinary
 *   path and it wins outright: the port and token were chosen for *this* session, so
 *   falling back to a scanned lock file could route a change to a different Visual Studio
 *   than the one the user is looking at, which is worse than failing.
 * - The lock files the extension writes, when the plugin was installed on its own and
 *   somebody started DeepSeek Harness themselves. Nothing here needs the extension to have
 *   launched the session - only for Visual Studio to be open, because that is where the
 *   window comes from.
 */
function bridgeCandidates() {
	const port = Number(process.env.DSH_VS_BRIDGE_PORT);
	const token = process.env.DSH_VS_BRIDGE_TOKEN;
	if (Number.isInteger(port) && port > 0 && typeof token === 'string' && token.length > 0) {
		return [{ port, token, expected: true }];
	}
	return scanLockFiles();
}

/**
 * Bridges advertised by lock files, ranked by how well their workspace matches this
 * process's directory.
 *
 * Liveness is deliberately not checked here. Node cannot open a socket synchronously, and a
 * failed request is the same evidence an explicit probe would produce - so the request *is*
 * the probe, and the caller moves to the next candidate. That also means a lock file left
 * behind by a Visual Studio that has since closed costs one refused connection and nothing
 * else, which is why nothing needs to clean them up for this to stay correct.
 */
function scanLockFiles() {
	const base = process.env.LOCALAPPDATA;
	if (typeof base !== 'string' || base.length === 0) return [];

	const directory = join(base, 'DeepSeekHarness', 'vs-bridge');
	let names;
	try {
		names = readdirSync(directory).filter((entry) => entry.endsWith('.lock'));
	} catch {
		// No directory means the extension has never run here, which is the ordinary case
		// for someone who installed this plugin on its own and has no Visual Studio open.
		return [];
	}

	const cwd = normalise(process.cwd());
	const found = [];
	for (const entry of names) {
		try {
			// A leading byte-order mark is stripped before parsing. The extension writes
			// these files without one, but anything else writing UTF-8 on Windows may add
			// it - and JSON.parse rejects a BOM outright, which would make this plugin skip
			// a perfectly good lock and silently route nothing.
			const raw = readFileSync(join(directory, entry), 'utf8').replace(/^\uFEFF/, '');
			const lock = JSON.parse(raw);
			const port = Number(lock.port);
			const token = lock.authToken;
			if (!Number.isInteger(port) || port <= 0 || typeof token !== 'string' || token.length === 0) continue;
			found.push({ port, token, expected: false, score: workspaceScore(lock.workspaceFolders, cwd) });
		} catch {
			// A lock file we cannot read is one we skip; it is not a reason to refuse to
			// review anything else.
		}
	}

	// Highest score first. Ties keep discovery order, which is fine: the first that answers
	// is the one the user gets, and a second Visual Studio is a rare configuration.
	return found.sort((left, right) => right.score - left.score);
}

/** Lower-case, forward-slashed, without a trailing separator, for comparison. */
function normalise(value) {
	return String(value).replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
}

/**
 * How well a lock's workspace folders match the session directory: exact beats enclosing
 * beats enclosed, and anything unrelated scores zero.
 *
 * Contains rather than equality, because a session routinely runs in a subdirectory of the
 * folder Visual Studio has open - and separator-aware, so `C:/work/app` does not match a
 * sibling `C:/work/app-service`.
 */
function workspaceScore(folders, cwd) {
	if (!Array.isArray(folders)) return 0;

	let best = 0;
	for (const folder of folders) {
		if (typeof folder !== 'string') continue;
		const workspace = normalise(folder);
		if (workspace.length === 0) continue;

		if (workspace === cwd) best = Math.max(best, 3);
		else if (cwd.startsWith(workspace + '/')) best = Math.max(best, 2);
		else if (workspace.startsWith(cwd + '/')) best = Math.max(best, 1);
	}
	return best;
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
