# DSH 双模式接入（插件 0.9.0 / FurtherAgent 0.14.16）

此接入在 DeepSeek Harness 自身 Host 内运行插件，复用其原生会话服务。
桌面与独立后台共用桥接和 Core 适配器，可由 Windows、Android 和 CLI 控制端访问。
已核对本机 DSH 0.2.1-rc.2（构建 332895e0f7c9d4dbfb29729f5eb367fa263ff986）的实际接口。

## 已接入功能

- 会话列表按最近活动排序，显示工作区；过滤 subagent，保留普通 fork。
- 只读历史、实时文本与思考流、按工作区新建、归档和恢复。
- 排队、执行中追加、停止、核对运行身份后打断并发送；不清空其他入口的原生队列。
- 原生模型目录、思考强度、Plan/Build 和原生权限预设。模型切换会更新该 DSH 实例默认模型；Plan 变更可能在下一执行步骤生效。
- 原生工具审批（仅允许本次/拒绝）、单选/多选/自由回答，以及原生 continued 问题的回应。
- 通用文件上传、下载、输出文件列表和缩略图。上传文件通过本机路径供 Agent 读取；图像理解能力取决于 DSH 当前模型与工具。
- 输出目录注册为当前会话的原生动态系统上下文，不追加到每条用户消息，不污染人类聊天记录。
- 共用现有持久配对、多控制端、中继和局域网优先链路、增量会话同步、手机分页/本地缓存及完成通知。

桥接的历史预算为最新序列化 UTF-8 内容约 20 MiB；超大单条保留末尾并显示截断说明，正确保留 Unicode 字符。手机仍通过既有协议先取最近约八条，再向上分页。桥接预算之前的历史不在本次远端快照中，DSH 原始历史保留。
完成通知仅使用原生 completed 终态；失败、阻断、停止和待处理队列不误报成功。
模型额度没有可靠原生接口时保持未知，不显示虚构数值。

## 安装及加载

在仓库或发布包根目录用 PowerShell 7 执行：

```powershell
pwsh -File scripts/Install-DshBridge.ps1 -Mode desktop
```

脚本备份当前用户 `.dsh/profiles/desktop/cordis.patch.yml`，仅添加或更新 FurtherAgent 插件块。
如使用其他 DSH home，显式传 `-DshHome <目录>`。
模块以版本和内容摘要存放于 `%LOCALAPPDATA%/FurtherAgent/dsh/plugin-0.9.0-*`，入口为 Windows 支持的 `file:///` URL；旧模块不会被覆盖。
首次安装或升级需在 DSH 空闲时完整退出并重开，使原 Host 加载新版插件。脚本本身不重启 DSH。

桌面客户端在「设置 → 共享本机 Agent」选择「DSH 桌面」，然后开启共享。
程序选择器保留已保存路径，并从运行进程、App Paths 和常见安装位置发现可用运行时；多个候选时需要明确选择。
独立后台选择「DSH 独立后台」，并指定正式的 DeepSeek Harness.exe；overlay 壳不能当作完整 DSH 安装。

CLI 接入端：

```powershell
$env:FURTHERAGENT_BACKEND = 'dsh-desktop'
$env:FURTHERAGENT_SERVER = 'https://你的中继地址'
$env:FURTHERAGENT_NAME = '我的 DSH'
.\Host\FurtherAgent.Host.exe --probe
.\Host\FurtherAgent.Host.exe
```

| 环境变量 | 用途 |
| --- | --- |
| FURTHERAGENT_BACKEND | hermes（默认）、dsh-desktop、dsh-background |
| FURTHERAGENT_DSH_EXE | 独立后台所用正式 DSH 程序的绝对路径 |
| FURTHERAGENT_DSH_DESCRIPTOR | 可选；显式固定现有插件实例。多个实例时必须明确目标 |
| FURTHERAGENT_SERVER / FURTHERAGENT_NAME | 中继地址与设备名称 |
| FURTHERAGENT_IDENTITY_FILE | 可选；设备持久身份文件位置 |

