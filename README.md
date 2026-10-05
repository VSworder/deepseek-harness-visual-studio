English | [中文](README.zh.md)

# DeepSeek Harness for Visual Studio

Bring **DeepSeek Harness** (dsh) into Visual Studio:

- **Native diff gate.** When the agent wants to change a file, the diff opens in Visual
  Studio's own comparison window — left is what is on disk, right is what the model
  proposes — with Accept and Reject. Nothing is written until you accept, and a rejection
  can carry an explanation that goes straight back to the model.
- **Visual Studio tools for the agent.** The session gets read-only MCP tools that report
  IDE state: which solution is open, which files are in the editor, what you have selected.
  The model stops guessing about your workspace.

Setup is the VSIX plus one command. Nothing is copied into your repository, nothing is added
to your DeepSeek Harness profile, and uninstalling leaves only a folder under
`%LOCALAPPDATA%` for you to delete if you want it gone.

> **Unofficial.** This is a community-built integration. It is not affiliated with, endorsed
> by, or supported by DeepSeek. DeepSeek Harness is their product; this repository only adds
> an IDE surface to it.

**Looking for VS Code?** This is for **Visual Studio** (the IDE). There are separate
community extensions for VS Code.
## Status

Early, but every claim below has been checked somewhere specific, and the table says where.

| Piece | State |
| --- | --- |
| Diff gate, edit on an existing file | **Verified in Visual Studio.** Reject left the file untouched and the reason reached the model; Accept wrote it |
| Diff gate, write of a new file | **Verified in Visual Studio.** Reject created nothing — no file, not even a zero-byte placeholder |
| Reads are not gated | **Verified.** A read passes through without a diff |
| MCP endpoint | Verified with the MCP client library the harness itself uses |
| `get_environment`, `get_open_files` | Verified in a live agent session |
| `get_current_selection` | Verified in a live session: with a selection, and with a bare caret |
| Starting a session from Visual Studio | **Works.** Tools — DeepSeek Harness — Start session |
| Status command | Works |
| `str_replace_editor` (create, insert, str_replace, view) | **Verified** against a session with the tool mounted. `create`, `str_replace` and `insert` each sent the correct before/after content; `view` did not reach the bridge at all, which is the read exemption working |

The tool surface the agent gets:

| Tool | Reads | Writes |
| --- | --- | --- |
| `mcp__vs__get_environment` | solution path, workspace folder, process id | nothing |
| `mcp__vs__get_open_files` | the files in the editor, with the active one marked | nothing |
| `mcp__vs__get_current_selection` | the selected text, its file and line range | nothing |

All three are read-only. The gate is the only thing that can affect a file, and it affects
nothing by itself — it shows a diff and returns your verdict.

See [SECURITY.md](SECURITY.md) for what the bridge exposes and what it does not.
## Requirements

- Visual Studio 2022 17.14 or newer (developed against Visual Studio 2026 18.10)
- .NET Framework 4.8
- DeepSeek Harness: `npm install -g @deepseek-ai/dsh`
- The TUI launcher: `npm install -g @deepseek-harness-tui/dsh-tui`

## Install

```powershell
& "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" `
  .\artifacts\DeepSeekHarness.VisualStudio.vsix
```

Adjust the path for your edition. Restart Visual Studio afterwards.

## Use

Open a solution, then:

> **Tools — DeepSeek Harness — Start session**

A terminal tab opens running DeepSeek Harness with the gate attached. In that session the
agent can call the Visual Studio tools as `mcp__vs__*`, and every file edit it proposes opens
in Visual Studio's diff window first.

To run a session somewhere else, attach the same patch by hand:

```powershell
dsh-tui --patch "$env:LOCALAPPDATA\DeepSeekHarness\vs-bridge\dsh-patch.yml"
```

The patch is rewritten every time Visual Studio starts, because it carries the bridge's
current port and token. The command stays the same.

### The menu

| Command | What it does |
| --- | --- |
| **Start session** | Opens a terminal tab running a gated session |
| **Status...** | Reports whether the gate is armed, and what is missing when it is not |
| **Open log** | Opens `%LOCALAPPDATA%\DeepSeekHarness\vs-extension.log` |
| **Clean up installed files...** | Deletes the folder this extension writes to. **Optional** - see below |

**About Clean up installed files:** you do not need it to stop using the extension.
Uninstalling it from *Extensions -> Manage Extensions* already removes every capability: with
no Visual Studio there is no bridge, sessions run ungated, and nothing on disk starts by
itself. What remains is a plugin file, a launch script and a log - inert files in
`%LOCALAPPDATA%\DeepSeekHarness\`.

A VSIX uninstall cannot run code, so it cannot remove that folder for you. This command is
the only way to delete it, and it is there for people who want the folder gone, not as a
required step. It asks for confirmation and names the folder first.
## How it works

```
agent wants to write a file
  -> the gate plugin intercepts the call at tools/pre-execute
  -> it reads the file and works out the exact bytes the tool would write
  -> it posts both to the extension's loopback endpoint
  -> the extension opens Visual Studio's native diff
  -> you accept or reject
  -> the verdict becomes the call's pre-execute decision
  -> accepted writes land; a rejection fails the call with your reason attached
