# Antigravity 桌面接入

FurtherAgent 0.18.0 可通过 Antigravity App 的 Sidecar 接入桌面原会话。接入端按原会话 ID 列出、打开和发送消息，也支持新建及归档。控制端可使用已有的 Windows、Mini 或 Android 会话入口。[Antigravity App 发布记录](https://antigravity.google/docs/changelog?tab=hub) [Google Sidecars 文档](https://antigravity.google/docs/sidecars)

## 在运行 Antigravity 的 Windows 电脑上启用

1. 使用 Antigravity App 2.35.x 或支持 Conversation Management API 的更新版本，并以平时使用 Antigravity 的 Windows 用户登录。
2. 在 FurtherAgent 源码根目录运行以下命令。已有完整发行包时，将安装脚本的 `-HostExecutable` 指向发行包里的 `Host/FurtherAgent.Host.exe`。

   ```powershell
   dotnet publish src/FurtherAgent.Host -c Release -r win-x64 --self-contained true -o artifacts/release/Antigravity-Host-win-x64
   pwsh -File scripts/Install-AntigravitySidecar.ps1 -HostExecutable artifacts/release/Antigravity-Host-win-x64/FurtherAgent.Host.exe
   ```

3. 重启 Antigravity App，让它启动名为 FurtherAgent 的 Sidecar。到 `$env:USERPROFILE/.gemini/antigravity/sidecar_data/furtheragent/logs/` 查看最新的接入端日志，在 FurtherAgent 使用其中的授权码连接 `机器名 · Antigravity`。

安装脚本保留其他 Sidecar 设置。更新时重新运行安装脚本并指向新的 Host 发布目录，沿用同一个设备名和固定配对身份。需要自定义显示名称时，可传入 `-DeviceName`。

## 会话和消息

会话列表、运行状态和归档由桌面 App 的 Conversation Management API 提供，发送通过 Antigravity 自带的 `agentapi` 提交到当前原会话。用户令牌由 Antigravity 提供给接入端进程，请在 App 启动的 Sidecar 中运行 Host。[Google Sidecars 文档](https://antigravity.google/docs/sidecars#agentapi)

Antigravity 写出本地聊天记录后，打开的会话会自动同步新记录。历史视图最多读取日志末尾 4 MiB、500 条消息，FurtherAgent 的历史分页从中提供较早记录。旧会话暂缺可读记录时仍能打开并发送。同一个会话正运行时，请等到空闲后再发送。停止、权限审批和文件交互在 Antigravity 桌面操作。[Google Hooks 文档](https://antigravity.google/docs/hooks)

若 Windows 上 Antigravity 安装在自定义目录，可给 Sidecar 的 `env` 设置 `FURTHERAGENT_ANTIGRAVITY_EXE` 为该安装内的 `resources/bin/language_server.exe`。接入端直接调用此可执行文件传送原始文字。
