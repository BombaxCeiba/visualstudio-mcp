# vs-mcp

> 让 AI 拥有和人类开发者一样的 Visual Studio 能力——构建、调试、符号导航，全部经 MCP 暴露给 AI agent；多个 VS 实例共享同一个 MCP 端口。

`vs-mcp` 由一个 Visual Studio 扩展（VSIX）和一个独立的 Gateway 进程组成。Gateway 独占本地一个 MCP 端口，把**多个 VS 实例**统一到这一个入口后面；AI agent 只需连一个地址，就能驱动任意一个已打开的 VS——构建、调试、符号导航，以及在不同 VS 实例之间切换。

<!-- TODO: demo GIF — AI 设断点 → 启动调试 → 命中 → 读局部变量 → 继续 -->

---

## 为什么需要它

今天的 AI 编程工具（Claude Code、Cursor、Cline）能读写代码，但它们**看不到** IDE 看得到的东西：

- AI 改完一份 C++/C# 文件，没人验证它能不能编译，就提交了。
- AI 想知道 `Foo::bar` 到底解析到哪个定义，只能 `grep` 然后猜。
- AI 想知道程序跑起来之后某个变量的值，只能让你自己去看。
- 你同时开着两个 VS（一个调服务端、一个调客户端），AI 却只能连到其中一个。

`vs-mcp` 通过把 Visual Studio 的能力暴露给 AI 来解决这些事——**让 AI 用和人类一样的 IDE 感知**。今天已经落地构建（触发编译、读 build 输出）、调试器控制（设断点、单步、读栈、求值）、符号导航与调用关系（find_symbol / get_type_hierarchy / get_call_graph / go_to_definition），以及**多实例路由**（一个端口背后挂多个 VS，按工作区或会话精确路由）。

---

## 架构

```
                                     ┌───────────────────────────────┐
   MCP 客户端 ──HTTP :43210─────────►│  VsMcpGateway.exe（独立进程）   │
   (Claude Code /                    │  独占 127.0.0.1:43210/mcp/      │
    Cursor / Cline / …)              │  Session 表 + VS 实例路由表      │
                                     │  + Gateway 级工具注入           │
                                     └──────┬───────────────┬─────────┘
                              NamedPipe   │               │  NamedPipe
                            (\\.\pipe\vs-mcp-gateway)       │
                            ┌─────────────┘               └─────────────┐
                            ▼                                           ▼
                  ┌──────────────────┐                       ┌──────────────────┐
                  │  VS 实例 A        │                       │  VS 实例 B        │
                  │  PipeMcpServer    │                       │  PipeMcpServer    │
                  │   └ McpRequestProcessor（共享工具/调试/符号）│   └ 同上           │
                  └──────────────────┘                       └──────────────────┘
```

- **Gateway 是独立进程**（`VsMcpGateway.exe`），随 VSIX 一起部署。VS 启动时由扩展拉起，独占 `127.0.0.1:43210`，作为所有 MCP 客户端的唯一入口。
- **每个 VS 实例不再开 HTTP 端口**，而是开一条 NamedPipe 主动连入 Gateway（`\\.\pipe\vs-mcp-gateway`），首帧 `register` 自报 PID 与 solution 信息。这从根上消除了多实例的端口冲突。
- **Gateway 能看懂 MCP 协议、按请求智能路由，不是哑代理（无脑转发）**：它解析每条 JSON-RPC，判断"这次调用该交给哪个 VS"（靠 `X-VS-Workspace` header、会话绑定、或单实例自动绑定——下文「多实例路由」详述），把请求精准送到目标 VS；而 `list_vs_instances` / `select_vs_instance` 这两个多实例管理工具它自己处理，并在 `tools/list` 响应里把这两个工具透明地塞进去。
- **Gateway 的生命周期独立于任何 VS**：所有 VS 退出后它宽限自动退出（防残留）；它自己崩溃时 VS 会抢占式拉起一个新的（端口绑定作分布式锁，保证最终只剩一个）。

---

## 现在能做什么

暴露 **24 个 VS 工具**（含 2 个可选项：`go_to_definition`、`eval_csharp`）+ **2 个 Gateway 注入的多实例路由工具**，共 26 个。AI 调 `tools/list` 时会看到全部（可选项需在 Tools → Options 或编译时启用）。`initialize` 响应里的 `instructions` 字段会引导 agent：查代码结构（符号定位 / 调用关系 / 继承）时**优先用这些 VS 工具而非 grep**——C++ 多态、重载、模板让 grep 不可靠。

