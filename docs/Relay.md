# FurtherAgent 0.2.0 Linux 中继候选包

这个包是中继服务 `FurtherAgent.Server`，不是 Hermes 接入端 `FurtherAgent.Host`。
Linux x64 自包含发布，包含 .NET / ASP.NET Core 运行时；不要求服务器安装 .NET 10。
首次 Linux 启动、HTTPS/WSS 和跨机器转发尚待服务器验证。本包沿用 0.2.0 中继程序，没有修改协议或 Hermes 配置。

## 本机临时启动验证

先校验收到包的 SHA256，解压到新目录，不覆盖已有服务。解压后执行：

```bash
chmod +x FurtherAgent.Server
./FurtherAgent.Server --urls http://127.0.0.1:47320
```

启动前检查 47320 是否被占用；如被占用，停止本轮验证并报告，不终止其他进程。
另一个终端调用：

```bash
curl --fail --silent --show-error http://127.0.0.1:47320/health
```

预期返回 `{"status":"ok","version":"0.2.0","mode":"relay"}`。
结束临时验证时，对自己启动的前台进程使用 Ctrl+C / SIGTERM。
这个步骤只验证中继启动，不连接 Hermes，不执行 Agent，不修改系统服务或公网路由。

## 公网连接前提

需要确定专用 HTTPS 域名及现有反向代理/隧道的管理方式，再添加指向本机 47320 的路由。
代理应支持 WebSocket Upgrade，保持长连接，空闲超时大于心跳间隔（建议至少 75 秒）。
客户端使用受信任的证书，不通过关闭证书校验来绕过错误。只暴露中继入口，Hermes 8642 继续保持回环。

当前中继拒绝含 Origin 的 WebSocket 请求，面向 FurtherAgent 桌面/接入端客户端，不是浏览器控制面。
若反向代理或访问控制层要求浏览器登录，现有客户端不会完成这个登录流程；路由配置需与原生客户端相容。
当前限流按 TCP 对端地址计数；反向代理后可能聚合为同一地址，不据此声称生产级多租户限流。

HTTPS 健康检查成功只证明普通 HTTP 路由可达；还要让接入端和 WPF 实际配对，验证 /host、/join、/control 的 WebSocket 长连接。

部署完成后：

- A 的 WPF：中继服务地址填写已经验证的 `https://中继域名`。
- B 的 Hermes 接入端：`FURTHERAGENT_SERVER` 使用同一个 HTTPS 地址。
- B 的 `HERMES_API_URL` 仍是 `http://127.0.0.1:8642`，两种地址不可互换。
- 中继无需 Hermes API key。key 只给同机 Hermes 接入端，不传给 WPF 或中继。

## 可选持久服务

完成临时启动和网络路径验证后，可使用包内 `furtheragent-relay.service` 用户服务模板。
模板程序目录是 `~/.local/share/furtheragent-relay`，与 Hermes 接入端目录分开。
安装服务及设置登录外常驻应按实际服务器的用户服务管理方式处理，不修改 Hermes 服务。

中继重启会丢失内存中的在线设备、授权码和票据。B 会重新注册并生成新码，A 需要新码。
中继自动重启与 Hermes 接入端重启是两回事：0.2.0 接入端仍应避免在运行期间重启，以免丢失运行归属。

中继拥有转发内容的可见性，当前尚无端到端加密；授权码是控制凭据，不应公开发布。
