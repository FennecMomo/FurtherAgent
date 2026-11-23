# 新版 Codex Desktop 的共享 App Server

原生队列可以把输入交给普通方式启动的 Codex Desktop。需要实时回复、普通停止或原生审批时，Windows 上的 Codex Desktop 与 FurtherAgent 接入端需要连接同一个持有会话的 Codex App Server。

## B 上首次启用

等 Codex 桌面当前任务结束，从托盘退出 Codex Desktop 和 FurtherAgent。用同一个 Windows 用户运行脚本，将路径换成已更新的 FurtherAgent 可执行文件。

```powershell
./scripts/Start-CodexSharedDesktop.ps1 -FurtherAgentExecutable 'D:/FurtherAgent/Client/FurtherAgent.Client.exe'
```

脚本使用当前 Microsoft Store 的 Codex Desktop。其他安装目录可以使用 `-CodexDesktopExecutable` 指向安装中的 `ChatGPT.exe`。脚本启动一个本机回环 App Server，让新启动的 Codex Desktop 与 FurtherAgent 继承同一个地址。保持原有 Codex 用户数据和 FurtherAgent 配对配置，在 FurtherAgent 窗口里按原方式开启共享。

已有后台启动方式的 B 也可以传入 `FurtherAgent.Host.exe`。脚本会以 Codex 模式启动后台接入端，并打印进程 ID 及包含配对信息的本机日志路径。使用自定义设备名或身份文件时，保持原有 `FURTHERAGENT_NAME` 和 `FURTHERAGENT_IDENTITY_FILE` 设置。

脚本默认监听 `127.0.0.1:47338`。端口被占用时，先确认是否已有共享 App Server。该入口供 B 本机的两个应用使用，请勿在路由器、防火墙或中继上开放此端口。

## 重启和退回普通方式

使用共享模式期间，从共享模式的启动脚本一起启动两端。脚本结束后共享 App Server 继续运行。需要重启或退回普通模式时，等当前任务全部结束，然后退出 FurtherAgent 和 Codex Desktop，再关掉脚本输出中对应的共享 App Server 进程。下次通过普通入口启动会继续使用原有接入方式。

已有经过认证的本机 WebSocket App Server 可以让 FurtherAgent 进程继承 `FURTHERAGENT_CODEX_APP_SERVER_WS_URL`。该服务需要令牌时，通过 `FURTHERAGENT_CODEX_APP_SERVER_TOKEN_FILE` 提供本机令牌文件。Codex Desktop 的普通本机 WebSocket 连接使用 `CODEX_APP_SERVER_WS_URL`，它与 FurtherAgent 应指向同一个服务。

共享方式使用 Codex App Server 的 WebSocket 协议。已用本机同版本运行时和隔离连接验证原会话复用、结束事件、历史分页、审批广播，以及中途加入后补收已有审批。Microsoft Store 图形界面的整套启动、环境继承及真实任务仍需在 B 上验收。[Codex App Server 协议](https://learn.chatgpt.com/docs/app-server#protocol)