### 多实例路由（Gateway 级工具）

| 工具 | 作用 |
|---|---|
| `list_vs_instances` | 列出所有连到 Gateway 的 VS 实例（PID、solution 路径、目录） |
| `select_vs_instance` | 把当前 MCP 会话绑定到指定 VS 实例，后续工具调用都路由到它 |

### 构建与编译

| 工具 | 作用 |
|---|---|
| `build_solution` | 触发整个解决方案构建并等待完成（构建期间推送日志通知保活并转发实时进度，长构建不会超时） |
| `get_build_output` | 读取 VS 输出窗口 Build 面板，返回**纯文本**日志切片（一行 header + 原文，不转义换行；编译错误/警告的权威来源） |

### 调试会话与项目

| 工具 | 作用 |
|---|---|
| `get_debugger_state` | 查询调试器当前模式（design / break / running） |
| `get_session_info` | 获取已加载的解决方案和当前调试目标 |
| `search_project` | 在解决方案里按名称搜索项目 |
| `start_debugging` | 启动调试会话 |

### 断点管理

| 工具 | 作用 |
|---|---|
| `set_breakpoint` | 在指定文件:行设置断点 |
| `list_breakpoints` | 列出所有断点 |
| `delete_breakpoint` | 删除指定文件:行的断点 |
| `clear_all_breakpoints` | 清空全部断点 |

### 执行控制

| 工具 | 作用 |
|---|---|
| `continue_execution` | 继续运行（支持 `wait_for_break` 等待下次断点命中） |
| `step_into` | 单步进入 |
| `step_over` | 单步跳过 |
| `step_out` | 单步跳出 |
| `stop_debugging` | 停止调试 |

### 运行时观察

| 工具 | 作用 |
|---|---|
| `list_local_variables` | 列出当前栈帧的局部变量，**纯文本**（每行 `name (type) = value`，`{…}` 标有子项；浅层不展开，字符预算超限会提示下钻） |
| `get_call_stack` | 获取调用栈，**纯文本**（每帧一行 `#idx  module!Function  → RetType`） |
| `evaluate_expression` | 在当前上下文求值任意表达式，**纯文本**（`expr (type) = value`，对象成员缩进展示） |
| `get_variable_detail` | 展开某个变量的子节点（递归下钻） |

### 符号导航

| 工具 | 作用 |
|---|---|
| `find_symbol` | 按名字搜索符号：C++ 经 VC CodeStore（`IVCNavigateToFactory`，与 Ctrl+T 同源）、C#/VB 经 LSP。返回**每个符号 ±contextLines 行带行号的源码上下文**（纯文本，`▶` 标记符号行，`contextLines` 默认 10，可设 0 只看符号行）；`maxResults`/`maxChars` 限制输出（超限截断会提示调高 `maxChars`、减小 `contextLines` 或收窄 query） |
| `get_type_hierarchy` | 返回类型的祖先链、派生类、兄弟类型（影响分析） |
| `go_to_definition` | 解析指定位置的符号定义（可选，需在 Tools → Options 启用） |

### 调用关系（C++）

| 工具 | 作用 |
|---|---|
| `get_call_graph` | 查 C++ 函数的调用关系：`callers`（谁调用了它）或 `callees`（它调用了谁，默认）——VS 调用层次结构窗口同款后端（VC CallHierarchy API）。同名符号（重载、`.h` 声明 + `.cpp` 实现、各类同名方法）全部遍历、合并去重。callers 反向搜全 solution 较慢（热门函数 1-3 分钟），默认 `timeoutSeconds=180` + 搜索期间每 5s 推送 logging 心跳保活；纯虚接口声明不在 C++ 符号索引里，其 callers 在具体实现上查 |

### 动态执行（可选，开发调试用）

| 工具 | 作用 |
|---|---|
| `eval_csharp` | 在 VS 进程内动态执行任意 C#（Roslyn 编译），注入 `Package`/`JTF`，用于实时探查 VS 内部状态、反射读非 public 字段，**无需重编重装扩展**。返回**纯文本**（`输出`/`返回值` 或 `输出`/`错误` 段；脚本 return 值的 JSON 不被外层 JSON 二次转义）。等价于任意代码执行——仅本地开发构建含此工具，发布版 VSIX 完全不含（见下文「eval_csharp 编译开关」） |

