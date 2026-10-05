# `/permission` endpoint contract

The contract between the **PreToolUse hook** (`scripts/vs-permission-hook.ps1`) and the
**Visual Studio extension**. A small JSON-over-HTTP protocol on loopback.

This shape was validated end-to-end against DeepSeek Harness before any extension code
existed, using a stand-in HTTP server implementing exactly these semantics.

## Discovery

The extension writes one lock file per Visual Studio instance:

```
%LOCALAPPDATA%\DeepSeekHarness\vs-bridge\<port>.lock
```

```json
{
  "port": 54123,
  "authToken": "3f9c1e2a-...",
  "pid": 12345,
  "workspaceFolders": ["D:\\WorkProject\\MyGame"]
}
```

Rules the hook relies on:

- **File name is the port.** No field is needed for it, but it is carried inside for clarity.
- **`pid` is checked.** A lock whose process is gone is skipped, so a crashed VS never wins discovery.
- **The port must be listening.** A 300 ms TCP connect probe decides.
- **Most-specific workspace match wins.** Both-way containment, ranked
  `exact` > `session inside workspace` > `workspace inside session`; ties break toward the
  longer workspace path. An unmatched lock stays a candidate at rank 0, so a single
  VS instance can serve any session — but see `ask` below.

## `POST /permission`

Request headers:

| Header | Value |
| --- | --- |
| `x-dsh-vs-authorization` | the `authToken` from the lock file |
| `Content-Type` | `application/json; charset=utf-8` |

Request body:

| Field | Type | Meaning |
| --- | --- | --- |
| `filePath` | string | absolute path of the file the model wants to change |
| `newContents` | string | the **complete proposed** content (the hook reconstructs it) |
| `cwd` | string | the agent session's working directory |
| `permissionMode` | string | DSH's permission mode for this session (may be empty) |
| `transcriptPath` | string | transcript path if DSH supplied one (may be empty) |
| `pid` | int | the hook process id, so the bridge can tell sessions apart |

Response body — exactly one of:

| Response | Meaning | Hook emits |
| --- | --- | --- |
| `{"accept": true}` | user accepted the diff | `permissionDecision: "allow"` |
| `{"accept": false, "reason": "..."}` | user rejected, optional reason | `permissionDecision: "deny"`, reason reaches the model |
| `{"ask": true}` | bridge declines to gate this session | **no decision** — DSH's own permission flow runs |

`ask` exists so the gate is never *stricter* than the user's own configuration: if the
session does not belong to this Visual Studio (different workspace, or ownership cannot be
proven), the bridge hands the decision back instead of forcing a prompt.

### Errors

| Status | When | Hook behaviour |
| --- | --- | --- |
| 401 | missing/wrong `x-dsh-vs-authorization` | hook's catch → `allow` (fail-open) |
| any non-2xx | server error | hook's catch → `allow` (fail-open) |
| timeout | user never decided | DSH kills the hook at 24 h; a killed hook is treated as **allow** |

Fail-open is deliberate: a broken bridge must never make the user's agent unusable.
The consequence — and it matters — is that **gate failure is silent**. That is why the
extension ships a health check and logs to the VS Output window.

## Why the timeout is 24 hours

DeepSeek Harness defaults a hook with no `timeout` to **10 minutes**
(`DEFAULT_HOOK_TIMEOUT_MS = 600000`) and treats a killed hook as `allow`. A diff review
can easily exceed 10 minutes, and the failure mode is a silently disabled gate, so the
generated `settings.json` always writes:

```json
"timeout": 86400
```

## Why the hook lives outside the repository

DeepSeek Harness executes command hooks through `ctx.shell`, which on Windows is Git Bash.
A repository path containing `&` — for example `D:\WorkProject\Unity\Move&Jump` — is parsed
as a command separator, so the hook never runs at all and the gate fails silently.
Installing to `%LOCALAPPDATA%\DeepSeekHarness\vs-bridge\` avoids the problem entirely, and
also lets the extension update the hook without touching the user's project.
