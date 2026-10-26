# 留言模块（Windows 0.15.0 / Android 0.13.0）

留言与实时会话是两个独立入口。用户可以连续留几条消息，由小夜在自己方便时主动查收和回复。发送留言只写入服务器，不调用 DSH/Hermes 的 send、steer、attach，不创建模型轮次，也不改变已有唤醒计划。

## 从源码打包

本轮按用户要求交付源码，不通过服务器分发安装包或自动部署。家里电脑拉取 `main` 后，在仓库根目录使用 .NET 10 SDK 和 PowerShell 7：

```powershell
git pull --ff-only
dotnet build FurtherAgent.slnx -c Release
./scripts/Publish.ps1 -SkipDesktopUpdate
```

输出为 `artifacts/release/0.15.0/`，包含 Client、Host、Server、Cli 和说明文档；`-SkipDesktopUpdate` 让打包不直接替换运行中的桌面端。安卓需已安装 MAUI Android 工作负载、Android SDK 和 JDK 21，再运行：

```powershell
./scripts/Publish-Android.ps1 -AndroidSdkDirectory "你的Android SDK目录" -JavaSdkDirectory "你的JDK21目录"
```

安卓输出为 `artifacts/release/android/0.13.0/`。只有用户实际部署新版中继并完成首次绑定后，公网留言功能才可用；本轮编译及回环检查不代表线上已经启用。

## 使用方式

1. 在 FurtherAgent 保存家里的 DSH 设备连接。
2. 桌面侧栏/托盘或安卓抽屉/连接管理打开“留言”，选择这个连接。
3. 第一次启用时让家里的接入端在线，沿用配对关系取得持久留言绑定。之后家里电脑、DSH 和 FurtherAgent Host 都可离线，服务器仍接收留言。
4. “待发送”表示只在当前设备保存；“已存入留言箱/已保存到留言箱”表示服务器确认落盘；“对方已读”仅来自 Agent 端明确确认。服务器接收不代表小夜已经阅读。
5. 本机网络失败时保留草稿或待发送队列；重试沿用原消息 ID，避免产生两条相同留言。自己的发送确认不会推进收件游标而跳过中间的回复。

桌面沿用现有聊天气泡、Markdown 与通知窗口，支持 Ctrl+Enter 发送、Enter 换行、系统听写输入。安卓沿用现有聊天展示、录音、Fun-ASR 转写和语音设置；语音先变成草稿，经用户确认后发送。此处没有另加桌面录音器或原声语音条。

桌面约每 12 秒、安卓前台/已有前台服务约每 15–20 秒补收。收到新回复显示独立的留言通知，点击进入留言页。只在用户实际查看留言页时确认已读；后台轮询不代替用户阅读。安卓应用被系统强杀后没有 FCM 类系统推送保证，进程/服务恢复后会补收。

## 小夜的独立工具

新接入端启动后，会在当前用户的 `FurtherAgent/mailbox-accounts` 目录生成不含密钥的账户描述，内容仅包括服务地址、设备名称和现有身份文件路径。`FURTHERAGENT_MAILBOX_ACCOUNT_DIR` 可指定该目录。账户描述不绑定 DSH 会话 ID，压缩、归档或切换会话不会换收件箱。

```powershell
./Cli/FurtherAgent.Cli.exe mailbox accounts
./Cli/FurtherAgent.Cli.exe mailbox status --account-file "上一步列出的账户描述.json"
./Cli/FurtherAgent.Cli.exe mailbox read --account-file "账户描述.json" --limit 20
./Cli/FurtherAgent.Cli.exe mailbox mark-read --account-file "账户描述.json" --through 读取结果中已看完的序号
./Cli/FurtherAgent.Cli.exe mailbox reply --account-file "账户描述.json" --text-file "回信.txt" --id "本条消息固定的GUID"
```

旧接入端已有持久身份但未生成账户描述时，可以使用新 CLI 的 `mailbox setup --server 中继地址 --name 设备名称 --identity-file 已有身份文件` 建立描述。此命令不会创建/轮换身份，不重启 DSH。Linux 使用 `dotnet Cli/FurtherAgent.Cli.dll mailbox ...`。

`status` 仅看未读数；`read` 默认从 Agent 已读位置后按时间顺序取一页，避免积压较多时跳过旧留言。`HasMore=true` 时用最后实际取得的序号继续 `read --after 序号`。主动确认后才调用 `mark-read`，或在读取时显式加 `--mark-read`。历史也可用 `--before 序号`。`reply` 的 GUID 在结果不确定时应复用，不能换 ID 盲目重发。

工具不会自行启动模型。小夜可以在自己的既有活动中选择调用它；本模块不配置新的定时唤醒、不自动把留言转入普通会话。

## 持久存储与部署

先升级中继，再更新客户端及家里的 CLI/接入端。`/health` 应显示 `mailboxVersion=1`、`persistentMailbox=true`，`softwareVersion` 为实际程序版本。旧实时中继协议保持兼容，但旧服务器会让留言入口明确提示待升级。

设置 `FURTHERAGENT_MAILBOX_ROOT` 到独立于发布目录的持久数据目录；默认位于运行用户 LocalAppData/FurtherAgent/mailboxes。每条消息独立写入并 flush 后原子交付，元数据同样原子替换；重启重新读取消息序号、绑定和双向已读位置。目录有单进程所有权锁，不能用两个服务实例同时写同一目录。升级时保留整个目录，不把它包含进发布包。

首次绑定由现有在线配对关系授权，不另设登录。用户端凭据在 Windows 用当前用户 DPAPI、Android 用 SecureStorage；Agent 端读取接入端既有身份文件。服务器只持久保存身份/令牌哈希，不在提示词或命令参数中传密钥。

HTTP 接口：`POST /mailboxes/pair`，`GET /mailboxes/{code}/status`，`GET/POST /mailboxes/{code}/messages`，`POST /mailboxes/{code}/read`。只有留言接口的请求体限制为 64 KiB；正文最多 8000 字符，历史单页最多 50 条、消息序列化目标 128 KiB。双向已读游标独立，GET 不修改已读。所有角色由服务端认证决定。

## 必要验证

`dotnet run --project tests/FurtherAgent.SmokeTests -c Release -- --mailbox-smoke` 使用回环服务验证真实配对、幂等发送、离线双向收发、服务器重启恢复、分页、已读和 Agent CLI；专用适配器计数用于确认没有实例化/附加/发送模型任务。真实通知、语音、设备和线上部署结果分别记录在 [待测试清单](Pending-Tests.md)，不能用编译结果代替设备验收。
