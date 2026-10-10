# Android 0.9.3：实时语音输入

## 使用

安装 `artifacts/release/android/0.9.3/FurtherAgent-Android-0.9.3.apk`，可覆盖 0.9.2。

1. 在阿里云百炼开通北京地域服务，创建可调用 `fun-asr-realtime` 的 API Key。
2. 手机首页“连接”→“语音设置”，填入 Key 并保存。可选填业务空间 ID，使用北京业务空间专属接入地址。
3. 连接设备、打开会话，点击“语音”，允许麦克风权限。看到“正在听”后开始说话。
4. 文字逐步更新；点击“结束”停止采集并等待最终结果。检查、修改文字后自行点击“发送”。

首次点击语音但没有 Key 时会打开配置页。已有密钥不会回填到输入框；留空保存保留原密钥，可以单独删除。密钥使用 MAUI SecureStorage 保存，APK 不含密钥。已有设备授权码与语音 Key 独立。

## 行为与边界

- 手机直接通过 WSS 连接阿里云；服务器、Host 和 Hermes 无须升级。
- Android AudioRecord 采集 16 kHz、单声道、PCM 16-bit；服务确认 task-started 后才开启麦克风。
- 发送 run-task、二进制音频、finish-task；接收中间和最终句子，按 sentence_id 替换修订，不把中间结果反复追加。晚到的中间结果不会覆盖最终结果。
- 原有草稿保留，语音追加在后面，始终归属启动录音的会话。识别期间禁用编辑、发送和会话切换。
- 单次录音最长 3 分钟；切到后台或锁屏停止采集，窗口销毁取消请求。初始化时可以取消，录音时点击结束。
- 连接/启动及最终结果等待有限时，网络错误不自动重传录音。已显示文字保留供人工核对；超时后的部分文字可能缺少最后一句修订。
- 录音只在内存中处理并直接传给阿里云，不写音频文件；识别文字作为普通会话草稿留在手机。服务费用由配置 Key 的账户承担。
- 默认北京公共地址 `wss://dashscope.aliyuncs.com/api-ws/v1/inference`；可选业务空间专属域名，禁止任意自定义地址以免误发密钥。
- 本版按个人自用、用户自带 Key 实现。若将来面向多人共用一个付费账户，应增加后端凭证服务和额度控制。

## 验证

`dotnet run --project tests/FurtherAgent.SmokeTests -c Release -- --speech-checks`

28 项检查通过：分片 WebSocket 文本帧、鉴权头、模型/音频参数、先确认再送音频、中间结果替换、最终结果幂等、心跳、保留草稿、显式 finish、鉴权失败不打开麦克风、断开/取消停止上传、缺失结束事件超时、业务空间地址校验。

Android 11 模拟器已验证覆盖安装、设置页显示、测试密钥保存与重新打开、删除密钥、系统麦克风授权弹窗。使用无效测试 Key 触发连接失败后，原草稿 `KEEP_VOICE_DRAFT` 保留，编辑/语音入口恢复；这不代表有效 Key 的云端识别成功。APK 签名校验通过，沿用原开发签名。

这些测试使用本机模拟识别服务与合成音频，不代表真实中文准确率或云端延迟。用户暂未配置 Key，也未连接实机，真实录音识别和手机端延迟仍待验收。

## 官方协议

- [接入地址和鉴权](https://help.aliyun.com/zh/model-studio/fun-asr-realtime-websocket-api)
- [客户端事件](https://help.aliyun.com/zh/model-studio/fun-asr-client-events)
- [服务端事件](https://help.aliyun.com/zh/model-studio/fun-asr-server-events)
- [获取 API Key](https://help.aliyun.com/zh/model-studio/get-api-key)
