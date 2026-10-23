# Android 0.12.9：会话完成系统通知

日期：2026-10-23。包含此前 0.12.7 后台连接修复和 0.12.8 固定直连修改；桌面版本保持 0.14.19。

## 原因与修改

接入端和桌面端已有 `session-completed` 事件，但手机 `MobileSession` 只接收 `session` 快照，忽略完成事件。Android Manifest 也没有 `POST_NOTIFICATIONS`，没有 Android 13 通知授权流程。

- `MobileSession` 新增完成事件出口，并按事件 ID 去重，最多保留 512 条去重记录。完成事件不修改当前查看的会话内容。
- 应用级 `ConnectionRuntime` 订阅完成事件，页面在后台或被销毁时仍可发布系统通知。
- 新建“会话完成提醒”高重要性通道，与“远端连接”常驻通知分开。提醒显示会话标题、设备名和“执行完成”。相同设备/会话后续提醒替换旧提醒，点击自动消除；锁屏内容遵循系统隐私设置。
- Android 13+ 在前台首次连接/恢复已保存连接后请求通知权限。拒绝不会中止连接，也不反复弹授权。连接管理页显示通知开启状态，并提供“会话完成通知设置”入口，允许用户重新开启或关闭提醒。
- 前台正在查看同一设备的同一会话时不通知；后台、其他会话、抽屉或设置页面会通知。声音、横幅和锁屏显示最终由系统通道、勿扰模式及厂商设置决定。
- 通知点击使用独立设备/会话标识，兼容已有 Activity 和冷启动。先保存当前草稿，返回聊天首页并只读打开目标会话；已有连接离线则等待恢复，尚未恢复的保存连接可由本次点击重新建立。手动连接、断开、选择其他会话或进入预览会取消尚未完成的通知跳转。

## 使用

安装 0.12.9 后连接设备，允许 Android 的通知权限。也可进入“连接管理 → 会话完成通知设置”核对系统通知和“会话完成提醒”通道。

接入端必须支持 `session-completed`，已有桌面完成提醒所使用的接入端无需为本次更改协议。仅在连接在线、接入端仍观察相关会话时接收事件；初次载入空闲历史、等待确认、明确失败/取消及手动停止沿用接入端已有抑制规则。没有新增云推送或断线事件补发，手机被强制停止或离线期间不承诺收到完成提醒。

## 必要验证与待验收

`--mobile-completion-smoke` 仅检查真实隔离本机中继上的移动会话事件链路：切走会话后的完成事件、重复空闲快照不重复提醒、当前会话完成事件出口、断开后不投递旧事件。4 项通过。

Android 编译、APK 与真机结果分开记录，详见 [待测试清单](Pending-Tests.md)。2026-10-23 连接真机后确认目标为 Vivo V2403A、Android 16（此前 Android 13 为用户口述，现以设备回读为准）。通知首次授权弹窗已出现并允许，系统回读 `POST_NOTIFICATIONS: granted=true`；横幅/声音、锁屏和点击跳转未实机验收，不把权限开启当作完成提醒效果已验证。

交付：Android Release 编译通过并生成签名 APK `FurtherAgent-Android-0.12.9.apk`，安装包旁附本报告和待测试清单。首次覆盖安装因旧 0.12.5 与新包签名不一致被系统拒绝。随后用户明确确认手机使用电脑端相同的 3 个连接，并授权备份后卸载重装：已校验电脑端 DPAPI 加密连接备份，卸载及安装均返回 Success，系统回读 0.12.9 / 1209，冷启动 Status=ok。

通过应用界面恢复 Codex-Company、Hermes-default、Codex-Home，重新启动后 3 条保存记录仍存在，手机端均开启自动恢复连接。Hermes-default 和 Codex-Home 已显示已连接，Codex-Home 已加载会话列表。Codex-Company 返回“授权码不存在或已过期”；用电脑端备份原始凭据直接请求配对也返回 HTTP 404，需对应设备提供有效授权码后再连接。保留该连接记录，未重置任何远端授权。备份位于忽略目录 `artifacts/private/android-reinstall-0129/connection.dpapi.json`，凭据保持 Windows 当前用户 DPAPI 加密，不提交 Git。旧手机本地数据随授权卸载清除，仅恢复上述连接资料；远端会话未修改。未发布在线更新源；没有修改或重启桌面端及接入端。

实现位置：`MobileSession.cs`、`ConnectionRuntime.cs`、`Platforms/Android/CompletionNotifications.cs`、`MainActivity.cs`、`MainPage.Notifications.cs`、`ConnectionPage.cs`。

官方依据：[Android 13 通知权限](https://developer.android.com/develop/ui/views/notifications/notification-permission)、[通知点击导航](https://developer.android.com/develop/ui/views/notifications/navigation)。
