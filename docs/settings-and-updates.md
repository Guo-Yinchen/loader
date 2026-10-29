# 配置与主程序更新

新版源码与本地验证构建使用下面的规则，正式发行包需两端一起更新后交付。本轮没有修改线上指针、服务端数据或已发布 ZIP。

## 配置位置

Wardogs 使用当前 Windows 账户的 `%LOCALAPPDATA%\Chispa\wardogs`，在本机通常为 `C:\Users\allen\AppData\Local\Chispa\wardogs`，不写死用户名。

- `Wardogs.ini`：默认配置。
- `Wardogs-<名称>.ini`：命名配置。
- `Wardogs.profile`：当前所选配置。
- `layout.ini`：界面布局；不会出现在配置列表中。
- `.storage-v1`：首次迁移完成标记，防止以后删除的配置被旧安装重新导入。

Development 和 Production 使用同一账户的同一目录。免授权与探针开关仍由编译类型决定，不从配置文件开启。

首次迁移按当前使用的版本优先，导入尚未存在的配置和布局，已有用户文件优先。loader 会在删除旧目录之前完成迁移。新主程序也能从旧 loader 的版本目录导入；便携运行时首先检查自身旁边的旧配置。迁移失败会停止更新，不先删旧程序。旧 loader 自身仍会保留版本目录，清理旧程序的功能需要新版 loader。

`CHISPA_CONFIG_DIRECTORY` 是测试隔离/明确指定存储路径的覆盖项；日常运行无需设置。测试必须使用独立目录，不写真实用户配置。

## 更新顺序

1. 验证发布清单签名，下载并检查包的 SHA-256。
2. 解压到临时目录，验证包路径及入口文件；当前程序此时保留。
3. 确认主程序没有运行，完成配置迁移。
4. 安装新目录并原子更新当前版本指针。重新安装同版本时只暂存被替换的目录；切换失败立即恢复，上次中断的暂存目录可恢复后重试。
5. 成功后仅保留当前主程序版本，删除旧数字版本目录、临时替换目录及旧 loader 创建的版本备份。不保留长期程序回退包；配置仍在独立目录。

清理限制在当前程序自己的安装目录内；包含链接的目录拒绝清理，其他程序及未知目录保留。清理失败会明确报告“新版本已安装，但旧程序目录清理失败”，不会悄悄忽略。

此处不改变 loader 自身 EXE 的更新方式，也不清理服务端发布历史或开发工程缓存。

## 本地验证

主程序运行 `test.ps1` 与 `build.ps1 -Flavor Development`。loader 构建 `tests/Chispa.Smoke.csproj` 后运行 `Chispa.Smoke.dll --install-storage-check`，覆盖迁移、升级/修复清理、指针失败回滚、中断恢复、其他程序隔离和运行进程保护；`tests/smoke.py` 验证签名、下载、安装和启动校验。

主程序固定产物为 `../wardogs chispa/bin/x64/Development/Release/Wardogs.Dev.exe`；loader 验证产物为 `.local/maintenance-check/client/Chispa.Launcher.exe`。两仓验证记录分别覆盖 `.local/maintenance-check/latest.md`，不创建每轮工程副本。
