# 任务 06–08 交付说明：后台加固、版本状态、下载恢复与限流

> 2026-10-26 验收修订：本文件保留初次交付记录，当前修复、测试范围和上线待办见 [验收简报](acceptance-review-2026-10-26.md)。

日期：2026-10-26。改动在 `D:\desktoop\loader`。

## 任务 06：保留查看密码，加固后台

**没删功能，也没把密文改成明文**：`users.json` 里仍然是 PBKDF2 哈希 + Data Protection 加密的 `passwordCiphertext`。

- 新增 `server/AdminSecurity.cs`（`AdminGuard`）：服务端二次验证 + 短时授权 + 限流 + 审计。
- 查看路径变成两步：`POST /admin/users/{name}/password/stepup`（输入管理员密码，服务端校验）→ 短时 grant（默认 120 秒、最多 3 次、绑定管理员当前登录 Cookie 与目标用户，原子扣减次数）→ `POST /admin/users/{name}/password/view`（带 grant）。缺 grant / 过期 / 换人一律 403，且不回显具体原因以免被探测。
- 限流：二次验证失败 5 次/15 分钟（按管理员+来源地址）→ 429；查看 20 次/10 分钟 → 429。
- 审计：`<dataRoot>/admin-audit.log`，JSON 行，只记事件/管理员/目标账号/结果/来源地址；不记密码、密文、令牌、响应正文。管理员登录成败也记。
- 敏感页：显式 `Cache-Control: no-store`，页面上 30 秒倒计时自动清空输入框，`pagehide`/`beforeunload` 也清空；页面写明"截图/录屏无法技术性收回"。
- 可配置后台限制：`CHISPA_ADMIN_ALLOWED_NETWORKS`（IP/CIDR，生效于转发头处理之后=真实客户端地址）；**loopback 永远允许**，所以配错也不会把自己锁在门外。恢复办法：本机 `admin-reset`、或停服务后用备份恢复（任务 05）。
- 密钥访问控制：密钥环 `keys/` 是 DPAPI 保护的，只有该机器该 Windows 账号能解；口令密文同理。备份目录默认收紧 ACL。

验收（`python tests/admin_matrix.py`，23/23）：未登录 ✗、缺二次验证 ✗、过期 grant ✗、CSRF 错 ✗、有效管理员 ✓、审计无秘密 ✓、连续失败 429 ✓、白名单外 403 / 本机仍可进 ✓。

## 任务 07：推荐版本、撤回与禁止启动

- `ProgramInfo.RecommendedVersion`、`Release.Status`（`active`/`withdrawn`/`banned`，旧清单缺字段即 `active`）。
- `Latest()` = 推荐版本（若仍可下载）否则最高 active；推荐指向被撤回/禁止/不存在的版本时**自动回落**，不会把程序变成不可用。
- **撤回 ≠ 禁止**：撤回只停止下载（`package` 返回 404 `release_withdrawn`），已安装的仍可启动；禁止下载和启动都停（`/launch`、`launch-ticket`、`redeem`、`heartbeat` 全部 403 `release_banned`，运行中的程序在下一个心跳退出）。
- 后台：程序面板里每个版本显示状态与"推荐/撤回/禁止/恢复"按钮，推荐版本可下拉切换；两个动作都写审计。
- 加载器升级提示：`GET /api/v1/launcher/latest` 返回 `version/minimumVersion/downloadUrl/notes/capabilities`，由 `CHISPA_LAUNCHER_VERSION` 等环境变量配置。下载地址只接受 https（本机回环 http 例外），非法值服务端清空、客户端再校验一次；登录页只显示文字 + 用户点击才打开浏览器，**不下载、不执行**。
- 安全边界：能力用 `capabilities` 标识（`launch-auth/1` 等），版本号只用于显示——版本字符串不作安全依据。禁用状态只约束走服务端的启动与下载；未完成主程序内部授权前，不能声称能阻止直接双击旧 EXE（这一点仍成立）。

验收（`python tests/version_matrix.py`，23/23）：切回旧推荐 ✓、撤回后不可下载但可启动 ✓、禁止后各接口一致拒绝 ✓（含票据签发后被禁、运行中被禁）、其他程序不受影响 ✓、非法下载地址被清空 ✓。

## 任务 08：下载恢复、限流与故障回归

