# 0.14.5 桌面替换与接入端打包

2026-10-14，用户授权打包替换。

- 包含源码提交 `ac8ff2c`：Codex 执行中插入消息投影、发送确认与草稿清理、桌面和手机会话权限入口。
- 本次发布 Windows Client、Host、CLI、Server 至 `artifacts/release/0.14.5`。仅安装本机 Client，未部署 Server、远端 Host 或 Android。
- 完整包：`artifacts/release/FurtherAgent-0.14.5.zip`。
- ZIP SHA256：`5511420F8E690FFBEAE893DFF9F2CC3F884B07D0FBA02D6B6D411A869E1BB52A`。
- 本机安装：`artifacts/current/builds/0.14.5-20261014-105315-9574119f`，9 个文件逐一与发布目录哈希一致。
- 使用本地控制通道正常退出 0.14.4 并启动 0.14.5；保留配置、已保存连接与旧快照。桌面快捷方式指向最新版本启动器。
- 实际运行 PID 31716，控制接口报告 version=0.14.5、ready=true、visible=true、connected=true、error=null；恢复连接至 MOMO · Codex，中继和 Host 均已有成功心跳。
- 本次 `adb devices -l` 未发现手机，未打包或安装 APK。本机没有独立运行的远端 Codex Host，当前连接的 B 端位于另一台设备；仅准备对应 Host 文件，不能把打包视为远端部署成功。
- 若 B 端通过桌面客户端开启共享，需在 B 端替换 Client 后重新开启共享；若运行独立 Host，则需保留原启动参数、身份与配置并更新 Host 目录。B 端更新前，新权限能力和插入消息投影尚不能视为上线。
- 本次未推送 Git，未修改 Hermes 服务器或真实 Codex 会话权限。

功能验证与真实环境验收边界见 `Codex-Interaction-And-Permissions-QA.md`。
