# adesk-pro

**给 AI agent 用的 Windows 桌面操作工具** — 通过操作系统无障碍树读写原生应用。不截图、不做像素匹配、循环里没有视觉模型。

[English](README.md) | 简体中文

## 设计

agent 的循环是:把应用窗口读成带 ref(`@snapshot:eN`)的 JSON 树,对 ref 执行点击 / 输入 / 滚动,再读一遍验证。adesk-pro 就是这个工具,内部没有模型。四个设计决定:

- **全链路 managed UIA**。`System.Windows.Automation` 原生包装了 COM 接口,整个核心约 2600 行 C#——树遍历、ref 解析、模式调用、键鼠合成全含在内。
- **MCP 服务器进程内 dispatch**。每个工具调用走和 CLI 同一张命令注册表——没有每次调用的子进程创建,没有全局文件锁;MCP 模式下 ref 存储就是服务器进程内存。
- **一张命令表**。CLI 解析、`--help`、MCP 工具 schema 全部从同一张注册表派生,不存在第二个定义命令的地方。
- **带预算的诚实遍历**。每次树遍历都有预算(深度 30 / 兄弟 2000 / 节点 5000 / 5 秒 deadline),触顶标记 `complete: false` 而不是悄悄截断。

## 实机演示

```
$ adesk-pro snapshot --app Notepad -i
{"ok":true,"data":{"snapshotId":"sn972n15auotk","refCount":18,"complete":true,
  "tree":{"role":"窗口","name":"无标题 - 记事本","ref":"@sn972n15auotk:e1","children":[
    {"role":"文档","name":"文本编辑器","ref":"@sn972n15auotk:e2","patterns":["scroll"]}, ...]}}}

$ adesk-pro type --ref @sn972n15auotk:e2 --text "adesk roundtrip 你好"
{"ok":true,"data":{"resolvedBy":"scope-path","how":"keyboard","delivered":true,"value":"adesk roundtrip 你好"}}

$ adesk-pro press --app 记事本 --keys ctrl+a
$ adesk-pro press --app 记事本 --keys ctrl+c
$ adesk-pro clipboard-get
{"ok":true,"data":{"text":"adesk roundtrip 你好"}}
```

## 安装

Windows 10 1809+,需要 .NET 10 运行时。

```
dotnet publish src/AdeskPro/AdeskPro.csproj -c Release -r win-x64 --self-contained false -o publish
```

## 接入 MCP

```json
{ "mcpServers": { "adesk-pro": { "command": "publish/adesk-pro.exe", "args": ["mcp"] } } }
```

工具名和 CLI 命令一一对应;结果返回完整 `structuredContent` 加一行摘要,截图作为独立 image block 返回。

## 命令行

30 个命令:

- **观察**:`status` `list-apps` `list-windows` `snapshot` `find` `get` `screenshot`
- **操作**:`click` `toggle` `expand` `select` `type` `set-value` `press` `scroll` `mouse-click` `hover` `drag`
- **应用与窗口**:`launch` `close-app` `minimize` `maximize` `restore` `move-window` `resize-window` `focus`
- **同步与脚本**:`wait` `batch` `clipboard-get` `clipboard-set`

几个亮点:

- `snapshot --root @snap:eN` 对旧快照的一个子树重新深钻;子树内的 ref 会以拼接后的 scope path 重新发放,子树外全部保持有效。
- `wait` 轮询到窗口出现或 ref 变为 `enabled` / `visible` / `gone`——agent 脚本里不再需要固定 sleep。
- `batch` 在一个进程里顺序跑多条命令,支持 `stop-on-error`。
- 每条命令输出一个 JSON envelope(`ok` + `data`/`error`),错误带 `disposition`:只有 `retry: safe`(未送达)允许重试。

## ref 语义

ref 形如 `@<snapshotId>:e<N>`,动作前按三步活体重定位:

1. 从窗口沿 scope-path 下降
2. UIA **RuntimeId** 钉扎——`find` 找到的重复控件也因此可寻址
3. 身份匹配(pid + role + automationId + name + className);零匹配 → `STALE_REF`,多个 → `AMBIGUOUS_TARGET`

深钻后刚刚释放的 ref 序号永不复用,旧 ref 不可能静默解析到别的元素。`STALE_REF` 返回里带诊断:活体与保存身份字段的逐项对比。

## 现状

- 目前仅 Windows。`ISource` 接缝就是 macOS(AX API)和 Linux(AT-SPI)适配器的插槽。
- MCP 顺序 dispatch(agent 循环本身是串行的,并行在路线图上)。
- managed UIA wrapper 不支持单调用超时——provider 卡住会拖住命令,COM 层超时在路线图上。
- 39 个单元测试;walker 对着 fake source 跑,和 UIA 适配器走同一批分支。

## 路线图

- [ ] 缺少 pattern 时对 ref 的坐标做 headed 点击
- [ ] 两次快照之间的截图 diff
- [ ] MCP 并行 dispatch
- [ ] COM 层单调用超时
- [ ] macOS / Linux 适配器

## License

MIT
