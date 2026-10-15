# Hermes 机器人语音转写：Fun-ASR-Realtime

服务器 Hermes 0.21.0 的两个 profile 原来都选择本地 `faster-whisper medium`。本接入把语音消息交给与 FurtherAgent Android 相同的阿里云百炼 `fun-asr-realtime` WebSocket 协议。Hermes 自带的微信 Silk → WAV 转换保持原样；新命令型 STT provider 接收 Hermes 准备好的音频文件，再用 ffmpeg 转为 16 kHz、单声道、16-bit PCM，按 100 ms 的音频时长发送二进制帧。

实现文件为 [`deploy/hermes-funasr-stt.py`](../deploy/hermes-funasr-stt.py)。它先等待 `task-started`，逐句合并 `result-generated`，在 `task-finished` 后只把转写正文写到 Hermes 提供的输出文件。API Key 从权限为 `0600` 的 `/home/ubuntu/.config/hermes-funasr/api.key` 读取；脚本的 stdout 不输出密钥、音频、服务端响应或转写正文，失败时 stderr 只输出固定错误说明。Hermes 网关在配置 provider 失败时仍可用本地 STT 回退。

两个独立配置文件分别为 `/home/ubuntu/.hermes/config.yaml`（default）和 `/home/ubuntu/.hermes/profiles/mom/config.yaml`（mom）。它们的 `stt` 段各自增加同一个命令型 provider，其他原有 `stt.local` 设置保留：

```yaml
stt:
  provider: funasr_mobile
  providers:
    funasr_mobile:
      type: command
      command: "/home/ubuntu/.hermes/hermes-agent/venv/bin/python /home/ubuntu/.config/hermes-funasr/stt.py --input {input_path} --output {output_path} --key-file /home/ubuntu/.config/hermes-funasr/api.key"
      format: txt
      language: zh
      timeout: 650
```

`stt.provider` 在每次转写时从配置重新读取，因此配置切换无需重启网关。回退时把对应 profile 的 `stt.provider` 恢复为 `local`，或恢复部署前保留的原字节备份；不要删除原来的本地模型。配置文件不存储 API Key 值。

2026-10-15 已按 mom、default 的顺序切换两个运行中的 Hermes 网关，未重启服务。切换前的配置备份分别保存在 `/home/ubuntu/.config/hermes-funasr/rollback/config.yaml.mom.20261015-161021.bak` 和 `/home/ubuntu/.config/hermes-funasr/rollback/config.yaml.default.20261015-161825.bak`。两个 profile 均通过 Hermes 转写入口验证了公开 WAV 样本及由该样本生成的微信 Silk 文件，返回 `provider=funasr_mobile` 和预期文本。尚未观察到真实用户语音消息进入已运行网关后的端到端结果；首次真实语音可据此再确认平台收发链路。

阿里云协议依据：[WebSocket 连接与调用顺序](https://help.aliyun.com/en/model-studio/fun-asr-realtime-websocket-api)、[客户端事件](https://help.aliyun.com/zh/model-studio/fun-asr-client-events)、[服务端事件](https://help.aliyun.com/zh/model-studio/fun-asr-server-events)。Hermes 命令型 STT provider 的配置见[官方语音文档](https://hermes-agent.nousresearch.com/docs/user-guide/features/tts/)。
