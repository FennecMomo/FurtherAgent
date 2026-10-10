# 双向文件传输（0.8.1）

## 0.8.1 Hermes 图片交付

Hermes 生成的文件仍写入本会话 outbox。接入端将文件名、大小和修改版本传给客户端，聊天中直接出现保存入口。
图片回复使用 `![图片说明](attachment:文件名)`，客户端在该位置嵌入缩略图；文件使用 `[文件名](attachment:文件名)`。
已有 Markdown 本地路径和 MEDIA 标记仅在文件名匹配本会话已交付文件时激活，绝不读取任意服务器路径或自动抓取外部 URL。
没有图片标记时，已交付图片在对话末尾提供缩略图作为后备入口。

- `files/thumbnail` 在 B 端生成 JPEG，最长 320×240，质量 65，编码结果不超过 64 KiB；仅请求缩略图不会返回原文件。
- PNG/JPEG/GIF/BMP 可生成缩略图；源文件限 20 MiB、4000 万像素、单边 16384 像素。超限保留下载入口，不偷偷下载原图。
- 透明像素合成白底，GIF 缩略图只展示首帧；不承诺缩略图保留动画或全部元数据。
- B 按文件修改版本缓存，A 按连接/会话/文件/版本缓存，最多并发两个缩略图请求。相同对话刷新不重新下载图片。
- 只有点击图片才走原图下载和 SHA256 校验。当前原图窗口保留 10 MiB/4000 万像素限制；更大文件可通过保存文件下载。
- CLI：`thumbnail --session ID --id outbox/picture.png --output preview.jpg`，保存的文件是 JPEG；`download` 才请求原文件。
- 历史里的任意磁盘路径并不自动变成附件。若旧回复只给出了其他路径，需让 Hermes 将产物复制到会话 outbox。

## 共享实现

