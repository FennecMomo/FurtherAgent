# DSH 双模式接入（0.8.0）

此适配从 FurtherAgent 0.6.8 开始实现，不依赖尚未推送的 0.7.0。
桌面与独立后台使用同一插件、同一个 Core 适配器，WPF 和 CLI 通过既有中继协议访问。
0.8.0 交付首批会话控制能力；扩展能力仍按下述边界关闭，不能把基础联调通过当成全功能验收。

## 生命周期与安装

`scripts/Install-DshBridge.ps1 -Mode desktop` 在当前用户 DSH 桌面 profile 中添加插件。
安装前备份 patch；插件按内容摘要放在独立目录，更新不覆盖正在使用的模块。
首次加载或升级后需在 DSH 空闲时彻底退出桌面并重新启动。

`scripts/Install-DshBridge.ps1 -Mode background` 准备独立后台的 patch。
后台使用用户指定的 DSH 桌面安装包所带运行时，不使用 PATH 上可能较旧的 dsh。
其 DSH_HOME 是 `%LOCALAPPDATA%/FurtherAgent/dsh/background-home`，不复制桌面的账号、会话或配置。
后台需要单独配置模型账号；退出请求若发现任务或待处理队列会拒绝关闭。

接入端参数：

| 环境变量 | 用途 |
| --- | --- |
| FURTHERAGENT_BACKEND | hermes（默认）、dsh-desktop、dsh-background |
| FURTHERAGENT_DSH_EXE | 后台使用的 DSH 桌面程序路径 |
| FURTHERAGENT_DSH_DESCRIPTOR | 显式选择现有本地插件实例；不设置时自动发现桌面或启动独立后台 |
| FURTHERAGENT_SERVER / FURTHERAGENT_NAME | 中继与设备名称，沿用既有机制 |

`FurtherAgent.Host --probe` 检查能力和会话数量；后台模式会启动独立运行时、初始化独立目录，再正常退出。
本地连接描述包含随机凭据，只允许当前用户和 SYSTEM 访问，不能上传或加入版本库。
插件只监听 127.0.0.1，拒绝浏览器 Origin 和缺失/错误的 Bearer 凭据。

## 当前实现边界

- 列表、历史、新建、发送、执行中追加、停止和模型设置已接入原生控制器。
- Plan/Build 通过 DSH 自身 `/plan` 命令切换；归档/恢复归档使用 workspaceController，拒绝带活动任务的归档，不删除历史。
- 查看历史仅调用 `sessionQuery.observeSession`，不调用 `resume` 或会隐式恢复冷会话的 `follow`。
- 监听同一 Host 的 assistant-stream 事件，区分真实文本和 reasoning 增量；提交最终消息后移除临时消息。
- 历史排除模型上下文替换副本及插件注入的 user 消息，保留人类原始对话。
- Core 使用历史游标和流式修订号建立长轮询订阅，空闲不反复读取历史；旧插件回退为快照。断线只恢复读取，不自动重发结果未知的消息。
- 排队、执行中追加、打断后发送分别路由；WPF 可选择发送方式，CLI 使用 `--delivery auto|queue|steer`。
- 打断后发送在 B 端核对运行身份、停止前后队列，队列非空会拒绝提交，不清空任何消息；其他入口同时提交仍按 DSH 原生顺序处理。
- 图片、文件、审批、提问仍未开放；不得仅凭原生接口名称宣称可用。
- 桌面插件退出后不会另起实例接管桌面会话，不删除锁文件、不直接写会话存储。

## 2026-10-10 阶段证据

真实桌面运行时为 0.2.0-rc.2；全局 CLI 0.1.5-rc.2 未用于接入。
后台独立运行时探测读到 0 条会话；桌面读到 9 条既有会话。
两条既有会话各读到 386、387 条投影消息，读取没有取得 Agent 写入权。
专用测试会话在桌面 Host 内创建、接受一条提示词并回复 `FA_DSH_OK`，没有调用工具。
完成后同一 Host 的 Agent 仍可查询，历史为一条用户消息和一条回复。

