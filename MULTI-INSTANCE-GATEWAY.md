# Multi-Instance Gateway: Design Document

> Support multiple Visual Studio instances behind a single MCP port via an independent Gateway process.

## Problem

当前每个 VS 实例的 `VsDebuggerMcpPackage` 硬编码绑定 `127.0.0.1:43210`。第二个 VS 实例启动时 `HttpListener.Start()` 抛 `HttpListenerException`，MCP 服务不可用。

根因：`HttpListener` 是独占式 TCP 绑定，同一端口只能被一个进程持有。当前架构里每个 `devenv.exe` 各自启动一个 `McpHttpServer`，端口冲突不可避免。

## Solution: Gateway + NamedPipe Multiplexer

引入一个独立的 Gateway 进程（`VsMcpGateway.exe`），独占 `:43210` 端口，作为所有 MCP 客户端的唯一入口。每个 VS 实例不再直接开 HTTP 端口，而是开一个 `NamedPipeServer`，Gateway 通过管道将请求路由到对应的 VS 实例。

### Architecture

```
                                     ┌──────────────────────────────┐
   MCP Client ──HTTP :43210─────────►│  Gateway 进程 (独立存活)       │
                                     │  HttpListener :43210          │
                                     │                               │
                                     │  Session 表:                   │
                                     │    sess-1 → PID 1234           │
                                     │    sess-2 → PID 5678           │
                                     │                               │
                                     │  路由表 (VS 实例注册):           │
                                     │    PID 1234 → pipe-1234        │
                                     │    PID 5678 → pipe-5678        │
                                     │                               │
                                     │  Gateway 级工具:               │
                                     │    list_vs_instances           │
                                     │    select_vs_instance          │
                                     └──────┬───────────────┬────────┘
                            NamedPipe      │               │    NamedPipe
                     ┌─────────────────────┘               └──────────────────┐
                     ▼                                                        ▼
            ┌──────────────────┐                                    ┌──────────────────┐
            │  VS-1 (PID 1234) │                                    │  VS-2 (PID 5678) │
            │  PipeServer      │                                    │  PipeServer      │
            │  pipe: vs-mcp-   │                                    │  pipe: vs-mcp-   │
            │       1234       │                                    │       5678       │
            │  心跳 ──► Gateway │                                    │  心跳 ──► Gateway │
            └──────────────────┘                                    └──────────────────┘
```

## Components

### 1. VsMcpGateway.exe (新建独立项目)

一个轻量的 console exe，无窗口（`CREATE_NO_WINDOW`）。职责：

- 独占 `http://127.0.0.1:43210/mcp/` 端口
- 维护 VS 实例路由表（PID → NamedPipe 连接）
- 维护 MCP session 绑定表（session-id → PID）
- 对 MCP 客户端暴露统一的 MCP endpoint
- 注入 Gateway 级工具（`list_vs_instances`、`select_vs_instance`）
- 心跳检测（双向：VS→Gateway 检测 Gateway 存活；Gateway→VS 检测 VS 存活）
- 全部 VS 实例掉线后宽限 30 秒自杀（防孤儿进程）

### 2. VS 插件端改动 (VsDebuggerMcpPackage)

- 移除 `HttpListener`，替换为 `NamedPipeServerStream`
- Pipe 名称：`\\.\pipe\vs-mcp-{PID}`
- 启动时检测 Gateway 是否存活，不存在则拉起
- 向 Gateway 注册实例信息（PID、solution info）
- 监听 `IVsSolutionEvents`，solution 变化时通知 Gateway 更新路由表
- 定期心跳（每 5 秒），检测 Gateway 断连后抢占式拉起
- VS Dispose 时不杀 Gateway 进程

### 3. Gateway 级工具 (Gateway 自行处理，不转发)

#### `list_vs_instances`

返回所有在线 VS 实例列表。

```json
{
  "name": "list_vs_instances",
  "description": "Lists all running Visual Studio instances connected to the gateway. Returns PID, solution path, solution directory, and debugger state for each instance.",
  "readOnly": true
}
```

响应示例：
```json
{
  "instances": [
    {
      "pid": 1234,
      "solution": "D:\\projects\\MyApp\\MyApp.sln",
      "solutionDir": "D:\\projects\\MyApp",
      "debuggerState": "break",
      "vsVersion": "17.10"
    },
    {
      "pid": 5678,
      "solution": null,
      "solutionDir": null,
      "debuggerState": "design",
      "vsVersion": "18.0"
    }
  ]
}
```

#### `select_vs_instance`

将当前 MCP session 绑定到指定 VS 实例。

```json
{
  "name": "select_vs_instance",
  "description": "Binds this MCP session to a specific VS instance. All subsequent tool calls route to it. Use list_vs_instances first to find available PIDs.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "pid": { "type": "integer", "description": "VS process ID to bind to" }
    },
    "required": ["pid"]
  }
}
```

