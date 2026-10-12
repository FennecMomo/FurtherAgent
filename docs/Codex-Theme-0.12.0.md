# 0.12.0 Windows 桌面风格

默认采用用户提供的 Codex 浅色窗口截图作为视觉参考。保留 FurtherAgent 原始绿色风格。

## 切换入口

- Codex 风格：左下角齿轮 → 设置 → 界面风格；顶部「视图」菜单也可切换。
- 原始风格：左侧「设置」→ 界面风格。
- 立即切换，保存至现有 `settings.json` 的 `uiTheme` 字段，重启读取。未设置时默认 `codex`，原始风格为 `original`。
- 切换复用窗口与控件，不重新连接设备，不清空会话草稿和附件。连接配置继续由原有存储管理。

## 布局与功能

Codex 风格包含暖色窗口栏、窄导航栏、项目树、白色会话区、蓝色用户气泡、底部圆角输入框。附件工具栏移入输入框底部，模型与发送靠右；运行时继续显示停止及打断发送。左侧保留添加工作空间和连接设备，搜索由项目栏放大镜展开。原始风格恢复绿色配色、原有搜索框、路径和日期、诊断状态及输入框上方附件工具栏。

导航图标对应现有会话、连接、归档、会话文件、诊断和设置。标题栏支持拖动、最小化、最大化和关闭到托盘；菜单提供实际可用的操作。关于窗口明确应用为 FurtherAgent。未增加 Codex 的语音、通知中心、dot 等能力。

多连接、工作空间分组、远端新建工作空间、附件和模型操作继续使用原有逻辑。本次无需升级远端或中继；Android 界面未改。

## 验证

- Release 构建与原有 27 项 WPF / 持久化检查。
- 直接加载实际 MainWindow / App XAML，渲染 Codex、原始、940×650 小窗口、设置页及模型弹窗。
- 双向切换验证草稿、消息数据源、附件工具栏实例保留。
- 自定义可编辑模型模板接受未列出的模型 ID，切换主题后保留文本，选择已有项后正常更新文本。

重跑：

```powershell
dotnet build tests/FurtherAgent.WpfChecks -c Release
dotnet tests/FurtherAgent.WpfChecks/bin/Release/net10.0-windows/FurtherAgent.WpfChecks.dll
dotnet tests/FurtherAgent.WpfChecks/bin/Release/net10.0-windows/FurtherAgent.WpfChecks.dll --themes E:/etc/FurtherAgent E:/etc/FurtherAgent/artifacts/theme-0.12.0
```

渲染测试使用合成会话，不发送真实任务；用于排版与控件状态检查，不代表所有远端功能均重新端到端执行。视觉比较记录见 [design-qa.md](design-qa.md)。
