# adesk-pro

**给 AI agent 用的 Windows 桌面操作工具** — 通过操作系统无障碍树读写原生应用。不截图、不做像素匹配、循环里没有视觉模型。

[English](README.md) | 简体中文

## 为什么写这个

[vercel-labs/agent-desktop](https://github.com/vercel-labs/agent-desktop) 验证了这条路:把无障碍树以 JSON 交给 agent,它就能不开截图驱动桌面应用。那个项目是三个平台适配器加起来约 36 万行 Rust。

我想知道 Windows 单平台上换个下注方式能走多远:

- **managed UIA 替代手写 COM interop**。`System.Windows.Automation` 把难啃的部分都包好了,整个核心约 2200 行 C#。
- **MCP 服务器进程内 dispatch**。每个工具调用走和 CLI 同一张命令注册表——没有每次调用的子进程创建,没有全局文件锁,ref 存储就是服务器进程内存。
- **MCP 响应只带一份 envelope**。完整 `structuredContent` 加一行摘要文本,不再复制第二份 JSON。
- **一张命令表**派生 CLI 解析、`--help` 和 MCP 工具 schema。

## 实机演示

```
$ adesk-pro snapshot --app Notepad -i
{"ok":true,"data":{"snapshotId":"sn972n15auotk","refCount":18,"complete":true,
  "tree":{"role":"窗口","name":"无标题 - 记事本","ref":"@sn972n15auotk:e1","children":[
    {"role":"文档","name":"文本编辑器","ref":"@sn972n15auotk:e2","patterns":["scroll"]}, ...]}}}

$ adesk-pro type --ref @sn972n15auotk:e2 --text "adesk-pro roundtrip 你好"
{"ok":true,"data":{"resolvedBy":"scope-path","how":"keyboard","delivered":true,"value":"adesk-pro roundtrip 你好"}}

$ adesk-pro press --app 记事本 --keys ctrl+a
$ adesk-pro press --app 记事本 --keys ctrl+c
$ adesk-pro clipboard-get
{"ok":true,"data":{"text":"adesk-pro roundtrip 你好"}}
```

快照 → 操作 → 验证,循环在调用方 agent 手里;adesk-pro 内部没有模型。

## 安装

Windows 10 1809+,需要 .NET 10 运行时。

```
dotnet publish src/AdeskPro/AdeskPro.csproj -c Release -r win-x64 --self-contained false -o publish
```

## 接入 MCP

```json
{ "mcpServers": { "adesk-pro": { "command": "publish/adesk-pro.exe", "args": ["mcp"] } } }
```

工具名和 CLI 命令一一对应(`snapshot`、`click`、`type`…),结果返回完整 `structuredContent` 加一行摘要文本。

## 命令行

17 个命令:`status list-apps list-windows snapshot find get click toggle expand select type set-value press scroll focus clipboard-get clipboard-set`,外加 `mcp`。

每条命令输出一个 JSON envelope(`ok` + `data`/`error`)。错误带 `disposition`:只有 `retry: safe`(未送达)允许重试——其余代表动作可能已落地,先读活体状态再决策。

## ref 语义

ref 形如 `@<snapshotId>:e<N>`,动作前按三步活体重定位:

1. 从窗口沿 scope-path 下降
2. UIA **RuntimeId** 钉扎——`find` 找到的重复控件也因此可寻址
3. 身份匹配(pid + role + automationId + name + className);零匹配 → `STALE_REF`,多个 → `AMBIGUOUS_TARGET`

遍历有预算(深度 30 / 兄弟 2000 / 节点 5000 / 5 秒 deadline),触顶会标记 `complete: false` 而不是悄悄截断。`STALE_REF` 返回里带诊断:活体与保存身份字段的逐项对比。

## 现状

- 目前仅 Windows。`ISource` 接缝就是 macOS(AX API)和 Linux(AT-SPI)适配器的插槽。
- 17 个命令;MCP 顺序 dispatch(agent 循环本身是串行的,并行在路线图上)。
- managed UIA wrapper 不支持单调用超时——provider 卡住会拖住命令,COM 层超时在路线图上。
- 30 个单元测试;walker 对着 fake source 跑,和 UIA 适配器走同一批分支。

## 与 agent-desktop 对比

| | agent-desktop | adesk-pro |
|---|---|---|
| 语言 | Rust(Windows 层约 7.7 万行 COM interop) | C#(managed UIA 上约 2200 行核心) |
| MCP 调用模型 | 每次工具调用 spawn 子进程 | 进程内 dispatch,纯内存状态 |
| MCP 响应 | envelope 同时放进 text + structuredContent | structuredContent + 一行摘要 |
| Windows 孪生元素 | 相同元素拒绝给 ref | `find` 可指向它们,解析时钉 RuntimeId |
| 平台 | macOS / Windows / Linux | Windows |
| 命令数 | 57 | 17(MVP) |

agent-desktop 是更完整的工具;adesk-pro 是一个更小的赌注,架构是我觉得更容易推理的那一种。两者都开源,按需取用。

## License

MIT
