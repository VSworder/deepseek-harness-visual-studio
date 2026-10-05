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
| Diff gate (lock file, hook, `/permission`) | Works; verified against a real DeepSeek Harness session |
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

Open a solution. The extension starts a loopback bridge and writes its hooks and a DSH
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
  -> DeepSeek Harness runs the PreToolUse hook
  -> the hook posts the proposed content to the extension's loopback endpoint
  -> the extension opens Visual Studio's native diff
  -> you accept or reject
  -> the verdict becomes the hook's permission decision
  -> accepted writes land; a rejection returns your reason to the model
```

The agent reaches Visual Studio state over MCP: the extension serves `/mcp` on the same
loopback endpoint and registers itself with `dsh-mcp-client`.

### Three problems worth knowing about

These cost real debugging time and shape the design.

**1. The hook must not live in your repository.** DeepSeek Harness runs command hooks
through `ctx.shell`, which on Windows is Git Bash, and `&` in a path is a command
separator there. A hook installed in a repository called `Move&Jump` never runs at all 閳?silently, because a missing hook is indistinguishable from an allowed edit. Installing
under the user profile avoids the whole class of problem.

**2. The hook timeout must be 24 hours.** DeepSeek Harness defaults a hook with no
`timeout` to **10 minutes** (`DEFAULT_HOOK_TIMEOUT_MS = 600000`) and treats a killed hook
as *allow*. A diff review can exceed that, and the failure mode is a silently disabled
gate. The generated config always writes `"timeout": 86400`.

**3. Tool names are lowercase.** DeepSeek Harness calls them `write` and `edit`, where
Claude Code uses `Write` and `Edit`. A matcher that does not match means the hook never
runs, and again the failure is silent.

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
  DeepSeekHarness.Bridge/   loopback endpoint: lock file, /permission, /mcp  (no VS dependency)
  DeepSeekHarness.Setup/    locating dsh, generating the hook, settings and patch
  DeepSeekHarness.VS/       the VSIX: package, diff window, IDE tools, commands
  DeepSeekHarness.Vsix/     packaging
scripts/
  vs-permission-hook.ps1    the PreToolUse hook, embedded in the VSIX
docs/
  protocol.md               /permission contract
tests/
  BridgeHarness.cs          runs the bridge standalone, for protocol testing
  mcp-sdk-test.mjs          drives /mcp with the real MCP client library
```

`DeepSeekHarness.Bridge` deliberately has no Visual Studio dependency, so the protocol can
be tested without an IDE in the loop. Most of the risk lives there, and
`tests/BridgeHarness.cs` plus `tests/mcp-sdk-test.mjs` exercise it directly.

## License

MIT
