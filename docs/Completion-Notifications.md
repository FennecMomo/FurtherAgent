# 桌面会话完成提醒

连接保持在线时，接入端观察到已打开过的会话从执行中进入空闲或完成状态，桌面端按以下规则提醒：

| 桌面当前状态 | 是否弹出 |
| --- | --- |
| FurtherAgent 在前台，正在查看完成的会话 | 否 |
| FurtherAgent 在后台、最小化或收起到托盘 | 是 |
| 在 FurtherAgent 中查看另一会话或另一设备 | 是 |
| 正在设置页或打开了覆盖会话的弹层 | 是 |

通知复用 `E:\etc\ResourceManager\ResourceManager.App\ChatToastWindow.cs` 的外观：340×112、白底、12 圆角、浅色边框，标题/说明/链接字号为 15/12/11。显示会话标题、设备名称和“执行完成”，右下角堆叠，5 秒消失；弹出不激活主窗口。点击后打开对应设备与会话，保留原会话草稿。屏幕放不下时关闭最早的通知；退出时清理全部通知。

同一个完成事件去重。初次加载空闲历史、等待确认、未知状态和适配器明确报告的失败/取消/停止不作为完成。客户端发出的手动停止也会抑制该轮完成通知。

## 接入端及范围

本功能新增 `session-completed` 通知，包含事件 ID、会话 ID、标题和轮次 ID。它独立于当前所选会话快照，因此切到别的会话不会丢掉原会话的完成事件，也不会把后台消息混入当前会话。

后续发布需要同步更新 FurtherAgent 桌面客户端和对应接入端；旧接入端不会产生这个事件。无需修改中继服务或手机 UI。只跟踪本次接入端运行期间被客户端打开过且适配器仍在观察的会话，不扫描所有原生会话。断线期间的事件不持久化，也不保证重连补发。

## 验证

- `CompletionChecks`：状态转换、重复快照、停止、无轮次 ID，以及真实本地中继中切走会话后的通知投递。
- `CompletionUiChecks`：隔离 WPF 窗口的前后台条件、跨设备同名 ID、设置/弹层、去重、外观及超时、点击后的真实 attach RPC、草稿保留与退出清理。
- WPF 测试使用注入的显示/激活入口和内存 RPC，仅离屏渲染卡片，不弹出窗口、不抢当前桌面焦点。

执行：

```powershell
dotnet build FurtherAgent.slnx -c Release
dotnet run --project tests/FurtherAgent.SmokeTests -c Release --no-build
dotnet run --project tests/FurtherAgent.WpfChecks -c Release --no-build
dotnet run --project tests/FurtherAgent.WpfChecks -c Release --no-build -- --completion artifacts/inspection/completion-notifications/toast.png
```

此次仅修改源码、构建测试并本地提交，不打包、不安装、不重启在用客户端或接入端。
