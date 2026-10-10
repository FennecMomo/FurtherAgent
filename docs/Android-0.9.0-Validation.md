# FurtherAgent Android 首版 0.9.0

2026-10-10。面向 Android 的可安装预览版，使用 .NET MAUI 和现有 C# 中继协议。两个旧项目仅用于功能参考。本文记录 0.9.0 首版，已获用户实机确认可用；后续 0.9.1 的多端连接和 8 位固定码见 [多端连接说明](Multi-Controller-0.9.1.md)。下文的单端限制属于 0.9.0 当时状态。

## 安装体验

APK 位于 `artifacts/release/android/0.9.0/FurtherAgent-Android-0.9.0.apk`，同目录提供 `.sha256` 校验文件。最低 Android 8.0 / API 26，本包包含 ARM64 手机及 x86_64 模拟器，不包含 32 位 ABI；这是使用本机开发签名的内部预览包，尚未建立正式发行签名与自动更新渠道。

1. 将 APK 复制到手机，允许当前文件管理器安装应用后安装。
2. 首次打开显示标注清楚的离线示例，可直接查看聊天布局和 Markdown。示例不会执行任务。
3. 在电脑或服务器开启 FurtherAgent 共享；手机点右上角「连接」，填写同一中继地址和远端授权码。
4. 连接后选择会话，再发送消息。新建、模型、停止、确认和文件入口按接入端声明的能力开放。

每台远端设备目前只允许一个控制端，若提示已被占用，需先断开原控制端。手机不会启动本地 Agent；实际任务继续在电脑或服务器执行。公网需要可信 HTTPS，HTTP 仅允许本机回环联调。

## 首版功能

| 范围 | 行为 |
| --- | --- |
| 连接 | 中继配对、断开、忘记连接；地址存 Preferences，授权码存 Android SecureStorage；重连只恢复读取 |
| 会话 | 列表、搜索、刷新、切换；支持的 Agent 可新建、归档当前会话 |
| 对话 | 原生输入区；Markdown 表格、代码块；用户与 AI 分色；工具和思考折叠；复制消息；安全外链 |
| 任务操作 | 发送、停止、支持的审批与问答；模型和提供商设置、能力允许时切换模式 |
| 附件 | Android 文件选择器上传，分块传输及哈希检查；远端产物下载后调用系统分享/保存 |
| 恢复与草稿 | 草稿按连接和会话保存在本机；前台恢复读取；提交结果未知时保留草稿并要求用户核对，不自动重发 |
| 界面 | 明暗主题、系统栏与键盘安全区、离线预览 |

消息 WebView 不加载远端图片、脚本或原始 HTML；图片先以链接和附件处理。待发送附件引用只保存在当前进程，重启后需重新选择。断开连接不停止远端任务。

## 实现与构建

