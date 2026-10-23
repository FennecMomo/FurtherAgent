# FurtherAgent 固定直连策略

2026-10-23，Windows 0.14.19 / Android 0.12.8。用户已确定：应用自身忽略系统代理；安装 Agent 为 VPN 的 TUN 模式添加 FurtherAgent 直连规则。此策略取代此前三种连接模式的提案，不自动回退到代理。

## 应用行为

- `DirectNetwork` 为应用自身的 HTTP 请求创建 `UseProxy=false` 的处理器，忽略系统代理及 HTTP_PROXY / HTTPS_PROXY / ALL_PROXY 等代理环境设置。
- 中继健康检查、注册、配对与 WebSocket 握手使用同一个直连 HTTP 客户端。覆盖 Windows 主窗口、Mini、独立 Host、CLI 和 Android 的共用中继连接。
- Android 更新检查与下载、Fun-ASR 连接及 Hermes API 连接同样使用明确的直连处理器；DSH API 原已禁用代理。
- 不修改操作系统代理，不修改 Codex / Hermes / DSH 自身访问模型服务的网络设置。
- 服务不可达时保留原有超时、心跳和自动重连逻辑，不尝试代理，不自动重发用户消息。

## 安装阶段的 TUN 规则

应用里的 `UseProxy=false` 不能改变 TUN 路由。安装者应使用 [安装 Agent 标准提示词](Install-Direct-Network-Prompt.md)，识别 VPN 产品及实际生效配置，将 FurtherAgent 进程和实际服务域名设为优先 DIRECT，并确认相关 DNS 不依赖代理节点。

提示词要求使用持久覆写配置、保留其他软件规则、验证生效规则与实际连接路径。只写入规则文件不能算验证通过。没有安装 VPN 的设备无需创建 VPN 配置。

目标是消除 FurtherAgent 对 VPN 远端代理节点的依赖。TUN 核心、物理网络、DNS 或 FurtherAgent 服务本身故障仍需由状态提示和重连处理。

## 验证边界

应用层使用隔离本机中继及不可用的进程内默认代理做必要冒烟检查，不改变真实系统代理。真实 TUN 规则由安装 Agent 按目标机器执行和验证；本次没有修改当前电脑的 VPN 配置。真机和真实 VPN 项目见 [待测试清单](Pending-Tests.md)。

技术依据：[Microsoft WebSocket 自定义 HTTP 处理器](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/websockets)、[Mihomo 规则](https://wiki.metacubex.one/config/rules/)、[Mihomo DNS 策略](https://wiki.metacubex.one/config/dns/)。