`--probe` 读取能力和会话数量，不连接中继、不提交 Agent 提示词；后台模式会启动并退出自己的独立运行时。
默认桌面发现可在同一 profile 重启后恢复；显式 descriptor 固定目标，不跳到其他 profile，不另起实例接管桌面会话。

## 审批和断线

只对有真实远端查看者的、属于当前 Host 的 exact live root Agent 接管审批/提问。
没有查看者时保持 DSH 本地原入口；subagent 不被插件代答。
观察租约按控制器分别维护，旧长轮询不能复活已经释放的租约。
断线、停止共享或租约到期时，尚未回答的请求交回原生入口；原请求已取消则不再打开失效窗口。
回答前核对当前会话与唯一请求身份，在原生调用前预留，避免多端重复回应。

短暂连接中断只恢复同一会话的读取，不重放写操作。
发送 ACK 未知时保留原 requestId、原发送方式和 turnId；历史确认已接收后，同文重试消费原回执而不重复发送。

## 独立后台

```powershell
pwsh -File scripts/Install-DshBridge.ps1 -Mode background
$env:FURTHERAGENT_BACKEND = 'dsh-background'
$env:FURTHERAGENT_DSH_EXE = 'E:\dsh\DeepSeek Harness.exe' # 换成实际安装位置
```

独立实例的 DSH_HOME 为 `%LOCALAPPDATA%/FurtherAgent/dsh/background-home`。
只继承 Windows 所需环境变量，不复制桌面账号、历史或配置；需要在独立 home 的 DSH 原生入口单独配置账号。
列出内置模型不代表已有凭据。后台有运行/排队任务时拒绝退出；桌面生命周期仍由 DSH 管理。
不要让两个运行时使用同一个 home，也不要把后台 home 改成桌面 `.dsh`。

## 验证边界（2026-10-18）

- Node 插件离线测试 42 项通过：原生服务 stub、真实本机文件系统、回环 HTTP 鉴权和取消、20 MiB 历史及 Unicode、审批/提问/租约/并发。
- C# 离线 HTTP 合约 22 项通过：工作区、模型、权限、文件、提问身份、原请求重试、断线恢复和旧插件能力门控。
- 常规 SmokeTests 452 项通过，覆盖共享中继、手机分页/缓存/重连、多端控制、文件传输、完成通知及既有 Codex/Hermes 回归。
- WPF 常规检查 27 项通过；新增设置和提问控件回归验证原生权限目录、多选答案、完整滚动详情与审批按钮。桌面解决方案构建、Android Release 编译及 APK 打包均成功，0 个警告/错误；APK v2/v3 签名验证通过。
- 使用本机正式安装中的真实 Cordis、SystemPrompt，在独立 Electron Node 进程内验证 waterfall 回调与动态上下文注册；只创建内存 mock，未运行真实账户任务。
- 使用正式 DSH 0.2.1-rc.2 启动 FurtherAgent 独立后台，真实加载插件 0.9.0；Host --probe 返回全部原生能力、0 条独立会话，随后正常退出，未向模型提交请求。
- 新审批、提问、文件及设置尚未完成真实手机到 DSH 模型任务的设备验收；编译及模拟服务通过不能替代此项。

复现：

```powershell
node --test tests/dsh/*.test.mjs
dotnet run --project tests/FurtherAgent.SmokeTests -c Release -- --dsh-adapter-checks
dotnet run --project tests/FurtherAgent.SmokeTests -c Release
```

显式真实联调入口 `--dsh-live [--controls]` 会创建专用会话并发起模型请求，应在插件加载、目标明确后运行。
2026-10-10 的旧版 0.8.0 已验证桌面同 Host 读写和独立后台隔离；那批证据不作为本轮新增功能的真实设备验收。
本地连接描述包含随机凭据，仅当前用户和 SYSTEM 可读，不能上传或加入 Git。插件仅监听 127.0.0.1，并拒绝浏览器 Origin 和无效 Bearer 凭据。