## Session Binding: Resolution Flow

Gateway 收到非 `initialize` 且非 Gateway 级工具的 MCP 请求时，按以下优先级解析绑定：

```
                        Gateway 收到 MCP 请求
                                │
                                ▼
                    ┌───── ① Header? ─────┐
                    │   X-VS-Workspace     │
                    │                      │
               有 Header              无 Header
                    │                      │
                    ▼                      ▼
            路径前缀匹配          ┌── ② Session? ──┐
            VS 实例列表            │  Mcp-Session-Id │
                                   │   → PID         │
                 ┌─────────┐       │                 │
                 ▼         ▼      已绑定          未绑定
              命中 1    命中 0/多     │              │
              │         │           ▼              ▼
              ▼         ▼        转发请求    ┌─ ③ 1 实例? ─┐
           转发       返回 isError              │             │
        (无状态)      + 提示 agent             是            否
                                              │              │
                                              ▼              ▼
                                         自动绑定        ④ 拦截
                                         + hint         返回 isError
                                         转发请求        + 提示 agent
```

### ① X-VS-Workspace Header (优先级最高, 无状态)

MCP 客户端在项目级配置中设置 header：

```json
// .mcp.json
{
  "mcpServers": {
    "vs-debugger": {
      "url": "http://127.0.0.1:43210/mcp/",
      "headers": {
        "X-VS-Workspace": "D:\\projects\\MyApp"
      }
    }
  }
}
```

Gateway 拿到路径后对注册表里每个 VS 实例的 `solutionDir` 做**文件夹级别前缀匹配**：

- VS-1 solutionDir = `D:\projects\MyApp`, header = `D:\projects\MyApp` → 精确匹配
- VS-1 solutionDir = `D:\projects\MyApp`, header = `D:\projects\MyApp\src` → 子路径前缀匹配
- Header 值可以是文件夹路径或 `.sln` 文件路径（后者自动提取 dir）

**特性**：
- **无状态**：不改 session 表，每次请求独立解析
- agent 可中途换 header 指向不同 VS，无需 `select_vs_instance`
- 命中 0 个：返回 isError + 可用实例列表
- 命中多个：返回 ambiguity 错误，列出匹配的实例

**未命中时的拦截消息（面向 agent→user）**：

```
"No VS instance found for workspace 'D:\\projects\\MyApp'.
Ask the user to open the project in Visual Studio first,
or configure X-VS-Workspace to match an existing instance.

Available instances:
  PID 5678: D:\\projects\\Other\\Other.sln (state: design)"
```

### ② Session 绑定

通过 `initialize`（带 `_meta.vsPid`）或 `select_vs_instance` 建立。Gateway 维护：

```
Session 表 (ConcurrentDictionary):
  "sess-abc-123" → { Pid: 1234, BoundAt: "2026-07-02T...", Source: "auto-bind"|"select"|"initialize" }
```

VS 实例掉线时 session 标记为 `orphaned`，下次请求返回提示。

### ③ 单实例自动绑定 + Hint

**触发条件**（全部满足）：
- 无 `X-VS-Workspace` header
- Session 未绑定
- 恰好 1 个 VS 实例在线

**行为**：
- 自动绑定 session → 该 VS
- 标记 `hintPending = true`

**Hint 注入**：auto-bind 后的第一次 tool call 响应中，在 `content` 数组开头插入：

```json
{
  "content": [
    {
      "type": "text",
      "text": "[VS MCP] Auto-bound to VS instance PID 1234.\n"
              "Solution: D:\\projects\\MyApp\\MyApp.sln\n"
              "Solution dir: D:\\projects\\MyApp\n"
              "If you need to target a different VS instance, call list_vs_instances."
    },
    { "type": "text", "text": "design" }
  ]
}
```

Hint 只注入一次（第一次 tool call 后 `hintPending` 置 false）。

### ④ 拦截未绑定请求

多实例 + 无 header + session 未绑定时，任何工具调用（除 gateway 级工具）被拦截。

**拦截消息（面向 agent→user）**：

```
"Multiple VS instances are running but none is bound to this session.

Available instances:
  PID 1234: D:\\projects\\MyApp\\MyApp.sln (state: break)
  PID 5678: D:\\projects\\Other\\Other.sln (state: design)

To resolve this, ask the user to either:
1. Tell you which project to work on, then call select_vs_instance
2. Add X-VS-Workspace to the MCP server config in their project-level .mcp.json

Example .mcp.json the user can create:
{
  \"mcpServers\": {
    \"vs-debugger\": {
      \"url\": \"http://127.0.0.1:43210/mcp/\",
      \"headers\": { \"X-VS-Workspace\": \"D:\\\\projects\\\\MyApp\" }
    }
  }
}"
```

