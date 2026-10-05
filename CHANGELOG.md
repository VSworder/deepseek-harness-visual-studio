# Changelog

## 0.2.0

The gate is now a DeepSeek Harness plugin in its own right, not only a part of the extension.

- **Installable with `dsh plugin add`.** `packages/dsh-plugin-vs-gate` declares a
  `dsh.bundle` manifest, so DeepSeek Harness can mount it directly. That is for running the
  harness in your own terminal rather than through **Start session**; the diff window still
  comes from the extension, so Visual Studio has to be open with it installed.
- **It finds Visual Studio without being told where it is.** The extension used to inject the
  bridge port and token, which is useless to a session it did not start. The plugin now also
  reads the lock files the extension writes, ranked by how well their workspace matches the
  session directory.
- **It says nothing when there is nothing to say.** With no Visual Studio reachable the plugin
  has no opinion, instead of prompting on every edit. A session the extension launched is the
  exception and still answers `ask`, because there a window was promised.
- **A lock file with a byte-order mark is read.** `JSON.parse` rejects a BOM outright, so one
  used to be skipped in silence and nothing routed — the same shape of failure as the
  package-directory URL that made the gate absent without a word.
- **One edit, one diff.** The bridge de-duplicates by call id, so mounting the gate twice —
  once by the extension, once by a profile install — no longer shows two windows for one
  change.

### The publisher

The manifest said `Publisher="DeepSeekHarness"`, which has to match the Visual Studio
Marketplace publisher ID exactly - an upload with a mismatched publisher is rejected. That
name is not one this project can register, because it reads as the vendor, so the publisher
is `VSworder`, the same name as the GitHub account.

**If you installed 0.2.0 from the GitHub release before this change, uninstall it first.**
Visual Studio identifies an extension by publisher and id together, so the old build and the
new one are two different extensions: both would be installed, and both would answer the
same commands.

## 0.1.0


First working version. The diff gate, the Visual Studio tools and one-click session start all
work; the gate is a plugin this repository owns.

### The diff gate

- **Edits are reviewed in Visual Studio's own comparison window.** Left is the file as the
  gate plugin read it, right is the exact content the tool is about to write. Accept writes
  it; Reject fails the tool call with your reason attached, which the model sees.
- **Rejecting a new file creates nothing.** Not the file, not its parent directories. The
  left-hand side is staged in the temp directory rather than at the target path.
- **A missing bridge is not a silent allow.** The plugin answers `ask`, handing the call to
  the harness's own permission flow. An earlier design failed open, which meant every edit
  landed unreviewed and nothing said so.
- **Reads are not gated**, and neither are `edit`s the harness itself would refuse — an
  unmatched or ambiguous search shows no diff, because there is no change to review.
- **All four `str_replace_editor` commands behave.** `create`, `str_replace` and `insert` send
  the correct before and after content; `view` never reaches the bridge, which is the read
  exemption. Verified against a session with that tool mounted.

### The Visual Studio tools

Three read-only MCP tools: `get_environment`, `get_open_files`, `get_current_selection`. The
last works while you are typing in the terminal, because it reads the text manager's last
active view rather than the focused one, and falls back to the caret's line when nothing is
selected.

### Starting a session

**Tools → DeepSeek Harness → Start session** opens a terminal tab running DeepSeek Harness
with the gate attached, mounted through a profile the extension registers at runtime and
withdraws once the terminal is up — your DeepSeek Harness profile is never modified.

### Notes from building it

Recorded because each of these cost real time and each failure was silent.

- **A package directory cannot be imported.** Mounting the gate by a URL naming a package
  directory makes Node answer `ERR_UNSUPPORTED_DIR_IMPORT`; the harness reports that as one
  line, `entry did not activate`, and continues. The gate was absent while the MCP entry in
  the same patch kept working, so the integration looked healthy. The plugin is mounted by
  its entry *file*.
- **Do not infer an API's shape from a decompiler.** A disassembly of
  `Microsoft.VisualStudio.Terminal.dll` omitted `TerminalWindowOptions.Name`, `.Profile` and
  `.WorkingDirectory` — inherited members — so several attempts passed a profile the terminal
  service then ignored. A runtime reflection dump settled it in one run.
- **"The call returned" is not "it worked".** Both of the above reported success. Where a
  component can fail open, make it say so; where it cannot, check the effect rather than the
  return value.
- **Do not force a user's policy.** An early version wrote `policy: never` into the generated
  patch, on a wrong assumption about patched sessions, which silently disabled an approval
  policy the user had enabled.
- **A plugin may only add.** The extension writes under `%LOCALAPPDATA%\DeepSeekHarness\` and
  mounts from there. It does not edit the user's DeepSeek Harness profile, terminal defaults
  or approval policy.