```

The agent reaches Visual Studio state over MCP: the extension serves `/mcp` on the same
loopback endpoint and registers itself with `dsh-mcp-client`.

### The gate is a plugin this repository owns

`packages/dsh-plugin-vs-gate` is a DeepSeek Harness plugin. The extension embeds it in the
VSIX, writes it under `%LOCALAPPDATA%\DeepSeekHarness\dsh-plugin`, and mounts it from the
generated patch.

It speaks the harness's own `tools/pre-execute` contract rather than Claude Code's
`hookSpecificOutput` format. An earlier version borrowed `@deepseek-ai/dsh-hooks-claude-code`
and drove it from a PowerShell script; the reasons that was replaced are worth recording,
because two of them were bugs that took a long time to see:

**1. A package directory cannot be imported.** The patch named the borrowed package by a URL
ending in the package name. Node answers `ERR_UNSUPPORTED_DIR_IMPORT` for that — only bare
specifiers get package resolution — and the harness reports the failure as one line,
`entry did not activate`, then carries on. The gate was simply absent while the MCP entry in
the same patch kept working, so every part of the integration looked healthy. If a mount
fails, say so loudly; a component that fails open and silently is worse than one that fails.

**2. Out of process means guessing.** The script only saw the tool's arguments, so it had to
reconstruct the result. That produced two defects that each showed the reviewer a diff
with no changes in it, which is worse than no diff: a name clash where `str_replace_editor`'s
`create` and `insert` commands carry no `old_str`, and a CRLF conversion that inserted a
carriage return into an already-CRLF search string. A plugin runs in-process and reads the
file, so the proposal is built from the same bytes the tool will write.

**3. Failure must not look like success.** The script failed open: a missing bridge meant
every edit landed unreviewed. The plugin answers `ask` instead, handing the call to the
harness's own permission flow. "I cannot show you this change" is not the same as "this
change is fine".

### The plugin can also be installed on its own

`packages/dsh-plugin-vs-gate` declares a `dsh.bundle` manifest, so it installs with
DeepSeek Harness's own command:

```sh
dsh plugin add github:VSworder/deepseek-harness-visual-studio#packages/dsh-plugin-vs-gate
```

That is for running DeepSeek Harness in your own terminal rather than through **Start
session**, and it gets the same thing: the diff gate, and the three Visual Studio tools.
The plugin mounts the MCP client itself once it finds a bridge, so every route ends up with
the same set. The window still comes from the extension — it is what owns the loopback
bridge — so Visual Studio has to be open with the extension installed. Nothing needs the
extension to have *launched* the session, and with neither one running the plugin stays out
of the way entirely.

With no Visual Studio reachable the plugin says nothing at all and the harness handles the
call as if it were not installed. That is deliberate: prompting on every edit would be a
worse outcome than staying quiet, for a plugin the user installed to make edits reviewable.
A session the extension launched is the exception — there the variables are set, a window is
expected, and a gate that cannot open one says so instead of writing unreviewed.
### Two problems still worth knowing about

**1. An ambiguous edit is refused, not reviewed.** The harness rejects an `edit` whose
`old_string` matches more than once unless `replace_all` is set, and refuses one that does
not match at all. The plugin recognises both and declines to gate, so nothing is written and
the reviewer is never shown a change that cannot happen.

**2. Nothing here is installed into your DeepSeek Harness profile.** The plugin lives under
the extension's own directory and the patch mounts it from there. The harness configuration
the user owns — approval policy, sandbox policy, profile dependencies — is not touched.

## Testing

[docs/testing.md](docs/testing.md) is the by-hand checklist: it covers the parts automated
tests cannot, which are also the parts that have failed most quietly here - whether the
harness mounts the gate at all, whether the diff window appears, and whether a rejection
actually stops the write. Each step says what a failure looks like, because every failure
this project hit looked like success from the outside.

The automated suites:

```powershell
node tests/plugin-rebuild.test.mjs     # the proposal the gate builds: 26 checks
node tests/mcp-sdk-test.mjs <port> <token>   # /mcp against the real MCP client library
```
## Building

Requires the **Visual Studio extension development** workload.

```powershell
.\build.ps1                 # Release build + package
.\build.ps1 -Install        # build, then install into the local Visual Studio
```

The VSIX lands in `artifacts\`. Visual Studio is located with `vswhere`; override with
`-VsInstallRoot`.

## Repository layout

```
src/
  DeepSeekHarness.Bridge/     loopback endpoint: lock file, /permission, /mcp  (no VS dependency)
  DeepSeekHarness.Setup/      locating dsh, generating the plugin mount and patch
  DeepSeekHarness.VS/         the VSIX: package, diff window, IDE tools, commands
  DeepSeekHarness.Vsix/       packaging
packages/
  dsh-plugin-vs-gate/         the gate plugin: embedded in the VSIX, and installable on its own
docs/
  protocol.md                 /permission contract
tests/
  BridgeHarness.cs            runs the bridge standalone, for protocol testing
  mcp-sdk-test.mjs            drives /mcp with the real MCP client library
  plugin-rebuild.test.mjs     asserts the proposal the gate plugin builds
  fake-bridge.mjs             stand-in bridge for tests
```

`DeepSeekHarness.Bridge` deliberately has no Visual Studio dependency, so the protocol can
be tested without an IDE in the loop. Most of the risk lives there, and
`tests/BridgeHarness.cs` plus `tests/mcp-sdk-test.mjs` exercise it directly.

## See also

| Document | What it covers |
| --- | --- |
| [CHANGELOG.md](CHANGELOG.md) | What is in each release, and the mistakes worth not repeating |
| [SECURITY.md](SECURITY.md) | What the bridge exposes, what the token is worth, and what the extension will not do |
| [docs/testing.md](docs/testing.md) | The by-hand checklist for what automated tests cannot reach |
| [docs/protocol.md](docs/protocol.md) | The `/permission` contract between the plugin and the extension |

## License

MIT
