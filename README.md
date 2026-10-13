# Codex 额度浮窗（粉色版）

这是一个 Windows 桌面小工具，用于在 ChatGPT/Codex 窗口旁显示当前账号的两个用量窗口：

- 5 小时额度：显示剩余百分比和精确重置时刻。
- 一周额度：显示剩余百分比以及“月、日、时、分”的重置时间。

界面采用雾粉背景、深炭黑正文、莓红标题和淡玫瑰边框。浮窗只在 ChatGPT/Codex 位于前台时显示，不占任务栏，也不会出现在 Alt+Tab 列表中。

## 主要功能

- 每 5 秒读取一次最新额度。
- 显示格式示例：`5小时：剩余 70%（14:09 重置）`。
- 显示格式示例：`一周：剩余 56%（10月18日 11:57 重置）`。
- 支持拖动、置顶、收起、恢复显示和退出。
- 支持开机登录后自动启动。
- 安装后在桌面创建“打开额度状态栏”快捷按钮。
- 支持 `Ctrl + Alt + Q` 唤回或启动浮窗。
- 单实例运行：重复启动只会唤回已有窗口。

## 隐私与安全

程序通过本机 Codex `app-server` 的只读接口 `account/rateLimits/read` 获取额度。它不会保存或打包账号令牌，不会发送聊天消息，也不会自动消耗模型额度。

本压缩包中没有个人缓存文件、登录凭据、API Key 或本机绝对路径。`config.json` 中的 `codexPath` 默认为空，程序会自动查找本机 Codex。

## 系统要求

- Windows 10 或 Windows 11，64 位。
- 已安装并登录 Codex 桌面版或 Codex CLI。
- Windows PowerShell 5.1。
- Microsoft .NET Framework 4.x（Windows 10/11 通常已自带）。

## 快速安装

1. 解压整个 ZIP，不能只单独运行 EXE。
2. 双击 `install.cmd`。
3. 安装完成后切回 ChatGPT/Codex，浮窗会自动出现。

详细步骤、故障排查和卸载方法见 [安装指南.md](安装指南.md)。

## 项目结构

```text
Codex额度浮窗-粉色版-20261013/
├─ README.md
├─ 安装指南.md
├─ install.cmd / install.ps1
├─ uninstall.cmd / uninstall.ps1
├─ open-widget.ps1 / status.ps1
├─ quota-widget/
│  └─ QuotaWidget.exe
├─ quota-timer/
│  ├─ quota-timer.ps1
│  └─ config.json
├─ source/
│  ├─ QuotaWidget.cs
│  ├─ widget.manifest
│  └─ build.ps1
└─ preview/
   └─ 粉色界面预览.png
```

## 源码说明

界面主体使用 C# Windows Forms 编写；额度探测脚本使用 PowerShell。若要重新编译，请在 PowerShell 中运行：

```powershell
.\source\build.ps1
```

编译完成的程序会写入 `quota-widget\QuotaWidget.exe`，随后自动运行内置自检。

