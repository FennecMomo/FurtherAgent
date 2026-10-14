# 0.14.4 桌面更新

2026-10-14，打包并替换本机 Windows 桌面客户端。

- 包含 46cc8e0：Codex 风格侧栏滚动条贴近右边缘，正文隐藏滚动条且保留滚动、历史加载；原始风格不变。
- 发布目录：`artifacts/release/0.14.4/Client`；安装包：`artifacts/release/FurtherAgent-Desktop-0.14.4.zip`。
- SHA256：`50D33EAC1F6A3C7EA07B1F4AFB36B05D9998D6E9EB2E6205BCEF213FB83B3C3B`。
- Release publish 成功；安装脚本逐文件校验哈希，正常退出旧客户端并启动新版，更新桌面入口。
- 控制接口确认运行版本 0.14.4，ready=true；自动连接及窗口可见性通过部署后检查。
- 此次只替换桌面客户端，不部署 Android 或服务器。此前旧包清理的策略阻止仍未解决，本次没有重新尝试删除。
