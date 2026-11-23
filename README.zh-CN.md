# adesk-pro

已上架 [Agensi](https://www.agensi.io/skills/adesk-pro) 技能市场。
**给 AI agent 用的桌面操作工具** — 通过操作系统无障碍树读写原生应用。不截图、不做像素匹配、循环里没有视觉模型。

支持 **Windows**（UI Automation）与 **macOS**（Accessibility / AX API）。

[English](README.md) | 简体中文

## 设计

agent 的循环是:把应用窗口读成带 ref(`@snapshot:eN`)的 JSON 树,对 ref 执行点击 / 输入 / 滚动,再读一遍验证。adesk-pro 就是这个工具,内部没有模型。四个设计决定:

- **全链路原生无障碍**。Windows 用 managed UIA;macOS 用 AX API 走手写 P/Invoke(不需要安装 Xamarin/Maui workload)。树遍历、ref 解析、模式调用、键鼠合成都在同一个约 3k 行的核心 + 薄适配层里。
- **MCP 服务器进程内 dispatch**。每个工具调用走和 CLI 同一张命令注册表——没有每次调用的子进程创建,没有全局文件锁;MCP 模式下 ref 存储就是服务器进程内存。
- **一张命令表**。CLI 解析、`--help`、MCP 工具 schema 全部从同一张注册表派生,不存在第二个定义命令的地方。
- **带预算的诚实遍历**。每次树遍历都有预算(深度 30 / 兄弟 2000 / 节点 5000 / 5 秒 deadline),触顶标记 `complete: false` 而不是悄悄截断。

## 安装

### npm（推荐，无需下载也无需构建）

```bash
npx -y adesk-pro doctor      # 检查权限与可见窗口
npx -y adesk-pro list-apps
```

npm 会按你的系统与架构自动安装对应的自包含二进制（作为 optional dependency），不需要装运行时,也不需要构建。接进 agent:

```json
{ "mcpServers": { "adesk-pro": { "command": "npx", "args": ["-y", "adesk-pro", "mcp"] } } }
```

### 从源码构建（不想用 npm,或平台未打包）

```bash
git clone https://github.com/ProcessMonitor/adesk-pro.git
cd adesk-pro
./build.sh                      # macOS/Linux;没有 SDK 时自动下载到 ./.dotnet
.\build.ps1                     # Windows(PowerShell)
```

产物是自包含单文件,零运行时依赖:macOS 约 35MB,Windows 约 68MB(WPF 原生库无法裁剪)。两个脚本都不会污染系统环境,`rm -rf .dotnet` 即可撤销。

### 权限

- **Windows**:终端与目标应用必须同一完整性级别(非提权进程读不到提权窗口)。
- **macOS**:给终端授予**辅助功能**权限(系统设置 › 隐私与安全性 › 辅助功能);只有要用 `screenshot` 才需要**屏幕录制**。`adesk-pro doctor` 会告诉你缺哪个。

只有真正读写无障碍树的命令才会申请权限。`--help`、`--version`、`status`、`doctor`、`list-apps` 永不弹窗。

## 接入 MCP

让工具自己生成配置,省掉手填路径:

```bash
npx -y adesk-pro install-mcp --client claude   # 打印 claude mcp add 命令
npx -y adesk-pro install-mcp                   # 打印通用 JSON 配置块
```

经 npm 运行时输出 `npx` 形式(升级不会失效);直接运行二进制则输出绝对路径。

工具名和 CLI 命令一一对应;结果返回完整 `structuredContent` 加一行摘要,截图作为独立 image block 返回。

## 实机演示

```
$ adesk-pro snapshot --app Notepad -i
{"ok":true,"data":{"snapshotId":"sn972n15auotk","refCount":18,"complete":true,
  "tree":{"role":"窗口","name":"无标题 - 记事本","ref":"@sn972n15auotk:e1","children":[
    {"role":"文档","name":"文本编辑器","ref":"@sn972n15auotk:e2","patterns":["scroll"]}, ...]}}}

$ adesk-pro type @sn972n15auotk:e2 "adesk roundtrip 你好"
{"ok":true,"data":{"resolvedBy":"scope-path","how":"keyboard","delivered":true,"value":"adesk roundtrip 你好"}}
```

macOS 实机(对 TextEdit 验证):

```
$ adesk-pro snapshot --app TextEdit -i        → 26 refs, complete: true
$ adesk-pro set-value @s1trh18tk4e7qn:e2 "round-2 验证 中文内容"
$ adesk-pro get @s1trh18tk4e7qn:e2              → scope-path 命中,内容正确读回
```

## 命令行

33 个命令。flag 是标准形式;示例里的简写同样可用(`click @sid:eN` 等价于 `click --ref @sid:eN`)。

- **观察**:`status` `doctor` `list-apps` `list-windows` `snapshot` `find` `get` `screenshot`
- **操作**:`click` `toggle` `expand` `select` `type` `set-value` `press` `scroll` `mouse-click` `hover` `drag`
- **应用与窗口**:`launch` `close-app` `minimize` `maximize` `restore` `move-window` `resize-window` `focus`
- **同步与脚本**:`wait` `batch` `clipboard-get` `clipboard-set`
- **安装与引导**:`install-mcp` `skills`

几个亮点:

- 元素没有可点击 pattern 时,`click` 自动回退到元素中心坐标的物理点击。
- `snapshot --root @snap:eN` 对旧快照的一个子树重新深钻;子树内的 ref 会以拼接后的 scope path 重新发放,子树外全部保持有效。
- `wait` 轮询到窗口出现或 ref 变为 `enabled` / `visible` / `gone`——脚本里不再需要固定 sleep。**`wait` 超时是错误(`TIMEOUT`),不再是 `ok:true`。**
- 每条命令输出一个 JSON envelope,错误带 `disposition`:只有 `retry: safe`(未送达)允许重试;输入已合成后才出错的动作标记为 `retry: unsafe`。
- `doctor` 报告平台、权限、可见窗口——snapshot 回来是空的时候先跑它。

## ref 语义

ref 形如 `@<snapshotId>:e<N>`,动作前按三步活体重定位:

1. 从窗口沿 scope-path 下降
2. Windows 上 **RuntimeId** 钉扎(重复控件因此可寻址)
3. 身份匹配(pid + 稳定 role id + automationId + name + className);零匹配 → `STALE_REF`,多个 → `AMBIGUOUS_TARGET`

身份用的是**稳定的 role id**(`ControlType.Button`、`AXButton`),不是本地化标签,所以系统语言变化后快照依然有效。名字等于内容的控件(文本框)不要求名字匹配,因此"编辑文本"不会让编辑它的那个 ref 失效。

深钻后刚刚释放的 ref 序号永不复用,旧 ref 不可能静默解析到别的元素。`STALE_REF` 返回里带诊断。

## 现状

- 支持 Windows 与 macOS。Linux(AT-SPI)可接入同一套 `ISource`/`IPlatform` 接缝。
- MCP 顺序 dispatch(agent 循环本身是串行的,并行在路线图上)。
- Windows:managed UIA wrapper 不支持单调用超时。macOS:每个元素设置 `AXUIElementSetMessagingTimeout` 2 秒,应用卡死时是调用失败而不是拖住 agent。
- 66 个单元测试;walker 对着 fake source 跑。macOS 适配器另经真实应用(TextEdit、Finder)手工验证。

## 路线图

- [ ] Linux 适配器
- [ ] 两次快照之间的截图 diff
- [ ] MCP 并行 dispatch
- [ ] COM 层单调用超时

## License

MIT