## Gateway Lifecycle Management

### 进程独立性

**核心原则：Gateway 的生命周期不绑定到任何 VS 实例。**

Windows 进程模型：子进程不会因父进程退出而自动死亡，除非父进程在 Job Object 内。`devenv.exe` 正常情况下不在 Job Object 内，因此 VS 退出后 Gateway 自然成为孤儿进程继续运行。

防御措施：
1. **VS Dispose 时不杀 Gateway**：只断开 pipe 连接，不碰 Gateway 进程
2. **`CREATE_NO_WINDOW`**：不弹 console 窗口
3. **Gateway 主动扫描 devenv.exe**：定期检查系统是否还有 VS 进程存活，全部退出则自杀（见下文）

### 心跳机制 (双向)

| 方向 | 间隔 | 目的 | 超时后行为 |
|------|------|------|-----------|
| VS → Gateway | 5s | VS 检测 Gateway 是否活着 | 超时 (15s, 3 次未收到) → 抢占式拉起新 Gateway |
| Gateway → VS | - | Gateway 检测 VS 是否活着 | 超时 (15s) → 从路由表删除该实例, 关闭对应 pipe |

Gateway→VS 方向不需要显式心跳：NamedPipe 连接断开即代表 VS 退出（OS 语义）。

### 抢占式 Gateway 拉起

当 VS 检测到 Gateway 心跳断开（超时）：

1. VS 实例各自尝试启动新 Gateway
2. 新 Gateway 尝试 `bind :43210`
3. **端口绑定是分布式锁**：第一个 `bind` 成功的 Gateway 存活
4. bind 失败的 Gateway 进程检测到端口已被占 → 优雅退出
5. 启动失败的 VS 实例检测到新 Gateway 已上线 → 连接过去

### Gateway 自杀 (双重防护)

Gateway 通过两种机制检测是否应该退出：

**机制 1：路由表空**（被动）
- 所有 VS 实例的 pipe 连接断开（路由表清空）
- 等待 30 秒宽限期
- 期间如有新 VS 连接，取消自杀
- 超时后自行退出

**机制 2：主动扫描 devenv.exe**（主动，更可靠）
- Gateway 每 10 秒扫描一次系统进程列表，检查是否还有 `devenv.exe` 在运行
- 如果**没有任何** `devenv.exe` 进程存活，立即开始自杀倒计时（10 秒）
- 倒计时期间如果检测到新的 `devenv.exe` 启动，取消自杀
- 超时后自行退出

```csharp
// 主动扫描逻辑 (Gateway 内部)
private async Task ProcessScanLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        var vsProcesses = Process.GetProcessesByName("devenv");
        bool anyVsAlive = vsProcesses.Length > 0;
        foreach (var p in vsProcesses) p.Dispose();

        if (!anyVsAlive)
        {
            // 没有 VS 在运行, 开始/继续自杀倒计时
            _noVsCountdown.TryStart(gracePeriod: TimeSpan.FromSeconds(10));
        }
        else
        {
            // 有 VS 在运行, 取消自杀倒计时
            _noVsCountdown.Cancel();
        }
    }
}
```

**为什么需要主动扫描？** 路由表空（机制 1）覆盖正常场景——VS 正常退出时 pipe 断开，路由表自然清空。但如果 VS 被 `taskkill /F` 强杀，或者进程崩溃导致 pipe 没有正常关闭，路由表可能残留过期条目。主动扫描 `devenv.exe` 进程是最终兜底，确保 Gateway 不会在没有任何 VS 的情况下成为永久孤儿。

## VS Instance Registration

VS 插件向 Gateway 注册时提供：

```json
{
  "pid": 1234,
  "pipeName": "\\\\.\pipe\\vs-mcp-1234",
  "solutionName": "MyApp.sln",
  "solutionDir": "D:\\projects\\MyApp",
  "solutionPath": "D:\\projects\\MyApp\\MyApp.sln",
  "vsVersion": "17.10"
}
```

### Solution 信息动态更新

solution 信息是动态的（用户可能在 VS 启动后才打开解决方案）。采用 **注册时提供 + 变更时推送**：

- VS 插件监听 `IVsSolutionEvents.OnAfterOpenSolution` / `OnAfterCloseSolution`
- solution 变化时通过 pipe 发一条 notification 给 Gateway 更新路由表
- Header 匹配前如果缓存超过 10 秒，Gateway 惰性刷新一次

## MCP Protocol Handling

Gateway 是 **MCP 感知的 multiplexer**，不是简单的 HTTP 反向代理。

### 请求处理流程