> 工具名称遵循 MCP 惯例使用 `snake_case`。返回格式按"AI 如何消费"分两类：**阅读型**工具返回**纯文本**（源码、日志、调用栈、变量值——整体通读的内容，JSON 包裹既费 token 又把换行/引号转义掉）；**结构型**工具返回**结构化 JSON**（typed DTO——路径、坐标、操作结果这类要抽取字段填给下游工具的内容，稳定且 schema 友好）。返回纯文本的工具：`find_symbol` / `get_build_output` / `get_call_stack` / `list_local_variables` / `evaluate_expression` / `eval_csharp`；其余返回 JSON。

---

## 快速开始

### 1. 安装 VSIX

从 [Releases](../../releases) 下载最新的 `.vsix`，双击安装（需要 Visual Studio 2022 17.x 或 Visual Studio 2026 18.x，.NET Framework 4.8）。

### 2. 启动 Visual Studio

扩展在 VS 启动时自动加载（包括无解决方案状态、CMake/打开文件夹场景）。它会拉起独立的 `VsMcpGateway.exe`，由 Gateway 独占本地 MCP 端口：

```
http://127.0.0.1:43210/mcp/
```

Gateway 只绑定到 `127.0.0.1`，**永远不会监听外部网络**。开第二个 VS 时不会再起 Gateway——它会发现端口已被占用，直接复用现有 Gateway。

### 3. 配置你的 AI 客户端

以 Claude Code 为例，**推荐用 CLI 命令**（项目级，写到项目 `.mcp.json`，团队共享、提交进仓库）：

```bash
claude mcp add --transport http --scope project vsdebugger https://127.0.0.1:43210/mcp/
```

> 多实例场景想固定路由到某个 VS，加 `X-VS-Workspace` header（详见下文「多实例路由」）：

```bash
claude mcp add --transport http --scope project vsdebugger-backend https://127.0.0.1:43210/mcp/ \
  --header "X-VS-Workspace: D:\projects\Backend"
```

或者直接编辑 MCP 配置 JSON（手动加 `mcpServers` 条目）：

```json
{
  "mcpServers": {
    "vs-mcp": {
      "url": "http://127.0.0.1:43210/mcp/"
    }
  }
}
```

其他支持 Streamable HTTP 传输的 MCP 客户端（Cline、Continue、自研 agent 等）配置方式相同。配好后，让 AI "在 main 函数设个断点然后启动调试" 即可。

### 4.（可选）多实例与工作区路由

当你开着多个 VS 实例、希望某个 agent 固定路由到其中一个，最稳的方式是在 MCP 配置里带 `X-VS-Workspace` header 指向那个 solution 的目录：

```json
{
  "mcpServers": {
    "vs-mcp-backend": {
      "url": "http://127.0.0.1:43210/mcp/",
      "headers": { "X-VS-Workspace": "D:\\projects\\Backend" }
    },
    "vs-mcp-frontend": {
      "url": "http://127.0.0.1:43210/mcp/",
      "headers": { "X-VS-Workspace": "D:\\projects\\Frontend" }
    }
  }
}
```

这样两个 agent 各自固定打到对应的 VS，无需手动 `select_vs_instance`。

---

## 多实例路由：请求怎么落到正确的 VS

Gateway 收到一个非 `initialize`、非路由工具的请求时，按以下优先级解析目标 VS（四层，逐层兜底）：

```
① X-VS-Workspace header  ──命中 1 个──► 路由到它（无状态，每次请求独立解析）
   │
   │ 无 header / 命中 0 或多个 → 报错并给出可用实例列表
   ▼
② Session 绑定  ──该会话之前 initialize / select 过──► 路由到绑定的 VS
   │
   │ 未绑定
   ▼
③ 单实例自动绑定  ──恰好只有 1 个 VS 在线──► 自动绑定 + 首次响应注入一条 hint
   │
   │ 0 个或 ≥2 个
   ▼
④ 拦截  ──返回 isError + 提示用户配置 header 或调 select_vs_instance
```

