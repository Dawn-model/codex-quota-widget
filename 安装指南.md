# Codex 额度侧边卡片（粉色版）

Windows 小工具：在 Codex 桌面窗口侧边显示用量、重置时间；短按卡片展开详情，按住左键拖动可调整并保存位置；默认放在原生用量图标右侧。浅色为淡粉色，深色为深粉色。仅供 Windows 使用，客户端更新、系统缩放及主题可能影响定位。

## 安装

1. 安装并登录 Codex 桌面应用及 Codex CLI，打开桌面应用。
2. 从仓库选择 **Code → Download ZIP**，完整解压，双击 `install.cmd`。无需输入或提供 token。
3. 切回 Codex 窗口，点击侧边卡片展开详情。若没有显示，可双击桌面的“打开额度状态栏”。右键卡片可打开菜单。

安装目录为 `%LOCALAPPDATA%\CodexQuotaTools`。升级时备份原显示程序和启动快捷方式到该目录下 `backups`，保留已有查询脚本、配置及状态；首次安装才复制内置只读查询脚本。不会建立计时触发器，也不会修改 GPT 客户端。若已有非标准路径或不兼容的查询脚本，需要手动检查，勿覆盖原有计时程序。

## 数据与隐私

额度约每 5 秒通过本机 Codex CLI 的 `account/rateLimits/read` 只读查询。订阅权益到期时间约每 5 分钟通过 `https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27` 查询，并按系统本地时区显示。订阅可能自动续费，权益到期时间不等同于取消日期。重置卡数量和到期时间通过 `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits` 查询，使用本机已登录账户凭据，不兑换或消费重置卡。接口、账户类型和客户端版本可能变化；查询失败时显示数据不可用。程序不包含账户凭据，请勿上传 `auth.json`、个人配置、缓存或日志。

支持显示 Plus 等账户返回的套餐；Pro 布局尚未在 Pro 账户上验证。重置时间目前按中国标准时间显示。界面位置、拖动、遮挡和点击行为尚未在所有客户端版本和缩放下验证；Pro 账户尚未实测。

## 卸载和恢复

双击 `uninstall.cmd` 停止并移除卡片及其快捷方式；保留查询脚本、配置、状态和备份。升级前的程序在 `backups` 中，将对应的 `QuotaWidget.exe` 复制回 `quota-widget`，必要时恢复其中的启动快捷方式，再启动即可。卸载不会清理额外创建的计时任务。

## 源码编译

Windows PowerShell 执行 `./source/build.ps1`，需要 .NET Framework 4.x 编译器和 UI Automation 程序集。发布的 EXE 可直接使用，无须自行编译。`status.ps1` 查看安装路径和最新额度刷新状态。
