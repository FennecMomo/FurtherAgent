# 0.14.3 桌面更新

2026-10-14，发布并替换本机 Windows 桌面客户端。

- 包含 456ad80：正文、侧栏及标题字号校准，连续列表行距收紧，普通段落和消息间距调整。
- Release publish 成功，通过管理脚本正常退出旧进程、更新桌面入口并启动新版。
- 新版控制接口确认 version=0.14.3、ready=true、visible=true、connected=true，自动恢复 MOMO · Codex 连接。
- 安装快照逐文件哈希校验由更新脚本完成。
- 发布包：`artifacts/release/FurtherAgent-Desktop-0.14.3.zip`。
- SHA256：`FA6FA1474DDB1AB8FA0A4CD2C67D9FC8C7E87E21ED18FB665E16500BE92893CC`。
- 已清点 61 个旧桌面发布目录/压缩包及 33 个旧安装目录，共约 112.65 MiB，清单在 `artifacts/cleanup-desktop-0.14.3.json`。
- 批量删除命令在执行前被工具策略拒绝（blocked by policy），因此未删除任何旧包。没有改用其他方法绕过限制。原安装及发布文件仍保留。
- 没有部署手机或服务器，没有推送。