```
Gateway 收到 POST /mcp/:

1. 解析 Mcp-Session-Id (可能不存在 = 新连接)
2. 解析 JSON-RPC method

3. method == "initialize"?
   → 生成新 session-id
   → 返回 capabilities (含 gateway 注入的工具)
   → 不绑定 (延迟绑定策略)
   → 如果 _meta.vsPid 存在, 预绑定

4. method 是 gateway 级工具? (list_vs_instances / select_vs_instance)
   → 自己处理, 不转发

5. method == "tools/list"?
   → 转发到绑定的 VS (或第一个可用 VS)
   → 在响应的工具列表中注入 list_vs_instances / select_vs_instance
   → 返回合并后的列表

6. 其他 (tools/call)?
   → 按绑定解析流程 (①②③④) 确定 VS 实例
   → 转发 JSON-RPC 到对应 pipe
   → 如有 hintPending, 修改响应 content
```

### tools/list 注入

Gateway 透明地给 `tools/list` 响应追加两个工具描述。VS 端完全不知道这两个工具的存在。

### Streamable HTTP 兼容性

Gateway 重启会导致正在执行的 tool call 的 SSE 流被截断。由于 Gateway 设计为独立存活（不随 VS 退出），重启只在极端场景（Gateway 自身 bug/crash）发生。客户端重新 `initialize` 即可恢复。

## File Changes Summary

### New Files

| File | Description |
|------|-------------|
| `src/VsMcpGateway/VsMcpGateway.csproj` | Gateway 独立 exe 项目 |
| `src/VsMcpGateway/Program.cs` | Gateway 入口, HttpListener + 路由逻辑 |
| `src/VsMcpGateway/InstanceRegistry.cs` | VS 实例路由表管理 |
| `src/VsMcpGateway/SessionTable.cs` | MCP session 绑定表 |
| `src/VsMcpGateway/PipeRouter.cs` | NamedPipe 连接池 + 转发逻辑 |
| `src/VsMcpGateway/ProcessScanner.cs` | 定期扫描 devenv.exe 进程, 无 VS 时触发自杀 |
| `src/VsMcp/GatewayLauncher.cs` | VS 端拉起 Gateway 的逻辑 (CREATE_NO_WINDOW) |
| `src/VsMcp/HeartbeatClient.cs` | VS 端心跳发送 + 抢占逻辑 |
| `src/VsMcp/PipeMcpServer.cs` | VS 端 NamedPipe 替代 HttpListener |

### Modified Files

| File | Change |
|------|--------|
| `src/VsMcp/VsDebuggerMcpPackage.cs` | 用 PipeMcpServer 替换 McpHttpServer; 加入 GatewayLauncher + HeartbeatClient |
| `src/VsMcp/McpHttpServer.cs` | 保留但重构为接受 Stream 而非 HttpListener (复用核心 MCP 逻辑) |
| `src/VsMcp/DebuggerFacade.cs` | 加入 PID 和 solution info 查询 (IVsSolutionEvents 监听) |
| `src/VsMcp/source.extension.vsixmanifest` | 加入 Gateway exe 作为 Asset |

## Packaging

Gateway exe 打包进 VSIX 的 `<Asset>`：

```xml
<Asset Type="Microsoft.VisualStudio.VSIXContainer" Path="VsMcpGateway.exe" />
```

VSIX 安装时 Gateway exe 随插件部署到同一目录。VS 启动时从 `Assembly.GetExecutingAssembly().Location` 的同目录找到它。

## Decisions Log

| ID | Decision | Rationale |
|----|----------|-----------|
| MI-01 | Gateway 是独立 exe (非 DLL 内嵌 self-extract) | 清晰、可维护；VSIX Asset 打包 |
| MI-02 | NamedPipe 替代 per-instance HTTP | 端口不冲突；NamedPipe 断开即感知 VS 退出 |
| MI-03 | 端口绑定作为抢占分布式锁 | 无需额外协调协议，OS 语义保证唯一性 |
| MI-04 | 不使用 CREATE_BREAKAWAY_FROM_JOB; Gateway 靠主动扫描 devenv.exe 自杀 | VS 默认不在 Job Object 内, 无需防 Job kill; 主动进程扫描比 Job Object 处理更简单可靠, 确保无 VS 时 Gateway 不残留 |
| MI-05 | 四层绑定优先级 (Header > Session > Auto > Intercept) | 覆盖单实例零配置到多实例精确控制全场景 |
| MI-06 | Header 绑定是无状态的 | agent 中途换 header 不需要 select_vs_instance |
| MI-07 | Hint 只注入一次 (auto-bind 后首次 tool call) | 避免每次调用都注入冗余信息 |
| MI-08 | 拦截消息面向 agent→user (提示用户配置) | agent 不能修改自己的客户端配置，需引导用户操作 |
| MI-09 | Gateway 在 tools/list 响应中注入工具 | VS 端零改动，Gateway 透明 multiplexing |
| MI-10 | Solution info 注册时提供 + 变更时推送 | 高效缓存 + 实时更新 |

---
*Created: 2026-07-02*
