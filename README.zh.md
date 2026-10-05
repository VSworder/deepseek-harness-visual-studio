[English](README.md) | 中文

# DeepSeek Harness for Visual Studio

把 **DeepSeek Harness**（`dsh`）带进 Visual Studio：

- **原生 diff 闸门。** agent 想改某个文件时，diff 会在 Visual Studio 自带的比较窗口中打开 —— 左边是磁盘上的内容，右边是模型提议的内容 —— 并提供 Accept 和 Reject。你不接受就什么都不写；而拒绝时可以附上说明，这段说明会原样回到模型那里。
- **给 agent 用的 Visual Studio 工具。** 会话会拿到一组只读的 MCP 工具，用来汇报 IDE 状态：打开了哪个解决方案、编辑器里有哪些文件、你选中的是什么。模型不必再猜你的工作区。

安装就是一个 VSIX 加一条命令。不往你的仓库里拷任何东西，不往你的 DeepSeek Harness profile 里加任何东西；卸载后只会留下 `%LOCALAPPDATA%` 下的一个文件夹，你想让它消失就自己删掉。

> **非官方。** 这是社区构建的集成，与 DeepSeek 没有隶属关系，也未获其背书或支持。DeepSeek Harness 是他们的产品；本仓库只是给它加了一层 IDE 界面。

**在找 VS Code 版？** 这个是给 **Visual Studio**（那个 IDE）用的。VS Code 另有社区扩展。

## 状态

项目还很早期，但下面每一条说法都在具体的地方验证过，表格里写明了是哪里。

| 条目 | 状态 |
| --- | --- |
| diff 闸门，编辑已有文件 | **已在 Visual Studio 中验证。** Reject 后文件原封不动，理由也传到了模型那里；Accept 则完成写入 |
| diff 闸门，写入新文件 | **已在 Visual Studio 中验证。** Reject 什么都没创建 —— 没有文件，连 0 字节的占位文件都没有 |
| 读取不经过闸门 | **已验证。** 读取直接通过，不弹 diff |
| MCP 端点 | 用 harness 自己使用的那个 MCP 客户端库验证过 |
| `get_environment`、`get_open_files` | 在真实的 agent 会话中验证过 |
| `get_current_selection` | 在真实会话中验证过：有选中内容的情况，以及只有光标的情况 |
| 从 Visual Studio 启动会话 | **可用。** Tools — DeepSeek Harness — Start session |
| 状态命令 | 可用 |
| `str_replace_editor`（create、insert、str_replace、view） | **已验证**：在一个挂载了该工具的会话上。`create`、`str_replace` 和 `insert` 都发送了正确的前后内容；`view` 根本没有到达 bridge，这正是读取豁免在起作用 |

agent 拿到的工具清单：

| 工具 | 读取 | 写入 |
| --- | --- | --- |
| `mcp__vs__get_environment` | 解决方案路径、工作区文件夹、进程 id | 无 |
| `mcp__vs__get_open_files` | 编辑器中的文件，并标出当前活动的那一个 | 无 |
| `mcp__vs__get_current_selection` | 选中的文本，及其所在文件和行范围 | 无 |

这三个都是只读的。唯一能影响文件的是闸门，而闸门本身不产生任何影响 —— 它展示一个 diff，然后返回你的裁决。

关于 bridge 暴露了什么、没有暴露什么，见 [SECURITY.md](SECURITY.md)。

## 环境要求

- Visual Studio 2022 17.14 或更高版本（开发时用的是 Visual Studio 2026 18.10）
- .NET Framework 4.8
- DeepSeek Harness：`npm install -g @deepseek-ai/dsh`
- TUI 启动器：`npm install -g @deepseek-harness-tui/dsh-tui`

## 安装

```powershell
& "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" `
  .\artifacts\DeepSeekHarness.VisualStudio.vsix
