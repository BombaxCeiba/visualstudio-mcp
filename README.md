# vs-mcp

> 让 AI 拥有和人类开发者一样的 Visual Studio 能力——构建、调试、符号导航，全部经 MCP 暴露给 AI agent。

`vs-mcp` 是一个 Visual Studio 扩展（VSIX），它在 IDE 内部启动一个 MCP（Model Context Protocol）HTTP 服务器，把你电脑上的 Visual Studio 变成 AI agent 可以驱动的后端。今天它已经暴露**构建、调试器、符号导航**三大类能力，路线图上还有 IDE 的更多感知。

<!-- TODO: demo GIF — AI 设断点 → 启动调试 → 命中 → 读局部变量 → 继续 -->

---

## 为什么需要它

今天的 AI 编程工具（Claude Code、Cursor、Cline）能读写代码，但它们**看不到** IDE 看得到的东西：

- AI 改完一份 C++/C# 文件，没人验证它能不能编译，就提交了。
- AI 想知道 `Foo::bar` 到底解析到哪个定义，只能 `grep` 然后猜。
- AI 想知道程序跑起来之后某个变量的值，只能让你自己去看。

`vs-mcp` 通过把 Visual Studio 的能力暴露给 AI 来解决这些事——**让 AI 用和人类一样的 IDE 感知**。今天已经落地构建（触发编译、读 build 输出）、调试器控制（设断点、单步、读栈、求值）、符号导航（find_symbol / get_type_hierarchy / go_to_definition），路线图上是语法诊断以及最终目标——**编辑后强制语法检查的闭环**。

---

## 现在能做什么

暴露 **22 个 MCP 工具**（含 1 个可选项 `go_to_definition`），按功能分组如下。

### 构建与编译

| 工具 | 作用 |
|---|---|
| `build_solution` | 触发整个解决方案构建并等待完成（构建期间推送日志通知保活并转发实时进度，长构建不会超时） |
| `get_build_output` | 读取 VS 输出窗口 Build 面板的切片视图（编译错误/警告的权威来源） |

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
| `get_local_variables` | 获取当前栈帧的局部变量（带字符预算，超限会提示下钻） |
| `get_call_stack` | 获取调用栈 |
| `evaluate_expression` | 在当前上下文求值任意表达式 |
| `get_variable_detail` | 展开某个变量的子节点（递归下钻） |

### 符号导航

| 工具 | 作用 |
|---|---|
| `find_symbol` | 按名字子串搜索符号（用 VS 语义模型，比 grep 准） |
| `get_type_hierarchy` | 返回类型的祖先链、派生类、兄弟类型（影响分析） |
| `go_to_definition` | 解析指定位置的符号定义（可选，需在 Tools → Options 启用） |

> 工具名称遵循 MCP 惯例使用 `snake_case`。所有返回值都是结构化的 JSON（typed DTO），而不是手拼的字符串——AI 拿到的是稳定、schema 友好的数据。

---

## 快速开始

### 1. 安装 VSIX

从 [Releases](../../releases) 下载最新的 `.vsix`，双击安装（需要 Visual Studio 2022 17.x 或 Visual Studio 2026 18.x，.NET Framework 4.8）。

### 2. 启动 Visual Studio

扩展会在 VS 启动时自动加载（包括无解决方案状态、CMake/打开文件夹场景），在本地回环启动 MCP HTTP 服务器：

```
http://127.0.0.1:43210/mcp/
```

服务只绑定到 `127.0.0.1`，**永远不会监听外部网络**。

### 3. 配置你的 AI 客户端

以 Claude Code 为例，在你的 MCP 配置中加入：

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

---

## 工作原理

```
┌──────────────┐      MCP (JSON-RPC over HTTP)     ┌─────────────────────┐
│  AI Agent    │ ◄──────────────────────────────► │  Visual Studio      │
│ (Claude Code │      http://127.0.0.1:43210/mcp   │  ┌───────────────┐  │
│  / Cursor /  │                                   │  │ vs-mcp VSIX   │  │
│  Cline / …)  │                                   │  │ ┌───────────┐ │  │
└──────────────┘                                   │  │ │McpHttpSvr │ │  │
                                                   │  │ └─────┬─────┘ │  │
                                                   │  │       │       │  │
                                                   │  │ ┌─────▼─────┐ │  │
                                                   │  │ │ Facades   │ │  │
                                                   │  │ │ Debug/Sym │ │  │
                                                   │  │ │ (COM/DTE) │ │  │
                                                   │  │ └───────────┘ │  │
                                                   │  └───────────────┘  │
                                                   └─────────────────────┘
```

