# 0.14.1 桌面与 Hermes 接入端部署记录

时间：2026-10-13，北京时间。用户授权打包替换桌面端，并谨慎更新服务器上的 Hermes 接入端。

## 桌面

- 发布 Windows 0.14.1，使用原 `Update-DesktopClient.ps1` / 本地控制管道正常退出旧进程、启动新进程。
- 当前安装快照：`artifacts/current/builds/0.14.1-20261013-151151-087829f9`；9 个发布文件与安装快照逐一哈希一致。
- 快捷方式仍通过 `scripts/Start-LatestClient.ps1` 读取当前版本指针，图标指向新快照。
- 新进程 PID 29440，报告版本 0.14.1、ready=true；已验证 Hermes 自动重连及当前 Codex 连接/双心跳在线。
- 用户配置、保存连接和旧安装快照保留。本次未重新打包或安装 Android。

## 服务器

完整 Linux x64 自包含包：`FurtherAgent-Hermes-0.14.1-linux-x64.tar.gz`，35,686,962 字节。公网上传耗时约 25 分钟，期间旧接入端保持在线；仅在完整文件校验通过后进入部署。

SHA-256：`3F7DAC121FEAA48DCB579D5B57804ABBF19FA81E7D995F2A652CC62BBFE95A86`。

| 入口 | systemd 用户服务 | 原 Host PID | 新 Host PID | 新程序目录 |
| --- | --- | --- | --- | --- |
| default | `furtheragent-host-default.service` | 540610 | 556576 | `/home/ubuntu/furtheragent-0.14.1/host` |
| mom | `hermes-furtheragent-mom.service` | 3751510 | 558077 | 同上 |

先更新 default，经 Windows 公网复验后再更新 mom。每次只增加对应服务的 `90-furtheragent-0141.conf` drop-in，清空并替换 ExecStart；原 cwd、环境文件、身份文件、文件根和运行记录配置保留。没有将旧目录中的运行库拼入新包。

预检分别执行两个 API 的 `--probe`，并用临时身份实测候选 Host 注册。实际切换由独立 systemd worker 执行，另设 240 秒回滚定时器：worker 失败或卡死时可恢复旧 ExecStart，避免依赖已经断开的控制连接。成功需固定码配对、连续 30 秒 PID/启动时间稳定、身份/配置未变、受保护进程未变。正常成功后解除回滚定时器。

以下进程与启动时间保持原样：default 网关 PID 535794、mom 网关 PID 3751064、中继 PID 540289、cloudflared PID 1019。中继仍为 0.9.1，本次未升级 Hermes 本体或中继。

部署后的应用 DLL 哈希：

- Host：`DFF7F441890F40DF5270D20B0162D959FB9502792767B8543DF387D958098347`
- Core：`4532FF3288DCA9DF26B4ADA4EF61007647910284864F6A2403FE81961749019E`

## 验收与恢复材料

- Windows 经原授权码重连 default，读取 44 个会话并通过心跳；重连 mom，读取 87 个会话并通过心跳。
- 新 default 接入端真实读取 62 个模型，声明思考强度能力。独立验证会话仅请求固定文本回复，收到了同一会话的 `session-completed` 事件，没有操作业务会话。
- 部署脚本还在本地隔离测试了成功、注册失败、配置变化三个分支，失败分支恢复旧程序及身份。
- 接入端配置、身份和文件目录保留。原 `/home/ubuntu/furtheragent-0.9.1` 未删除，回退所需单元备份和私有状态位于 `/home/ubuntu/furtheragent-0.14.1`；维护脚本为 `/home/ubuntu/furtheragent-deploy-0141-final.py`，任务记录为 `/home/ubuntu/furtheragent-deploy-0141-todo.txt`。
- 成功后的手动回退需要核对并恢复本次新增 drop-in，再单独 reload/restart 对应 Host；不得重置身份、重启网关或删除用户目录。脚本的定时自动回滚入口在报告 complete 后会主动跳过，防止迟到的定时器撤销已验收版本。
- 脱敏检查报告、本地探测日志和部署材料留在忽略目录 `artifacts/integration/update-0141`；不把私有连接信息或完整环境加入 Git。

公网连接在升级前后均偶发 TLS/握手超时。只重试已确认没有提交业务请求的连接或只读检查；未自动重放部署命令或模型任务。最终验收以实际成功的连接、会话读取、心跳和完成事件为准。
