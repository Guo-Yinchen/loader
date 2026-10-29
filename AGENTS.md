# 项目维护约定

本仓库维护 Chispa 加载器、授权服务、打包与签名工具。主程序在相邻 `../wardogs chispa/`。

每次新话题涉及主程序、跨仓构建、测试、清理或发布时，先阅读 [主程序维护流程](../wardogs%20chispa/docs/MAINTENANCE-WORKFLOW.md) 与主程序 `AGENTS.md`。仅处理本仓库时同样执行下列约定；若相邻仓库不存在，按这些约定继续本仓库任务，不凭空创建主程序目录。

- 开始先检查当前分支、Git 状态及相关差异，保护用户和其他话题的已有修改。纯问答或文档修改不为走流程额外构建。
- 原地维护源码，历史通过 Git 管理，不新增工程备份、`*-bak` 或每轮一套永久 build。
- 新建专项验证产物优先使用固定 `.local/maintenance-check/`。测试必须隔离账号数据与端口；临时目录在 `finally` 清理，重复成功报告覆盖，保留尚未解决的失败证据。
- 服务构建有运行占用和部署指针，不能套用主程序的直接覆盖规则。一般验证把输出定向到 `.local/maintenance-check/server/` 等独立固定位置；不要为检查编译而运行会修改 `.local/current-server-build.txt` 的根 `build.ps1`。
- 主程序日常用 `build.ps1 -Flavor Development`，免授权且开放探针，固定输出 `bin/x64/Development/Release/Wardogs.Dev.exe`；交付用 `-Flavor Production -Configuration Release`，固定输出 `bin/x64/Wardogs/Wardogs.exe`。Flavor 和 Debug/Release 编译配置的输出、缓存已经隔离。正式发布必须通过成品 `--build-info` 核验，开发版即使重命名也须拒绝；不向正式 `dist` 写入测试产物。
- `dist/client/`、`dist/server/`、`dist/software/` 为当前交付入口。已发布的包、签名和哈希不可变；离线发行副本原则上只留当前和上一稳定版，更早版本确认可恢复、无运行依赖后清理。
- 清理先预览、核实绝对路径/白名单/链接/Git 跟踪状态及进程占用，只删除本任务已结束且可重建的产物。`tools/clean-workspace.ps1` 会清理缓存和部分历史副本，不能每次新话题盲目执行 `-Apply`，也不能假设其自动保留所有回退构建。
- 保护 `.local/server/`、`.local/signing/`、在用及回退所需服务构建、个人配置、SDK、正式原包及调查证据。服务端在线历史版本不按离线副本数量直接删除。
- 交付说明改动、验证、唯一产物位置和临时目录清理/保留情况。只在任务已有授权范围内提交、推送、切换服务或发布，普通维护不自动上线。

目录与现有命令见 [工作区与发布说明](docs/workspace.md)。维护约定由每次任务执行者落实，不代表已经安装后台清理或自动轮换任务。
