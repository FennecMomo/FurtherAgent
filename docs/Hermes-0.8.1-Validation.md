# Hermes 0.8.1 图片交付与流量验证

日期：2026-10-10（北京时间）。仅处理 Hermes；DSH 的图片/文件能力没有在本轮开放。

## 故障与修复

旧 HermesAdapter 每两秒无条件 Publish，DeviceHost 将完整 SessionView 转发，即使历史完全相同；SSE 增量也触发包含整个历史的快照。
例如一份 200 KiB 快照每两秒发送一次，单向应用载荷约 352 MiB/小时；这足以解释数小时 GB 级流量，但并非对用户之前账单的逐字节追溯。

现接入端丢弃内容完全相同的快照。新控制端协商 session-sync v1，通知仅携带变化后的消息尾部及元数据，最多每 500 ms 一次，队列只保留最新状态。
重连从完整快照开始，基线必须连续；控制端即使暂未注册观察者也维护同步基线。旧控制端仍收到兼容完整快照，但同样享受去重。
心跳独立于会话刷新，不通过重复历史证明在线。界面与本机状态接口新增当前 WebSocket 连接收发载荷计数。

原有文件通道缺少会话中的直接交付入口。现在 outbox 文件成为可保存附件，图片在 Markdown 图片标记位置显示服务器压缩缩略图，点击再下载并校验原图。
图片按版本缓存，文件本体不进入 SessionView。HTTP /v1/runs 仍走文字与本机文件路径；Hermes 使用自身 vision_analyze 理解图片。

## 自动化结果

- Core/中继/CLI/文件/缩略图回归：154 项通过。
- 无窗口 WPF/持久化：20 项通过，包括图片在文字前后之间的占位、渲染不请求原图、点击才请求原图。
- 250 条长消息、50 次回复变化：模拟完整快照合计 150,608,775 字节，增量载荷 16,858 字节，二者均不含首次完整历史。
- 随机图像夹具超过 5 MB，服务端 JPEG 缩略图 11,270 字节、320×240；路径穿越和非法图片检查通过。

## 真实公网链路结果

专用接入端 Hermes-FA-081-Integration 走服务器回环连接中继；本机控制端走 Cloudflare 公网 HTTPS/WSS。
真实会话上传 sample.txt 和 PNG，Hermes 读取原文、调用 vision_analyze 识别白底左红方块右蓝圆，并生成 generated.png 和 result.txt。

- 输出文件通过会话元数据被发现，最终回复包含 attachment:generated.png 图片标记与结果文件链接。
- 单独缩略图请求返回 3,950 字节 JPEG、320×180；该次接收应用载荷增加 5,761 字节。
- 随后分别下载原 PNG 与文本文件，SHA256 校验通过；结果文本保留上传校验词及视觉识别结果。
- CLI thumbnail 命令独立调用成功，不返回原图数据。
- 任务结束后保持心跳的 30 秒：接收 2,886 字节、发送 426 字节，中继和 B 端均在线。该数字不含 TLS 帧开销和独立 HTTP 健康检查，也不是持续一天的平均值。

## 部署

生产 Host 已从 /home/ubuntu/furtheragent-0.6.1/host 迁移到 /home/ubuntu/furtheragent-0.8.1/host。
迁移反馈确认旧进程退出、新进程注册成功；固定身份原字节及文件根保持不变。Hermes 网关、中继和 cloudflared 未重启，未改网络入口。
独立测试端继续保留，后续可后台验证，无需占用业务端。私有配对数据、迁移脚本与原始联调记录仅保存在忽略目录，不提交到仓库。

缩略图编解码使用纯托管 StbImageSharp/StbImageWriteSharp，固定包版本；接口与实现依据其官方项目文档：
[StbImageSharp](https://github.com/StbSharp/StbImageSharp)、[StbImageWriteSharp](https://github.com/StbSharp/StbImageWriteSharp)。