- `src/FurtherAgent.Android`：仅 `net10.0-android`，MAUI 页面、生命周期、Android 清单、图标和平台存储。
- `src/FurtherAgent.Mobile`：可独立测试的连接/会话控制器和 Markdown 展示；引用现有 Core。本版尚未拆分 Core 中的 Host 类型，但手机入口不运行 Host 或桌面适配器。
- `FurtherAgent.Android.slnx`：Android 构建入口。常规 `FurtherAgent.slnx` 仍可在没有 Android 工作负载时构建桌面和协议测试。
- 首版关闭裁剪与 AOT，优先验证序列化和运行行为，包体优化后续处理。
- 关闭 `AndroidEnableMarshalMethods`，规避本机复现的 Release 启动 JNI 回调异常；相关上游记录见 [MAUI issue 35209](https://github.com/dotnet/maui/issues/35209)。

环境：.NET SDK 10.0.401，MAUI 10.0.0，Android SDK 平台 36 / Build Tools 36.0.0，Microsoft JDK 21。发布命令：

```powershell
dotnet workload install maui-android
./scripts/Publish-Android.ps1 -AndroidSdkDirectory '<Android SDK 路径>' -JavaSdkDirectory '<JDK 21 路径>'
```

脚本发布 Release APK、复制为固定交付文件名并写入 SHA256。工作负载、JDK、模拟器、APK 和联调凭据均不提交进仓库。

## 验证与限制

- 常规 Release 解决方案构建成功，0 警告、0 错误。
- 共享协议及移动控制器自动检查：171 项全部通过，包括连接、发送、未选中会话通知隔离、失败切换保留、结果未知不重放、确认、新建、归档和 Markdown 注入防护。
- WPF 与持久化回归：20 项全部通过。
- Android APK 已完成 Release 构建及 v2/v3 签名校验。在 Android 11 / API 30、x86_64 模拟器中实际安装、启动并通过隔离 UI 联调：配对、会话列表、选中会话、发送 `FINAL_APK_OK`、收到中文回复与 Markdown 表格/代码块、点击允许确认并收到 `accept` 回执。
- 已检查键盘弹起时顶部、输入和发送按钮的布局；覆盖安装后保存的连接可读取；系统返回退出后再次打开能够重新连接。最终 UI 验证期间 crash 日志为空。
- 文件入口已打开 Android 系统选择器；模拟器选择测试文本文件未完成返回，手机 UI 上传/下载/分享完整链路仍待验收，不能以共享协议文件测试通过替代。
- 运行截图保存在 `artifacts/android-validation/preview.png`、`chat.png`、`keyboard.png`；隔离模拟器与测试中继在验收结束后关闭。

交付 APK 的 SHA256：`563C1B49034FA96D5BF2C88E00FCF1A79AABF06CD29B7A803B0ABC83589A9F68`。

自动协议测试使用隔离适配器与真实中继传输，不能替代真实 Codex / Hermes / DSH 的 Android 联调。通知、语音、拍照/粘贴、内嵌图片预览、多设备管理、后台常驻、自动更新不在首版完成范围。Android 厂商电池策略、网络切换、长时间后台恢复及实体手机兼容性仍需后续实机验收。

复现隔离 UI 测试可运行：

```powershell
dotnet tests/FurtherAgent.SmokeTests/bin/Release/net10.0/FurtherAgent.SmokeTests.dll --mobile-ui-fixture
```

它仅启动本机临时中继与测试 Agent，连接资料写入忽略目录 `artifacts/android-validation/fixture.json`。模拟器通过 `adb reverse` 映射该端口后，可用手机界面完成真实 RPC 收发。创建同目录的 `stop` 文件可终止；再次运行前需移除该停止标记。测试资料不包含业务 Agent 凭据。

## 0.9.2：系统语音输入（2026-10-10）

用户选择先使用手机系统识别。Android 版本升级为 0.9.2（versionCode 902），输入框旁增加“语音”按钮；中继和 Host 继续使用 0.9.1，无需服务端更新。

- 通过 Android `ACTION_RECOGNIZE_SPEECH` 打开系统识别 Activity，按设备默认语言识别。录音与权限由识别服务处理，FurtherAgent 仅接收识别文本，没有新增应用录音权限或内置云语音账号。
- 识别文字追加到草稿末尾，已有文字保留；需要用户检查并手动发送。启动前保存草稿，异步返回只写入原会话的草稿，连接变化不会把结果写入其他会话。Activity 销毁时释放等待状态。
- 取消保持原文，空结果和服务错误提示重试；没有识别服务时提示使用输入法语音或启用系统语音服务。应用不会自动下载引擎或切换至第三方云服务。
- 系统识别服务可能联网，速度、准确率、语言与离线能力依赖手机安装的实现，不承诺离线处理或与 DeepSeek Chat 相同效果。[Android 官方接口说明](https://developer.android.com/reference/android/speech/RecognizerIntent#ACTION_RECOGNIZE_SPEECH)

验证使用最终 Release APK，在 Android 11 / API 30 模拟器覆盖安装：

1. 未安装识别服务时，按钮弹出明确提示，应用未崩溃。
2. 安装独立测试识别 Activity（仅测试包，不含在交付 APK），经真实系统 Activity 结果回调返回固定中文及英文混合文字；输入框保留 `KEEP_DRAFT` 并追加识别内容。
3. 分别返回取消、空结果与识别网络错误，原草稿保持一致，界面恢复可操作。测试替身用于验证回调与草稿行为，没有使用麦克风，不代表识别模型效果。
4. 独立 CLI 经实际隔离中继读取测试会话，确认语音返回后用户消息数仍为 0；手动点击发送后只有 1 条用户消息，包含完整草稿，测试 Agent 回复正常。
5. Android crash 日志为空；v2/v3 签名通过，签名证书与 0.9.1 相同，可覆盖安装。实体手机、麦克风权限交互、中文识别准确率与延迟仍待用户实测。

验证材料位于忽略目录 `artifacts/voice-validation`，包括测试识别器、界面 XML、`result.png` 截图、CLI 读回记录和签名检查。发布日志为 `artifacts/voice-android-publish.log`。

交付：`artifacts/release/android/0.9.2/FurtherAgent-Android-0.9.2.apk`。
SHA256：`7CE6DB4061EB68A9AE22A15654A0036220F1C772C2C712D9AC602852905D2837`。