- **① Header（无状态，最高优先级）**：`X-VS-Workspace` 的值（文件夹或 `.sln` 路径）与每个 VS 的 solution 目录做前缀匹配。agent 中途换 header 即可切换目标 VS，无需 `select_vs_instance`。
- **② Session**：通过 `initialize`（带 `_meta.vsPid` 预绑定）或 `select_vs_instance` 建立，Gateway 维护 session-id → VS 的映射。
- **③ 单实例自动绑定 + Hint**：只有一个 VS 在线且无任何绑定时，自动绑到它，并在之后的**首次**工具响应里注入一条提示文本（只这一次），告诉 AI 绑到了哪个实例。
- **④ 拦截**：多实例且无任何绑定时，工具调用被拦截，返回可用实例列表 + 配置指引，引导用户解决歧义。

`list_vs_instances` 和 `select_vs_instance` 永远不被拦截——它们是 Gateway 自处理的，不转发到任何 VS。

---

## 工作原理

- **传输**：Gateway 与 VS 之间用自定义的 NamedPipe 帧协议（4 字节大端长度前缀 + UTF-8 JSON）。帧类型包括 `request` / `head` / `data` / `end`（请求-响应，按 id 配对，支持同一 pipe 上多个会话并发 demux）、`register` / `solution-changed` / `heartbeat`（VS 主动推送的控制帧）。
- **MCP 感知**：Gateway 解析 JSON-RPC 的 `method`：`initialize` 生成 Gateway session-id 并捕获 VS 分配的 session-id 做双向映射；`tools/list` 转发到 VS 后在响应里注入两个路由工具；路由工具自处理不转发；其余按四层解析路由。
- **VS 能力驱动**：扩展通过 EnvDTE / `IVsDebugger` COM 接口驱动 VS 自带的调试器，通过 VS 语言服务的 Object Model 查询符号；构建走 `SolutionBuild`，构建输出读 Output Window 的 Build 面板。所有 COM 调用都正确切换到 VS 主线程，调试会话切换时重新获取 COM 对象（避免 RCW 失效）。
- **solution 动态更新**：VS 端订阅 `IVsSolutionEvents`，打开/关闭 solution 时主动推 `solution-changed` 帧，Gateway 路由表实时刷新，保证 ① Header 匹配和 `list_vs_instances` 不 stale。
- **生命周期**：VS 每 5s 经 pipe 发心跳探测 Gateway，连续失联 15s 即抢占式拉起新 Gateway（端口 bind 是分布式锁，多 VS 并发拉起只存活一个）；Gateway 路由表空 30s 或扫不到 `devenv.exe` 进程 10s 即自动退出，杜绝残留进程。

---

## 路线图

`vs-mcp` 的最终目标是**让 AI 拥有和人类开发者一样的 IDE 感知**，并强制把这种感知接回编辑闭环：

| 状态 | 能力 | 价值 |
|---|---|---|
| ✅ 已完成 | **构建控制** — 触发构建、读 build 输出 | AI 改完代码能验证编译 |
| ✅ 已完成 | **调试器控制** — 断点、单步、调用栈、局部变量、表达式求值 | AI 能看到运行时状态 |
| ✅ 已完成 | **符号导航** — find_symbol（C++/C#/VB，带源码上下文）/ get_type_hierarchy / go_to_definition | AI 用 VS 语义模型找定义，不再猜 |
| ✅ 已完成 | **调用关系** — get_call_graph（C++ callers/callees，VS CallHierarchy 后端） | AI 做重构影响分析、追踪控制流，不再 grep |
| ✅ 已完成 | **多实例 Gateway** — 一个端口后挂多个 VS，按工作区/会话路由 | 同时开多个项目不再端口冲突 |
| 🚧 规划中 | **语法诊断** — 直接读 VS 的编译/语义错误，不是 grep | 比 grep 准，覆盖 C++/C#/Python/… |
| 🚧 规划中 | **符号导航扩展** — find-references / workspace symbols | 影响分析、重构场景 |
| 🔮 远期 | **post-tool-use 钩子** — 编辑后强制语法检查 | AI 改完代码立刻看到编译错误并自愈 |
| 🔮 远期 | **工程级 MCP/hook 自动配置** — 读解决方案目录下的 `.claude/` 等配置自动生成 | 项目内开箱即用，无需手配 |

