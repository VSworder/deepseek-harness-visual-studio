# Testing the extension

Everything here is written to be performed by hand in Visual Studio. The automated suites
cover what they can — the plugin's proposal reconstruction, the MCP surface, the bridge — but
the parts that fail most quietly are the ones that need a real IDE: whether the gate actually
intercepts, whether the diff appears, and whether a rejection stops the write.

Do these in order. Each step states what you should see **and what a failure looks like**,
because every failure this project has hit looked like success from the outside.

---

## 0. Before you start

- Visual Studio 2022 17.14+ with the extension installed, **restarted after installing**
- `dsh` and `dsh-tui` on PATH (`npm install -g @deepseek-ai/dsh @deepseek-harness-tui/dsh-tui`)
- A project open that you do not mind the agent touching — or better, one throwaway file

Two files are worth knowing before you begin:

| File | What it tells you |
| --- | --- |
| `%LOCALAPPDATA%\DeepSeekHarness\vs-extension.log` | What the extension did: bridge port, plugin written, terminal started, MCP tool count, every accept/reject |
| `%LOCALAPPDATA%\DeepSeekHarness\hook.log` | What the **gate plugin** did. **This file no longer exists** — the plugin was rewritten and does not write one. Absence is expected. |

---

## 1. The extension loaded

> **Tools → DeepSeek Harness**

**Expect:** a submenu with four entries — *Start session*, *Status…*, *Open log*,
*Clean up installed files…* — and a *Start DeepSeek Harness session* item in the editor's
right-click menu.

**Failure looks like:** no submenu at all. The command table was not compiled in. Check
`vs-extension.log` for a line beginning `failed to start bridge`.

---

## 2. The gate is armed

> **Tools → DeepSeek Harness → Status…**

**Expect** a report containing:

```
Diff gate
  gate plugin     C:\Users\<you>\AppData\Local\DeepSeekHarness\dsh-plugin\index.js
  bridge patch    C:\Users\<you>\AppData\Local\DeepSeekHarness\vs-bridge\dsh-patch.yml

  tui launcher    C:\Users\<you>\AppData\Roaming\npm\dsh-tui.cmd
  dsh launcher    C:\Users\<you>\AppData\Roaming\npm\dsh.cmd

Result: the gate is armed. Edits wait for the diff window.
```

**Failure looks like:** `gate plugin MISSING`, or `Result: the gate is NOT armed`. Restart
Visual Studio — the plugin is written on every start — and check again.

---

## 3. A session starts

> **Tools → DeepSeek Harness → Start session**

**Expect:** a terminal tab titled *DeepSeek Harness* running the `dsh-TUI` banner, and about
two seconds later a line in `vs-extension.log`:

```
mcp: listed 3 tool(s)
```

**Failure looks like:** a plain PowerShell prompt instead of the TUI. In `vs-extension.log`,
`started terminal … with profile 'DeepSeek Harness'` appears either way, so trust the screen,
not the log line.

---

## 4. The gate intercepts an edit — **the main event**

In the session, ask for a small change to a file you can see:

```
Change <file> so that <something small>. Do not use a shell command.
```

**Expect, in this order:**

1. The agent's tool call **blocks** — it does not return while you read
2. Visual Studio's **native diff window** opens beside your code
3. Left pane labelled *Current (on disk)*, right pane *Proposed (DeepSeek Harness)*
4. A small dialog offering **Accept** and **Reject**
5. The right pane shows **the actual change** — not an empty diff

**Failure looks like — and each means something different:**

| What you see | What it means |
| --- | --- |
| The edit lands instantly, no diff | The plugin is not mounted. Check `vs-extension.log` for `GATE OFF` and re-run step 2. |
| Diff opens but **both sides are identical** | The reconstruction is wrong. This is the defect the plugin was written to remove; capture the file and the tool call. |
| Terminal shows an error instead | The bridge refused or crashed; `vs-extension.log` records why. |

Ask the agent directly if you are unsure whether it was blocked:

```
Did your last edit call return immediately, or did it wait? Quote the tool result.
```

A gated call that you took your time over will show a long delay or none at all from the
model's side — it cannot see your clock — but the **file will not have changed yet** while
the dialog is open. That is the reliable check.

---

## 5. Accept writes

With the diff open, press **Accept**.

**Expect:**

- The diff window closes
- The file **now contains the proposed content**
- `vs-extension.log` gains `accepted: <path>`
- The agent reports success and moves on

---

## 6. Reject refuses, and the reason travels

Make another change, then press **Reject** and type a reason, for example `not this way`.

**Expect:**

- The file is **unchanged**
- The agent's tool call **fails** and it sees your reason — ask it:
  ```
  What did the tool return? Quote it exactly.
  ```
  and it should report something containing `not this way`

**Failure looks like:** the file changed anyway, or the model reports success. Either means
the verdict did not reach the call — capture `vs-extension.log`.

---

## 7. New files are not created on rejection

Ask for a **new** file that does not exist yet:

```
Create <new-file> containing <something>.
```

**Expect:** a diff, left side empty, right side the new content.

Now press **Reject**, then check the disk.

**Expect:** the file **does not exist**, and neither do any parent directories the model
asked for. An earlier version created them before asking, so a rejected change still left a
zero-byte file behind — worth checking explicitly.

---

## 8. Reads are not gated

```
Read <file> and tell me what it contains.
```

**Expect:** no diff window. Reads pass straight through — a diff for a read would be noise,
and rejecting it would deny a read.

---

## 9. The Visual Studio tools work

```
Call mcp__vs__get_environment
```

**Expect:** the solution path, workspace folder and process id.

Then put the caret on a line in the editor **without selecting anything**:

```
Call mcp__vs__get_current_selection
```

**Expect:** the text of that line. Then select a few lines and call it again — it should
return the selection.

**Note:** the caret is read through the text manager's *last active* view, so it works even
though you are typing in the terminal.

---

## 10. Cleanup is optional — **read this before clicking**

> **Tools → DeepSeek Harness → Clean up installed files…**

This deletes `%LOCALAPPDATA%\DeepSeekHarness\` — the gate plugin, the launch script and the
log.

**You do not need to run this to stop using the extension.** Uninstalling it from
*Extensions → Manage Extensions* already removes every capability: with no Visual Studio there
is no bridge, the session runs ungated, and nothing on disk starts by itself.

**Run it only if you want the folder gone too**, and know that:

- Sessions already started keep running, but **stop being gated** the moment the plugin is
  deleted
- A VSIX uninstall cannot run code, so it cannot do this for you — that is the only reason
  the command exists

**Expect:** a confirmation dialog naming the folder, then a result dialog. Cancel it if you
only wanted to look.

---

## What the automated suites cover

Run these after any change to the gate:

```powershell
# Proposal reconstruction: 21 checks over write, edit, CRLF, create, insert, delete, view,
# unmatched and ambiguous searches, rejection reasons, and the no-bridge path
node tests/plugin-rebuild.test.mjs

# /mcp against the real MCP client library the harness uses
# (start tests/BridgeHarness.cs first, then:)
node tests/mcp-sdk-test.mjs <port> <token>
```

Neither can check whether the harness mounts the plugin, whether the diff window appears, or
whether a rejection reaches the model. Those are steps 2, 4 and 6 above.
