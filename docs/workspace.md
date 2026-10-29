# 工作区与发布入口

新话题先读仓库根目录的 [AGENTS.md](../AGENTS.md)。主程序按相邻仓库的 [维护流程](../../wardogs%20chispa/docs/MAINTENANCE-WORKFLOW.md) 执行，包含开始检查、固定构建、验证清理、版本保留及交付记录。

## 当前成品

1.0.5 的分程序激活累加、Beta 通行证与旧版本删除规则见 [授权与版本维护](entitlements-and-version-deletion.md)。

未发布的配置迁移与旧主程序清理规则见 [配置与主程序更新](settings-and-updates.md)。loader 本地验证版固定在 `.local/maintenance-check/client/Chispa.Launcher.exe`，不覆盖下表中的已发布成品。

| 组件 | 版本 | 本地位置 | GitHub |
|---|---|---|---|
| 客户端 | 1.0.5 | `dist/client/Chispa.Launcher.exe` | [loader v1.0.5](https://github.com/Guo-Yinchen/loader/releases/tag/v1.0.5) |
| 独立服务端 | 1.0.5 | `dist/server/Chispa.Server-1.0.5-win-x64.zip` | 同上，含运行时及启动说明 |
| 软件 | 1.2.4 | `dist/software/Wardogs-1.2.4.zip` | [Wardogs v1.2.4](https://github.com/Guo-Yinchen/Wardogs/releases/tag/v1.2.4) |

源码分别提交到 loader 与 Wardogs。成品不提交进 Git，发布到 Releases，附 SHA256SUMS；软件包附原始签名，不重新压缩或覆盖原版本。

## 目录分工

- `client/`、`server/`、`shared/`：加载器及服务端当前源码。
- `dist/`：唯一的当前成品入口，Git 忽略。
- `.local/server/`：实际账号、授权、密钥环及已发布软件，不能当作编译缓存清理。
- `.local/server-builds/`：服务构建，清理时保留构建指针及运行中进程所用目录。
- `.local/signing/`：签名私钥，本地保留，不上传。
- `.tooling/`：本机 SDK、运行时及依赖缓存，保留用于后续开发。
- `docs/screenshots/`：当前界面预览；旧的本机截图和测试输出为可清理产物。
- 软件工程的历史源码副本集中到 `docs/archive/source-snapshots/`，开发使用当前 `Usermode/`。

## 常用命令

```powershell
./build.ps1
./publish-client.ps1                     # 默认 1.0.5，输出 dist/client
./publish-server.ps1                     # 自带运行时，输出 dist/server
./tools/clean-workspace.ps1               # 只预览
./tools/clean-workspace.ps1 -Apply        # 清理白名单内的旧构建与缓存
```

清理脚本只处理白名单内的编译产物。实际数据、签名私钥、当前服务构建、SDK 和 dist 不在清理目标内。被运行中的客户端占用的文件会跳过；退出旧进程后可再次清理。

2026-10-27 整理释放约 **1,275.9 MiB**；正在使用的旧路径客户端约 49.4 MiB 保留。随后构建产生的少量缓存也可用同一脚本清理。历史数据目录中的已发布版本是服务运行资料，不直接删除。

GitHub Release 发布工具：`tools/publish-github-release.py`。通过 Git Credential Manager 读取凭据，先建立草稿，逐个上传并比对 GitHub 返回的 SHA-256，完成后才公开；不覆盖已有资产。