```

按你的版本调整路径。之后重启 Visual Studio。

## 使用

打开一个解决方案，然后：

> **Tools — DeepSeek Harness — Start session**

这会打开一个终端标签页，里面运行着挂了闸门的 DeepSeek Harness。在该会话里 agent 可以以 `mcp__vs__*` 调用 Visual Studio 工具，而它提议的每一次文件修改都会先打开在 Visual Studio 的 diff 窗口中。

要在别的地方运行会话，就手动挂载同一个 patch：

```powershell
dsh-tui --patch "$env:LOCALAPPDATA%\DeepSeekHarness\vs-bridge\dsh-patch.yml"
```

这个 patch 每次 Visual Studio 启动时都会被重写，因为它带着 bridge 当前的端口和 token。命令本身不变。

### 菜单

| 命令 | 作用 |
| --- | --- |
| **Start session** | 打开一个终端标签页，运行带闸门的会话 |
| **Status…** | 报告闸门是否已就绪，以及未就绪时缺什么 |
| **Open log** | 打开 `%LOCALAPPDATA%\DeepSeekHarness\vs-extension.log` |
| **Clean up installed files…** | 删除本扩展写入的文件夹。**可选** —— 见下 |

**关于 Clean up installed files：** 想停用这个扩展并不需要它。从 *Extensions → Manage Extensions* 卸载时就已经移除了全部能力：没有 Visual Studio 就没有 bridge，会话会以无闸门的方式运行，磁盘上也没有任何东西会自行启动。剩下的只有一个插件文件、一个启动脚本和一个日志 —— 是 `%LOCALAPPDATA%\DeepSeekHarness\` 下几个不会自己运行的文件。

VSIX 卸载时无法执行代码，所以它没法替你删掉那个文件夹。这条命令是删除它的唯一途径，它是为那些想让这个文件夹消失的人准备的，而不是必需步骤。它会先请求确认，并先说明是哪个文件夹。

## 工作原理

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

agent 通过 MCP 拿到 Visual Studio 的状态：扩展在同一个 loopback 端点上提供 `/mcp`，并用 `dsh-mcp-client` 注册自己。

### 闸门是本仓库自己维护的一个插件

`packages/dsh-plugin-vs-gate` 是一个 DeepSeek Harness 插件。扩展把它嵌进 VSIX，写到 `%LOCALAPPDATA%\DeepSeekHarness\dsh-plugin` 下，并从生成的 patch 里挂载它。

它说的是 harness 自己的 `tools/pre-execute` 契约，而不是 Claude Code 的 `hookSpecificOutput` 格式。早期版本借用 `@deepseek-ai/dsh-hooks-claude-code`，用一个 PowerShell 脚本去驱动它；换掉它的理由值得记下来，因为其中两条是花了很久才看出来的 bug：

**1. 包目录无法被导入。** patch 用一个以包名结尾的 URL 指定了借来的那个包。Node 对此会回 `ERR_UNSUPPORTED_DIR_IMPORT` —— 只有裸说明符才会走包解析 —— 而 harness 把这个失败报成一行 `entry did not activate`，然后继续往下走。闸门就这么不见了，而同一个 patch 里的 MCP 条目却一直在正常工作，所以集成的每一部分看上去都很健康。挂载失败就该大声说出来；一个失败时静默放行的组件，比一个干脆失败的组件更糟。

**2. 进程外就意味着靠猜。** 脚本只能看到工具的入参，所以它必须自己重建结果。这带来了两个缺陷，每一个都会给审查者展示一个毫无改动的 diff，而这比没有 diff 更糟：一处命名冲突 —— `str_replace_editor` 的 `create` 和 `insert` 命令不带 `old_str`；以及一次 CRLF 转换 —— 往一个本来就是 CRLF 的搜索串里插入了回车符。插件在进程内运行并直接读文件，所以提议是用工具即将写入的那同一批字节构造出来的。

**3. 失败不能看起来像成功。** 脚本是失败即放行的：bridge 不在，每一次编辑就都不经审查直接落盘。插件则回答 `ask`，把这次调用交给 harness 自己的权限流程。「我没法给你看这个改动」和「这个改动没问题」不是一回事。

### 这个插件也可以单独安装

`packages/dsh-plugin-vs-gate` 声明了 `dsh.bundle` manifest，所以可以用 DeepSeek Harness 自己的命令安装：

```sh
dsh plugin add github:VSworder/deepseek-harness-visual-studio#packages/dsh-plugin-vs-gate
```

这是给「在自己的终端里跑 DeepSeek Harness」而不是走 **Start session** 的场景用的。窗口仍然来自扩展 —— loopback bridge 归它所有 —— 所以 Visual Studio 必须开着并装好扩展。但不需要由扩展来*启动*这个会话。

连不上 Visual Studio 时，插件什么都不说，harness 会把这次调用当成它没装一样处理。这是有意为之：对于一个用户为了让编辑可被审查而装上的插件，每次编辑都弹提示，比保持沉默更糟。扩展启动的会话是例外 —— 那里环境变量已经设好，窗口是被期待的，闸门打不开窗口时就会明说，而不是不经审查地写入。

### 还有两个问题值得了解

**1. 有歧义的编辑会被拒绝，而不是送去审查。** harness 会拒绝 `old_string` 匹配到多处而又没设 `replace_all` 的 `edit`，也会拒绝完全匹配不上的那种。插件能识别这两种情况，并选择不介入闸门，于是什么都不会写入，审查者也绝不会看到一个不可能发生的改动。

**2. 这里的一切都不会装进你的 DeepSeek Harness profile。** 插件待在扩展自己的目录下，patch 从那里挂载它。属于用户的 harness 配置 —— approval policy、sandbox policy、profile 依赖 —— 一概不动。

## 测试

[docs/testing.md](docs/testing.md) 是人工检查清单：它覆盖自动化测试够不到的部分，而这些也正是本项目里失败得最无声的部分 —— harness 到底有没有挂载闸门、diff 窗口会不会出现、拒绝是否真的拦住了写入。每一步都写明失败长什么样，因为这个项目遇到的每一次失败，从外面看都像是成功。

自动化测试套件：

```powershell
node tests/plugin-rebuild.test.mjs     # the proposal the gate builds: 26 checks
node tests/mcp-sdk-test.mjs <port> <token>   # /mcp against the real MCP client library
```

## 构建

需要 **Visual Studio extension development** 工作负载。

```powershell
.\build.ps1                 # Release build + package
.\build.ps1 -Install        # build, then install into the local Visual Studio
```

VSIX 会落在 `artifacts\` 下。Visual Studio 的位置用 `vswhere` 定位；可用 `-VsInstallRoot` 覆盖。

## 仓库结构

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

`DeepSeekHarness.Bridge` 刻意不依赖 Visual Studio，这样协议测试就不需要把 IDE 拉进回路。大部分风险都在这里，`tests/BridgeHarness.cs` 和 `tests/mcp-sdk-test.mjs` 直接对它做验证。

## 另见

| 文档 | 涵盖内容 |
| --- | --- |
| [CHANGELOG.md](CHANGELOG.md) | 每个版本里有什么，以及那些不值得重犯的错误 |
| [SECURITY.md](SECURITY.md) | bridge 暴露了什么、token 值多少、以及扩展不会做什么 |
| [docs/testing.md](docs/testing.md) | 自动化测试够不到之处的人工检查清单 |
| [docs/protocol.md](docs/protocol.md) | 插件与扩展之间的 `/permission` 契约 |

## License

MIT
