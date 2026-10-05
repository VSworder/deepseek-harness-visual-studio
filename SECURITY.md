# Security

What this extension can do, what it deliberately cannot, and where the trust boundaries are.
Written to be checkable: every claim below names the file that enforces it.

## The short version

Nothing listens on a network interface, nothing is installed into your DeepSeek Harness
profile, and the extension runs no shell commands. The one non-obvious boundary is that the
bridge trusts any process running as **you** — which is the same set of processes that could
edit your files directly, so the token grants no new capability.

## Trust boundaries

### The bridge is loopback only

`BridgeServer` binds exactly one prefix:

```
http://127.0.0.1:<port>/
```

(`src/DeepSeekHarness.Bridge/BridgeServer.cs`)

No wildcard, no `+`, no `localhost` alias, no IPv6. A machine on the same network cannot
reach it. The port is chosen from the first free one at startup and written to a lock file.

### Requests are authenticated with a per-instance token

Every route except `/health` requires the `x-dsh-vs-authorization` header, compared in
constant time:

```csharp
for (var i = 0; i < presented.Length; i++) diff |= presented[i] ^ _authToken[i];
```

The token is a fresh `Guid.NewGuid().ToString("D")` per Visual Studio instance, so it is not
reused across restarts and not shared between two open instances.

### What the token is worth to someone who steals it

The token is written to disk in three places, all under `%LOCALAPPDATA%\DeepSeekHarness\`:
`dsh-patch.yml` (`headers`), `start.cmd` (`set DSH_VS_BRIDGE_TOKEN=`), and the `<port>.lock`.
Any process running as the same user can read them.

With the token, such a process can:

| Call | Effect |
| --- | --- |
| `POST /mcp` `get_current_selection` | Read the text currently selected in the editor |
| `POST /mcp` `get_open_files` | Read the list of open files |
| `POST /mcp` `get_environment` | Read the solution path and process id |
| `POST /permission` | Make a diff window appear |

**None of these write a file.** `/permission` receives a proposal and *returns a verdict*; the
writing is done by the gate plugin inside the harness, on behalf of the tool call that asked.
A caller who posts a proposal learns only whether the user accepted — it has no way to apply
that verdict to anything.

So the exposure is: a same-user process can read what you have selected, and can put a dialog
on your screen. It cannot edit your files this way, and it already could by direct means.

### The plugin file is user-writable

`%LOCALAPPDATA%\DeepSeekHarness\dsh-plugin\index.js` is replaced on every Visual Studio start
from the copy embedded in the VSIX. Between starts it is an ordinary file, and a same-user
process could modify it — and the plugin runs in-process inside DeepSeek Harness.

This is not defended against, deliberately. A process that can rewrite that file can also
rewrite your shell profile, the globally installed `dsh` package, or any other file you own.
A hash check would raise the cost of one path among many while adding a failure mode where a
mismatch silently disables the gate. The honest statement is that the extension inherits the
integrity of your user account, like every other per-user tool.

## What the extension will not do

- **It does not modify your DeepSeek Harness profile.** The plugin lives under
  `%LOCALAPPDATA%\DeepSeekHarness\` and is mounted by a patch file passed on the command
  line. Your profile's dependencies, approval policy and sandbox policy are untouched.
- **It does not change your approval policy.** An earlier version forced
  `policy: never`, which silently disabled a protection the user had enabled. The plugin
  returns a decision from the harness's own waterfall instead and never touches that policy.
- **It runs no shell commands.** The only process it starts is the terminal the user asked
  for, running the generated `start.cmd`, which invokes `dsh-tui` with the patch.
- **It does not write into your repository.** The plugin and patch live under the user
  profile; the diff's left-hand side is staged in the temp directory, not at the target path.
- **It does not send anything off the machine.** The plugin posts to `127.0.0.1`.
- **Its log does not contain file contents.** `vs-extension.log` records paths, decisions and
  errors, never the bytes being reviewed.

## What the gate plugin reads

The plugin reads the target file to build the left-hand side of the diff, and it does so
before the harness's own sandbox decision is applied — `tools/pre-execute` runs ahead of the
tool body, where the filesystem policy lives. A file the harness would refuse to *write* can
therefore still be read by the plugin and shown to you.

The content goes to the local bridge, into a temporary file that is deleted when the diff
closes, and onto your screen. It is not stored, logged or transmitted. The model could read
the same file with the harness's own `read` tool, subject to the same policy that governs
everything else — so this is a wider read than the write policy implies, not a way around it.

If you want the gate to stay inside the workspace, the place to enforce it is the plugin's
`review` function: it already has the path and the session's working directory.

## Reporting

Open an issue. There is no bug bounty and no security contact; this is a small extension
whose threat model is stated above, and a report that shows one of these claims to be false
is more useful than one that lists a category.
