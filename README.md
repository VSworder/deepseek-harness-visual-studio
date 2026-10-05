# DeepSeek Harness for Visual Studio

Bring **DeepSeek Harness** into Visual Studio:

- **Native diff gate.** When the agent wants to change a file, the diff opens in Visual
  Studio's own comparison window 閳?left is what is on disk, right is what the model
  proposes 閳?with Accept and Reject. Nothing is written until you accept, and a rejection
  can carry an explanation that goes straight back to the model.
- **Visual Studio tools for the agent.** The session gets MCP tools that read IDE state:
  which solution is open, which files are in the editor. The model stops guessing about
  your workspace.

Setup is the VSIX plus one command. Nothing is copied into your repository, no profile is
rewritten, and uninstalling leaves nothing behind but a folder under `%LOCALAPPDATA%`.

## Status

Early, but the core is verified end to end.

| Piece | State |
| --- | --- |
| Diff gate (plugin, `/permission`) | Works; verified against a real DeepSeek Harness session |
| MCP endpoint | Works; verified with the same MCP client library DeepSeek Harness uses |
| `get_environment`, `get_open_files` | Verified in a live agent session |
| `get_current_selection` | Verified in a live session: with a selection, and with a bare caret |
| Status command | Works |
| Launching the session from Visual Studio | **Works.** Tools 鈫?DeepSeek Harness 鈫?Start session |

The tools the agent gets:

| Tool | Reads |
| --- | --- |
| `mcp__vs__get_environment` | solution path, workspace folder, process id |
| `mcp__vs__get_open_files` | the files in the editor, with the active one marked |
| `mcp__vs__get_current_selection` | the selected text, its file and line range |

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

Open a solution. The extension starts a loopback bridge and writes its gate plugin and a DSH
patch under `%LOCALAPPDATA%\DeepSeekHarness\`.

Check that the gate is armed:

> **Tools 閳?DeepSeek Harness 閳?Status閳?*

Then start a session with the patch attached:

```powershell
dsh-tui --patch "$env:LOCALAPPDATA\DeepSeekHarness\vs-bridge\dsh-patch.yml"
```

The patch is rewritten every time Visual Studio starts, because it carries the bridge's
current port and token. The command stays the same.

In that session, the agent can call the Visual Studio tools as `mcp__vs__*`.

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

`src/DeepSeekHarness.DshPlugin` is a DeepSeek Harness plugin. The extension embeds it in the
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

### Two problems still worth knowing about

**1. An ambiguous edit is refused, not reviewed.** The harness rejects an `edit` whose
`old_string` matches more than once unless `replace_all` is set, and refuses one that does
not match at all. The plugin recognises both and declines to gate, so nothing is written and
the reviewer is never shown a change that cannot happen.

**2. Nothing here is installed into your DeepSeek Harness profile.** The plugin lives under
the extension's own directory and the patch mounts it from there. The harness configuration
the user owns — approval policy, sandbox policy, profile dependencies — is not touched.

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
  DeepSeekHarness.DshPlugin/  the gate plugin, embedded in the VSIX
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

## License

MIT