**post-tool-use 强制语法检查**是这套设计最锋利的一环——它直接回应了 AI 编程最大的痛点：AI 改完代码没人验证就提交。当 diagnostics 落地（构建控制 + 符号导航已可用），AI 编辑代码后会立刻看到 VS 编译器的真实反馈，自己修掉错误再继续。

---

## 配置参考

| 项 | 默认值 | 说明 |
|---|---|---|
| 监听端口 | `43210` | 由 Gateway 持有（当前硬编码，避开 Windows 排除端口段） |
| 绑定地址 | `127.0.0.1` | 仅回环，不暴露到网络 |
| 传输协议 | Streamable HTTP（客户端↔Gateway）+ NamedPipe（Gateway↔VS） | MCP 标准传输 + 进程间复用 |
| 鉴权 | 关闭 | 可选 Bearer token（`McpAuthMiddleware`，默认禁用） |
| 并发模型 | 多会话并发路由；同一 VS 实例内串行 | 不同 VS 可并行服务；单个 VS 的工具调用按 VS SDK 单会话串行 |
| 工作区路由 | `X-VS-Workspace` header | 无状态前缀匹配，可选 |

日志写入 VS 的 **输出窗口 → "VS MCP" 面板**（Information 级别及以上），错误同时写入 `IVsActivityLog`。**每次工具调用的回复也会原样打到该面板**（前缀 `← MCP 回复：`），方便立即观察 agent 实际收到的内容——**不截断**，面板看到的和 agent 收到的完全一致。Gateway 是无窗口进程，自身不写文件日志——运行状态用任务管理器看 `VsMcpGateway.exe`、`netstat -ano | findstr :43210`、或 Sysinternals `pipelist` 看 `vs-mcp-gateway`。

---

## 已知限制

- **依赖 VS 运行**：工具调用需要在 VS 进程内执行；没有任何 VS 实例运行时 Gateway 会宽限后自动退出，MCP 端点不可达。Gateway 不会、也无法自动启动 VS（它不知道该开哪个 solution）。
- **`list_vs_instances` 的 `debuggerState` 暂未实时**：当前返回注册快照（多为 null），实时调试状态留待后续增强。
- **同一 VS 实例内串行**：单个 VS 的工具调用受 VS SDK 单会话契约约束串行处理；不同 VS 实例之间可并行。
- **切换 VS 后需重新 initialize**：`select_vs_instance` 切换到一个尚未经本 Gateway 初始化的 VS 时，下一次请求会返回"需重新 initialize"的提示（Gateway 不擅自合成 initialize）。
- **语言覆盖**：调试器工具本身语言无关（VS 支持的都能调），核心验证场景是 C++/CMake 与 C#。


---

## 贡献

欢迎 Issue 和 PR。开发需要本地装有 Visual Studio 2022 或 2026；用 MSBuild（不是 `dotnet build`）构建解决方案 `src/VsMcp.sln`——VS SDK 的 VSIX 打包目标只在完整 MSBuild 下可用。

```
msbuild src\VsMcp.sln /p:Configuration=Release
```

本地构建默认**含 `eval_csharp`**（动态 C# 执行，开发调试用）。发布构建显式关闭——发布 VSIX 不含任意代码执行能力：

```
msbuild src\VsMcp.sln /p:Configuration=Release /p:EvalCsharpEnabled=false
```

> **eval_csharp 编译开关**：csproj 的 `<EvalCsharpEnabled>`（默认 `true`）控制 `EVAL_CSHARP` 编译常量。`EvalCsharpFacade`、`eval_csharp` 工具注册、`EnableEvalCsharp` 运行时开关、整条参数链路都用 `#if EVAL_CSHARP` 包裹——`/p:EvalCsharpEnabled=false` 时这些代码完全不编译进 VSIX。GitHub 发布管线（`.github/workflows/release.yml`）已默认带上此参数。

产物：`src/VsMcp/bin/Release/net48/VsMcp.vsix`（内含 `VsMcpGateway.exe` 及其依赖，部署后与 `VsMcp.dll` 同目录）。

测试（net48 + VS SDK 引用，用 VS 自带的 vstest.console）：

```
"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\Extensions\TestPlatform\vstest.console.exe" ^
  src\VsMcp.Tests\bin\Release\net48\VsMcp.Tests.dll
```

设计文档见 [MULTI-INSTANCE-GATEWAY.md](./docs/MULTI-INSTANCE-GATEWAY.md)。

---

## 许可证

Apache 2.0。
