# Android 0.12.2 安装记录

2026-10-14，用户确认手机已连接，授权继续覆盖安装。

- 设备：vivo V2403A，ADB 在线。
- 旧版：0.12.0 / versionCode 1200；新版：0.12.2 / versionCode 1202。
- 包：`artifacts/release/android/0.12.2/FurtherAgent-Android-0.12.2.apk`。
- SHA256：`BC3BA9DD6C663269E45D8EF69941CDE02614CA06D49D5C0FC0F766156E2C1C02`。
- 从设备读取已安装 APK，与新包比较签名证书 SHA256，一致；新包通过 APK v2/v3 签名校验。
- 使用 `adb install -r` 覆盖安装，返回 Success。未卸载、未清除应用数据；首次安装时间仍为 2026-10-12 08:43:12。
- 安装后系统回读 versionName=0.12.2、versionCode=1202；主 Activity 冷启动 Status=ok，启动耗时 1005ms，进程 8690。
- 启动检查时该进程 crash buffer 无输出；实机截图显示重构后的首页、连接下拉和底部输入区，并自动恢复到已保存的 Hermes 连接（已连接、未选会话）。截图保存在忽略目录 `artifacts/inspection/android-0122-startup.png`。
- 本次包含此前尚未安装到该设备的手机 UI 重构、输入区不透明背景、0.12.1 连接/更新改进，以及 `ac8ff2c` 的草稿清理和 Codex 权限入口。
- 尚未在实际 Codex B 端验证新权限写入、插入消息及软键盘状态；B 端接入程序仍需更新。此次安装与启动验证不等于完整视觉和业务链路验收。
- 未更新服务器或 B 端接入程序，未推送 Git。
