# adesk

**Desktop use for AI agents on Windows — through the OS accessibility tree, no screenshots, no pixel matching.**

adesk 让 AI agent 通过 Windows UI Automation 读取和操作桌面应用：读出带 ref 的结构化树，对 ref 执行点击/输入/滚动，再读一遍验证结果。观察-行动-验证的循环在调用方 agent 手里；adesk 自己不带模型。

它是 [vercel-labs/agent-desktop](https://github.com/vercel-labs/agent-desktop) 的**另架构实现**：同样的核心思想，不同的四条架构决策。

## 与 agent-desktop 的架构差异

| 决策 | agent-desktop | adesk |
|---|---|---|
| MCP 调用模型 | 每次 tools/call 重新 spawn CLI 子进程 | **常驻进程内 dispatch**（零进程创建、零磁盘往返） |
| MCP 响应体 | envelope 同时放进 structuredContent 和 text（双份） | structuredContent 全量 + text 仅一行摘要 |
| 状态与并发 | 全局 RefStore + 2s 文件锁 | **按 session 隔离**；MCP 模式纯内存、无文件锁 |
| 命令定义 | clap 命令树派生 | 一张命令注册表派生 CLI 解析、--help、MCP schema |
| 语言 | Rust（COM interop 手写，Windows 层 7.7 万行） | C#（.NET 官方 UIA wrapper，核心 <5k 行） |

**Windows 上的额外能力**：动作解析优先钉住 UIA RuntimeId 再退回身份匹配——agent-desktop 在 Windows 上对孪生元素直接拒绝给 ref，adesk 在"捕获时唯一"的元素上可以跨 UI 重建存活（RuntimeId 失效则退回唯一身份匹配，仍歧义才报 `AMBIGUOUS_TARGET`）。

## 安装

需要 Windows 10 1809+ 和 .NET 10 运行时（或直接用自包含发布）。

```
dotnet build src/Adesk/Adesk.csproj -c Release
dotnet publish src/Adesk/Adesk.csproj -c Release -r win-x64 --self-contained false -o publish
```

## 接入 MCP

```json
{ "mcpServers": { "adesk": { "command": "publish/adesk.exe", "args": ["mcp"] } } }
```

工具名与 CLI 命令一一对应（`snapshot`、`click`、`type`…），返回的 envelope 同时提供 `structuredContent` 和一行摘要文本。

## CLI 速览

```
adesk list-apps
adesk snapshot --app 记事本 -i          # 交互元素带 ref 的树
adesk click @s8f3k2p9:e12              # 对 ref 执行默认动作
adesk snapshot --app 记事本 -i          # 再读一遍，验证
adesk find --app 记事本 --name 保存
adesk type @s8f3k2p9:e3 --text "hello"
adesk press --app 记事本 ctrl+s
adesk status
```

命令集（17 个）：`status list-apps list-windows snapshot find get click toggle expand select type set-value press scroll focus clipboard-get clipboard-set`，以及 `mcp`。每条命令输出一个 JSON envelope（`ok` + `data`/`error`），错误带 `disposition`：**只有 `retry: safe`（未送达）的动作才允许重试**，其余先读活体状态再决策——防止双击、双提交。

## ref 语义

- ref 格式 `@<snapshotId>:e<N>`，绑定到所属快照
- 动作前重新定位元素：先按 **RuntimeId** 钉扎，失败则按（pid+role+automationId+name+className）唯一匹配，仍歧义报 `AMBIGUOUS_TARGET`，找不到报 `STALE_REF`
- 快照默认带预算（深度 30、兄弟 2000、节点 5000、deadline 5s），触顶标记 `complete: false` 而不是悄悄截断
- `--skeleton` 把第 2 层起的容器子树摘要成 `children_count`；`--root` 深钻（路线图）

## 路线图

- [ ] `--root @ref` 局部深钻与 ref 替换
- [ ] headed 鼠标操作（拖拽、hover、右键，SendInput 路径已就绪）
- [ ] UIA COM 直连超时控制（managed wrapper 无法设置单调用超时，长卡顿场景需要 COM 层兜底）
- [ ] screenshot / wait-for / batch
- [ ] macOS / Linux 适配（架构上 ISource/ITreeSource 抽象已就位）

## 设计取舍

- **顺序 dispatch**：MVP 阶段 MCP 调用串行执行（agent 循环本身就是串行的），换取 UIA COM 单元安全与实现简单
- **状态落盘按需**：MCP 模式纯内存；CLI 单发模式把快照 identity 存到 `%LOCALAPPDATA%\adesk\sessions\<name>\`，跨进程解析 ref
- **fail-closed**：没有语义路径的动作直接报错，绝不偷偷转物理输入

## License

MIT
