# 本机客户端更新与状态查询（0.6.4）

发布入口 `scripts/Publish.ps1` 在验证新快照并更新桌面入口后，如发现本仓库管理的客户端正在运行，会通过本机命名管道请求退出、等待旧进程退出，再以 `--background` 启动新快照。无需用户关闭托盘或点击快捷方式。

参考 ResourceManager 的本机控制模式实现，未改动 ResourceManager 工程。控制管道按进程 ID 区分，限制为当前 Windows 用户，不使用桌面自动化，也不激活窗口。只管理当前仓库 `artifacts/current/builds` 内、同一 Windows 会话的客户端。

```powershell
./scripts/Client-Control.ps1 -Action Status
./scripts/Client-Control.ps1 -Action Shutdown
./scripts/Client-Control.ps1 -Action Restart -ExecutablePath '<已发布客户端完整路径>'
```

状态包括实际可执行路径、版本、PID、用户资料目录、连接中/已连接、远端设备、脱敏错误和窗口可见性。发布脚本确认新版已就绪，不把尚在进行的连接当作已连通；需要连接验收时继续读取 Status 的 connected 字段。

启动恢复连接不依赖窗口 Loaded 事件，因此后台托盘启动也会恢复已保存连接；连接失败最多尝试四轮，间隔 5/10/15 秒，每轮仍使用已有 TLS 有限重试。只重试配对建连，不重发聊天或审批。手动连接、断开、忘记连接或退出更新取消等待中的启动重试。

未发送正文、其他会话草稿、待发附件、传输或正在提交操作会使更新退出请求返回拒绝，旧进程保留。不会为自动更新强制丢弃内容。已发布快照和桌面入口可能已经更新，此时发布会明确报出运行实例尚未切换。

0.6.3 及更早版本没有退出通道，仅首次迁移可显式传入 `Publish.ps1 -AllowLegacyForce`，核验旧进程路径、版本和启动时间后结束该客户端。此选项不会强制结束支持控制通道的新版。旧版内存草稿无法通过该通道恢复。

## 本次实际验证（2026-10-09）

- 旧 0.6.3 PID 40292 经首次迁移退出，0.6.4 PID 60168 后台启动并自动连上 Hermes-default。
- 随后再次正常发布，60168 通过本机通道退出，新快照 PID 61752 就绪。
- 第二次切换后查询：version=0.6.4、connected=true、device=Hermes-default、connecting=false、error=null、visible=false；只剩一个管理范围内的客户端。
- 用户资料仍为 `C:\Users\renqi\AppData\Local\FurtherAgent`，未改服务器、授权码或用户配置。
- 编译通过；现有 16 项无窗口 WPF/加密持久化检查通过。此前旧版自动连接失败没有可查询的错误记录，不能事后确定该次失败原因；本次以新版实际连接结果验收。
