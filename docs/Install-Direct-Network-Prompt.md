# 安装 Agent 标准提示词：FurtherAgent 始终直连

适用于 Windows 0.14.19 / Android 0.12.8 及后续支持固定直连的版本。把下面整段提示词交给负责安装或升级的 Agent；它需根据目标机器的实际 VPN 配置执行，不能照搬某台电脑的路径。提示词不包含连接码、订阅地址或密钥。

## 可直接复制的提示词

```text
请安装或升级 FurtherAgent，并完成直连配置。目标：FurtherAgent 自身联网不使用系统 HTTP 代理；如果本机开启虚拟网卡 / TUN，则其流量应命中 DIRECT，不经过 VPN 远端代理节点，避免代理节点超时影响连接。

请直接完成安装范围内的检查、必要修改和验证：

1. 核实版本和安装位置。
确认版本支持固定直连（Windows 0.14.19 / Android 0.12.8 起）。核对实际运行程序路径、版本及启动入口，不把源码已修改当成已安装。保留用户会话、身份和保存的连接。

2. 识别网络环境。
读取系统代理状态、TUN 状态、VPN 客户端及内核版本、当前规则/全局模式、正在使用的配置及持久覆写入口。没有 VPN/TUN 就跳过对应配置，不额外安装 VPN。
区分系统代理开关、TUN 开关和软件内的全局/规则模式。开启 TUN 仍可分流；全局模式未必会执行普通 rules。

3. 确定最小匹配范围。
核实实际存在的 FurtherAgent.Client.exe、FurtherAgent.Host.exe、FurtherAgent.Cli.exe，使用该 VPN 支持的进程名或稳定进程路径规则。Android 包名为 top.fennecmomo.furtheragent，按该客户端的应用分流能力配置。
如果通过 dotnet 启动 DLL，不能把所有 dotnet.exe 都设为直连；使用实际服务域名等限定条件。不要把 node.exe、python.exe、Codex、Hermes、DSH 或其他 Agent 软件整体改为直连。
从 FurtherAgent 配置读取实际中继域名，仅提取主机名，避免输出连接码和凭据。默认中继是 furtheragent.fennecmomo.top，默认 Android 更新源是 bridge.fennecmomo.top；自定义地址以实际配置为准。语音及其他启用服务也按实际目标核对，不把整个 aliyuncs.com 等共享域名设为直连。

4. 添加持久、优先的直连规则。
修改前备份受影响的配置。使用客户端支持的持久覆写/合并/扩展配置，避免只编辑订阅刷新或重启后会被覆盖的生成文件。
FurtherAgent 规则必须指向 DIRECT，优先于会把它送往代理节点的规则。保留其余订阅、节点、策略组和规则，不把整台电脑或其他应用都改为直连。重复安装更新或去重既有 FurtherAgent 规则，不反复追加。
如果全局模式绕过普通规则，使用该客户端支持的全局例外，或在保持其他流量原有出口策略的前提下转换为规则分流。若无法保持其他流量行为，说明具体差异并让用户选择；不能宣称添加一条不会执行的规则已完成任务。

5. 检查 DNS 依赖。
检查 FurtherAgent 实际服务域名的解析路径，确保 DNS 请求不被强制送到不可用的代理节点。按当前 VPN 版本支持的域名 DNS 策略及直连解析机制配置，避免影响其他域名。
核对 nameserver-policy、direct-nameserver、respect-rules、DNS 服务器自身路由和 fake-ip 的相互作用；规则里出现 DIRECT 不代表解析也已直连。不要为省事全局关闭 fake-ip、替换系统 DNS 或写死域名当前 IP。若不能限定修改范围，明确记录剩余依赖。

6. 校验与加载。
使用所安装 VPN 版本提供的配置校验方式，校验通过后优先热加载。检查实际生效配置和规则顺序，确认订阅更新会保留该覆写。不要在交付报告中输出订阅链接、API 密钥或连接码。

7. 做必要连接冒烟检查。
保持用户 VPN 正常运行，通过 FurtherAgent 自身确认实际服务连接及会话只读同步可用；结合 VPN 连接记录或等价诊断，确认匹配规则和最终出口为 DIRECT，而非远端代理组。浏览器或 curl 访问成功不能单独证明 FurtherAgent 的路径正确。
不主动停掉 VPN，不把所有应用切换到故障节点，不做干扰其他工作的断网测试。代理节点故障、重启持久性和订阅刷新测试若未执行，写入项目待测试清单并注明未测试。若没有实际连接或无法取得路径证据，只能报告配置已完成、路径待验证。

8. 交付结果。
报告安装版本及实际运行路径、修改的持久配置位置、新增规则、备份及回退方式；分别说明应用层直连、TUN DIRECT 和 DNS 路径的证据及未测试项。
应用层直连不等于完全绕过 TUN。本任务消除的是对 VPN 远端代理节点的依赖，不宣称能规避物理断网、服务端宕机或 VPN 本地核心故障。
```

## Mihomo / Clash 系客户端规则示例

以下可合并到现有规则前部，**不是完整 VPN 配置**。Agent 必须核对进程名、域名、运行模式、版本语法和持久覆写机制；不要拿此段覆盖整个配置。

```yaml
rules:
  - PROCESS-NAME,FurtherAgent.Client.exe,DIRECT
  - PROCESS-NAME,FurtherAgent.Host.exe,DIRECT
  - PROCESS-NAME,FurtherAgent.Cli.exe,DIRECT
  - DOMAIN,furtheragent.fennecmomo.top,DIRECT
  - DOMAIN,bridge.fennecmomo.top,DIRECT
  # 此后保留用户既有规则；不要在这里新增 MATCH,DIRECT。
```

域名规则适用于进程识别不可用的场景，但会让同一域名的其他访问也直连。自定义中继必须增加实际主机名；Android 按 VPN 的应用分流能力处理。DNS 需结合现有策略检查，示例不直接覆盖 `dns` 配置。

官方参考：[规则优先级与进程匹配](https://wiki.metacubex.one/config/rules/)、[运行模式](https://wiki.metacubex.one/config/general/)、[DNS 策略](https://wiki.metacubex.one/config/dns/)。