`dotnet run --project tests/FurtherAgent.SmokeTests -c Release -- --dsh-live` 是显式真实联调入口。
它会创建一个专用会话并提交一条无工具测试提示词，启动独立回环中继，调用真实 CLI。
使用 `--dsh-live --controls` 可额外验证真实停止、队列与后台隔离；目前共 32 项通过，覆盖同一 Host 双读取者、CLI 中继读取/发送、模型目录、Plan/Build、
原生归档/恢复、观察连接更新、断开后读取不重发、流式和最终回复不重复。
实测还包括真实打断后仅提交一次、非空队列拒绝替换、后台与桌面的会话列表隔离、
后台退出不改变桌面 Host。停止很早时 DSH 可能尚未将原始输入提交到历史，验收应检查替换消息唯一性，不能假设被取消输入一定已落盘。
这不等同于鼠标操作或 WPF 视觉验收，也尚未覆盖两个入口同时执行写入的竞争。

尚待验收：WPF 桌面视觉同步、双入口近同时发送及审批竞争、图片/文件原生附件与远程审批/提问。首批测试已验证 CLI 经中继控制同一桌面 Host、停止与队列边界、重读不重发及双模式进程隔离。桌面页面停留与交互效果没有通过模拟点击宣称已验收。

实际生命周期联调通过：空闲桌面完整退出并重新启动后，原适配器按同一 profile 身份重新发现插件；独立后台 PID 和会话保持不变，重开的桌面仍看不到后台会话。默认桌面发现支持该恢复，显式指定描述文件则固定目标。常规回归 141 项、无窗口 WPF 与持久化检查 17 项通过。

## 客户端入口与后台配置

在「设置 → 共享本机 Agent」选择 Codex 桌面、DSH 桌面或 DSH 独立后台，然后开启共享。
选项和 DSH 安装路径随地址保存在当前用户目录，更新程序目录不丢失。
插件安装脚本使用 PowerShell 7：`pwsh -File scripts/Install-DshBridge.ps1 -Mode desktop`；后台改为 `background`。
本次已在本机安装两种插件配置并验证桌面加载，不需要你再重启一次。

独立后台只继承 Windows 必需环境变量，不继承模型 API key 或外部 DSH 环境覆盖。
它可能在未登录时就展示内置模型目录，因此设置页明确提示「能列出模型不代表已配置凭据」。
目前未复制桌面账号，也未验证后台真实模型生成；后台的进程、列表、新建和隔离已验证。
需要配置后台时，先停止 FurtherAgent 的后台共享，再使用独立 home 启动 DSH 自身网页入口：

```powershell
$env:DSH_HOME = Join-Path $env:LOCALAPPDATA 'FurtherAgent/dsh/background-home'
$env:ELECTRON_RUN_AS_NODE = '1'
$exe = 'D:/Software/DeepseekHarness/DeepSeek Harness.exe'
$cli = Join-Path (Split-Path $exe) 'resources/app.asar/dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/cli.js'
& $exe --expose-internals $cli --profile web --port 0
```

该命令请在专用 PowerShell 窗口运行，按 DSH 提供的本地入口配置该实例，完成后退出该进程再开启 FurtherAgent 后台共享。
不要把后台 home 改成桌面的 `.dsh`，不要让两个后台运行时同时使用同一 home。
模型切换是 DSH 原生行为，会同步修改该实例的默认模型。Plan 切换已验证，Plan 完成时若触发原生审批或提问，仍须由所属 DSH 入口处理；FurtherAgent 0.8.0 不提供远程回应。
图片、文件按钮按 DSH 能力禁用，Hermes/Codex 原有能力不受此限制。

发布包同时包含 Client、Server、Host、Cli、插件和安装脚本。当前中继透明转发，不需要为本机 DSH 接入升级或重启线上 Hermes 服务。