**客户端**（`client/LauncherApi.cs`、`client/Installer.cs`）：
- 下载有界重试（3 次，指数退避），断点续传：保留 `.partial`，按版本和包哈希跨进程保留断点，用 `Range` 续传；服务器回 206 且 Content-Range 完全匹配才追加，回 416 或忽略 Range 时从零重来；完成后仍按清单大小与签名 SHA-256 校验。
- 认证失败/权限撤销/签名不符/禁止版本不重试（`IsRetryable` 只认网络与 IO 异常）。
- 安装前检查磁盘空间（按包大小 ×2 + 64 MB 余量），不足直接给出可读提示。
- 安装互斥：`<program>/.install.lock` 独占，第二个实例得到"正在由另一个安装任务处理"；清理崩溃遗留的 `.stage-*`（仅 1 小时前的），旧版本目录与 `backups/` 保留不动。
- 下载失败文案区分中断（保留断点）、哈希不符（停止安装、文件未改动）、磁盘不足、服务器不可用。

**服务端**：
- 包下载开启 Range（`enableRangeProcessing`）。
- `server/RequestLimits.cs`：登录/普通 API/上传三档，按来源地址每分钟限流；登录与上传请求体分别限 8 KB / 64 MB（超大直接 413）；上传并发按地址上限。
- 维护定时器（每 2 分钟）：清理过期会话、过期登录失败记录、限流桶，`PruneCaches` 给启动清单缓存封顶 128。
- 启动签名校验的哈希工作移出全局锁（原来 90 MB 的哈希会阻塞所有请求）；`/launch` 遇到磁盘上的包与清单不符返回 `package_corrupt`。

验收（`python tests/fault_matrix.py`，11/11）：断点续传完成且哈希一致 ✓、服务端 206/416 ✓、超大登录体 413 ✓、登录限流 429 ✓、API 限流 429 ✓、包损坏时启动被拒 ✓、权限撤销后旧会话立即失效 ✓、包缺失 404 ✓。
测试规模与耗时：本轮 11 项，含 4 个独立临时实例，约 40 秒；环境为 Windows + 本机回环 + 独立数据目录，**不对公网服务施压，也不给并发容量承诺**。

未实现/已知限制：没有整包多连接下载；会话清理靠定时器（最长 2 分钟延迟）；磁盘不足只在客户端安装前检查，服务端写盘失败仍由通用错误返回。

## 改动文件

新增：`server/AdminSecurity.cs`、`server/RequestLimits.cs`、`tests/admin_matrix.py`、`tests/version_matrix.py`、`tests/fault_matrix.py`、本文件。
修改：`server/AdminPages.cs`（step-up/查看/审计/网络限制/版本面板/推荐与状态接口/修剪）、`server/Program.cs`（推荐与状态、启动/下载一致性、Range、限流中间件、维护定时器、加载器元数据、哈希移出锁）、`server/LaunchAuth.cs`（禁止版本在兑换与心跳中拒绝）、`server/Ui/app.js`（敏感页自动清除）、`client/LauncherApi.cs`（可续传下载、加载器元数据、URL 校验）、`client/Installer.cs`（安装锁、遗留清理、明确错误）、`client/LauncherForm.cs`（升级提示）、`client/Chispa.Launcher.csproj`（版本号）、`tests/smoke.py`（走两步查看）、`tests/Program.cs`（`--download-check`）、`tools/restore-backup.ps1`（恢复验证走两步）、`README.md`。

## 部署顺序与回退

1. `./build.ps1`（服务端 + 加载器 + 签名工具），`./publish-current.ps1 -Version X` 分发主程序。
2. 停服务 → 换服务端 → 起服务；后台新增"版本状态"面板立刻可用，旧清单缺 `status` 按 active 处理，无需迁移。
3. 加载器升级提示需要设置 `CHISPA_LAUNCHER_VERSION`（及可选 `CHISPA_LAUNCHER_DOWNLOAD_URL` 等），不设则接口 404、登录页不显示提示。
4. 回退：换回旧服务端即可；`status`/`recommendedVersion` 字段会被旧版本忽略（JSON 反序列化不认多余字段仍可读），不会损坏数据。
5. 后台网络限制默认关闭；开启前先确认来源网段，loopback 始终可用。

## 遗留

- 二次验证的门槛仍是管理员口令；没有接 TOTP/WebAuthn（可后续加在 `AdminGuard` 里，接口不变）。
- 审计日志只追加与按 8 MB 轮转，未做异地汇总与防篡改。
- 加载器升级仍是手动下载，没有自更新；提示只覆盖登录页。
- 禁用版本只约束服务端路径；直接双击已安装的旧 EXE 无法被服务端阻止（与任务 03 的签名边界一致）。
