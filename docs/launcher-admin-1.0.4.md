# 加载器 1.0.4：默认以管理员身份启动

加载器使用 Windows 原生应用清单，`requestedExecutionLevel` 设为 `requireAdministrator`，`uiAccess` 为 `false`。双击 EXE 时 Windows 自动请求管理员权限；用户确认 UAC 后启动，无需通过右键菜单选择“以管理员身份运行”。

成品：`.local/distribution/client-1.0.4/Chispa.Launcher.exe`。

正式服务器地址保持不变。已检查成品 PE 资源中的管理员启动清单及单文件包内部程序集的服务器地址，错误地址的负向检查通过。普通权限的打包进程运行成品自检时会收到 Windows 错误 740，因此打包脚本增加只读的成品检查方式，不启动或提升 GUI，也不修改 UAC 设置。

SHA-256：`ae4ec9d929ebf02ca6d9de9c73323244e072f2c355f046c766a002168b6d4074`。

本次不需要更新服务端。系统 UAC 确认由使用者完成。