CLI 和 WPF 都直接调用 Core/FileTransfer.cs 中的 FileTransferClient；传输、分块、SHA256 校验、取消、落盘与附加消息规则不在两端各写一遍。B 的 DeviceHost 使用 FileTransferStore 统一处理同一组 files/* 与 send-files RPC。Codex/Hermes 适配器继续负责 Agent 会话控制。

CLI 和 WPF 的参数处理、文件对话框、进度显示等交互层不同；已有非文件会话命令仍有各自的 UI/CLI 编排，但共用 RemoteDevice、RpcPeer 和 Adapter。

## 使用

0.5.0 WPF 增加「粘贴图片」和会话文件列表中的「预览图片」。图片通过同一文件通道上传下载，预览限制 10 MiB/4000 万像素，仅解码选中的图片，不自动抓取聊天内远程图片。服务器 `/v1/runs` 仍是文本入口；附件路径能否被视觉理解取决于 Agent 图像工具，不声称接通原生多模态消息。

WPF：打开会话 → 添加文件 → 等待上传完成 → 输入要求并发送。附件发送失败时保留，原样重试使用原文件 ID。移除待发附件只取消下一条消息引用，不删除服务器文件。

Agent 输出文件写到会话 outbox 顶层，点击「会话文件 / 下载」读取列表后保存。不会扫描整台远端电脑，也不把消息中的任意路径直接变成下载链接。列表区分上传文件与 Agent 输出。

CLI（后台）：

```powershell
$env:FURTHERAGENT_CODE_FILE = '本机保存专用授权码的文件'
$cli = 'E:/etc/FurtherAgent/artifacts/release/FurtherAgent-Cli-0.4.0-win-x64/FurtherAgent.Cli.exe'
& $cli create --title 'FurtherAgent 文件联调'
& $cli upload --session '会话ID' --path 'E:/test/sample.txt'
& $cli files --session '会话ID'
& $cli send --session '会话ID' --file-id '上传返回的id' --text '读取附件，在输出目录生成 result.txt，内容为附件字数。' --seconds 30
& $cli files --session '会话ID'
& $cli download --session '会话ID' --id 'outbox/result.txt' --output 'E:/test/result.txt'
# 也可以合并上传与发消息：send --session ID --file PATH --text TEXT。
# 若上传成功但发送结果未知，保留 uploaded 事件里的 ID，使用 --file-id 原样重试，勿重新上传形成不同路径。
```

新控制端向支持文件的 Host 发送消息时，会添加当前会话输出目录说明；有附件时附带 B 本地的实际文件路径。Agent 仍需具备读取文件/生成相应格式的工具，文件传输不等于模型原生理解所有格式。图片输入仍通过文件路径和 Hermes 可用的视觉工具处理；0.8.1 已实现聊天内缩略图及按需原图，不声称 /v1/runs 支持原生多模态正文。

## 存储与边界

- Host 与 Agent 必须能访问同一个本机文件系统。当前推荐 Hermes Host 与 Hermes 同机运行；API 若指向别的机器，不能直接使用本机路径方案。
- `FURTHERAGENT_FILES_ROOT` 指定独立文件根目录，默认本地应用数据目录下 FurtherAgent/files。每个 session ID 以 SHA256 隔离目录，分 inbox/outbox，重连/重启不删除已完成文件。
- Linux 创建根目录时设为 700。它不是 Agent 操作系统沙箱，只限制客户端文件 RPC 路径；Agent 原有系统权限不变。
- 单文件最多 100 MiB，每块 128 KiB，最大 20 个消息附件；新上传受每会话 200 文件/1 GiB 限额约束，同一 Host 最多 8 个未完成上传。
- 不覆盖上传文件；每次生成唯一名称。下载到同目录临时文件，完整长度和 SHA256 通过后才改为目标文件名，不覆盖已有目标。
- 仅暴露会话根目录下 inbox/outbox 顶层文件；拒绝客户端路径穿越与检测到的符号链接/重解析点。没有提供任意服务器路径读取接口。
- 文件不会塞进 SessionView 或长消息。仍复用同一 WebSocket/RPC，每块有界并顺序确认；不是独立并行高速传输通道，公网吞吐受往返延迟影响。
- 取消时尝试删除本次暂存；断线不自动重传，也暂不支持断点续传。运行进程内超过一小时未活动的上传在下次文件操作时清理；上次崩溃遗留超过一天的 .part 在访问对应会话目录时清理。完成文件保留，管理员按需清理。
- 同时观察与文件传输可共用通道，但 WPF 传输时暂时禁用发送/停止等操作以避免会话切换；可先取消传输。CLI 每条命令退出即释放控制连接。

## 部署

现有 0.2.0 中继可以继续转发，**不需要升级或重启中继**。基础文件传输要求 Host 与 WPF/CLI 至少 0.4.0；本页的聊天内缩略图及增量同步要求两端升级到 0.8.1。旧 Host 没有 CanFiles 能力，界面禁用文件按钮，CLI 明确提示升级。

不要覆盖正在运行的 Host。推荐在新目录启动专用联调 Host、配置独立 FURTHERAGENT_FILES_ROOT、返回专用授权码。业务 Host/网关保持运行。完整转交指令见 Hermes-Files-0.4.0-Handoff.md。

## 验证

本地真实回环中继验证分块二进制双向传输、中文文件名、空文件、SHA256、文件中途变化、取消、跨会话拒绝、路径穿越拒绝、额度与偏移检查、重连后列表，以及 CLI 上传/列表/下载/附带文件发送。这部分 Agent 输出使用测试适配器在 outbox 写文件。

2026-10-09 已通过独立 Host 和后台 CLI，完成公网中继上的上传、真实 Hermes 读取附件并生成结果文件、下载与内容/SHA256 核对，见 [实机联调记录](File-Transfer-Live-Validation.md)。WPF 已编译检查，遵照用户要求没有启动窗口或操作桌面；共享传输核心通过不等于桌面交互已经实测。