扩展通过 EnvDTE / `IVsDebugger` COM 接口驱动 VS 自带的调试器，通过 VS 语言服务的 Object Model 查询符号；构建走 `SolutionBuild`，构建输出读 Output Window 的 Build 面板。所有 COM 调用都正确切换到 VS 主线程，调试会话切换时重新获取 COM 对象（避免 RCW 失效）。

---

## 路线图

`vs-mcp` 的终局是**让 AI 拥有和人类开发者一样的 IDE 感知**，并强制把这种感知接回编辑闭环：

| 状态 | 能力 | 价值 |
|---|---|---|
| ✅ 已完成 | **构建控制** — 触发构建、读 build 输出 | AI 改完代码能验证编译 |
| ✅ 已完成 | **调试器控制** — 断点、单步、调用栈、局部变量、表达式求值 | AI 能看到运行时状态 |
| ✅ 已完成 | **符号导航（部分）** — find_symbol / get_type_hierarchy / go_to_definition | AI 用 VS 语义模型找定义，不再猜 |
| 🚧 规划中 | **语法诊断** — 直接读 VS 的编译/语义错误，不是 grep | 比 grep 准，覆盖 C++/C#/Python/… |
| 🚧 规划中 | **符号导航扩展** — find-references / workspace symbols | 影响分析、重构场景 |
| 🔮 远期 | **post-tool-use 钩子** — 编辑后强制语法检查 | AI 改完代码立刻看到编译错误并自愈 |
| 🔮 远期 | **工程级 MCP/hook 自动配置** — 读解决方案目录下的 `.claude/` 等配置自动生成 | 项目内开箱即用，无需手配 |

**post-tool-use 强制语法检查**是这套设计最锋利的一环——它直接回应了 AI 编程最大的痛点：AI 改完代码没人验证就提交。当 diagnostics 落地（构建控制 + 符号导航已可用），AI 编辑代码后会立刻看到 VS 编译器的真实反馈，自己修掉错误再继续。

---

## 配置参考

| 项 | 默认值 | 说明 |
|---|---|---|
| 监听端口 | `43210` | 当前在扩展内硬编码（避开 Windows 排除端口段） |
| 绑定地址 | `127.0.0.1` | 仅回环，不暴露到网络 |
| 鉴权 | 关闭 | 可选 Bearer token（`McpAuthMiddleware`） |
| 传输协议 | Streamable HTTP | MCP 标准传输 |
| 并发 | 单会话串行 | 同一时刻只处理一个工具调用（v1 契约） |

日志写入 VS 的 **输出窗口 → "VS MCP" 面板**（Information 级别及以上），错误同时写入 `IVsActivityLog`。

---

## 已知限制

- **单会话**：同一时刻只服务一个 MCP 客户端、串行处理工具调用（v1 设计契约）。
- **调试器宿主依赖**：工具调用需要在 VS 进程内执行；扩展未运行时 MCP 端点不可达。
- **语言覆盖**：调试器工具本身语言无关（VS 支持的都能调），核心验证场景是 C++/CMake 与 C#。

---

## 贡献

欢迎 Issue 和 PR。开发需要本地装有 Visual Studio 2022 或 2026；用 MSBuild（不是 `dotnet build`）构建解决方案 `src/VsMcp.sln`——VS SDK 的 VSIX 打包目标只在完整 MSBuild 下可用。

```
msbuild src\VsMcp.sln /p:Configuration=Debug
```

测试：

```
dotnet test src\VsMcp.sln
```

> 注：`DebuggerFacadeTests` 中触及 `DebuggerFacade` 静态初始化的用例，需要在能解析 `Microsoft.VisualStudio.Shell.*` 程序集的环境下运行（IDE 内或 VS Test Host）；纯逻辑测试在任意 `dotnet test` 宿主下都能跑。

---

## 许可证

MIT（LICENSE 文件待添加）。
