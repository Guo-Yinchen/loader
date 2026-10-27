# 任务 04 交付说明：服务与隧道恢复，以及代理安全配置

> 2026-10-26 验收修订：本文件保留初次交付记录，当前修复、测试范围和上线待办见 [验收简报](acceptance-review-2026-10-26.md)。

日期：2026-10-26。改动在 `D:\desktoop\loader`（服务端、配置、脚本、测试）。隧道配置本人未改动。

## 0. 结论摘要

1. **只读勘察结论**：公网入口是 **SakuraFrp**（Windows 服务 `SakuraFrpService`，`StartMode=Auto`），隧道 `[chispa, tcp]` 的映射目标是 `tcp(auto-https)://127.0.0.1:5115`。**TLS 在本地 frpc 进程终止**：公网 `:24154` 呈现的是 `CN=*.rm7lwjwtg.nyat.app`（ZeroSSL DV，TLS 1.3，有效期至 2027-01-24），frpc 再用**明文 HTTP** 转发到 `127.0.0.1:5115`。所以服务端看到的来源永远是 127.0.0.1，真实客户端只在 `X-Forwarded-*` 里。
2. **实测到一个真实风险**：frpc 会给 `X-Forwarded-For` **追加**真实 IP、并把 `X-Forwarded-Proto` 覆盖成 `https`，但**完全不清理**客户端自带的 `X-Forwarded-For`、`X-Real-IP`、`Forwarded`。实测一个带着 `X-Forwarded-For: 6.6.6.6` 的公网请求到达服务端时是 `X-Forwarded-For: 6.6.6.6, 183.195.64.167`。任何"取最左侧"或"信 X-Real-IP"的实现都等于让客户端自选来源 IP。
3. **已改**：只信任 `CHISPA_TRUSTED_PROXIES` 里列出的对端（默认不信任任何转发头），`ForwardLimit=1` 只取代理追加的那一条；生产模式下管理/反 CSRF Cookie 一律 `Secure`（保留 HttpOnly、SameSite=Strict、CSRF），并且生产模式取消 loopback 明文豁免（`/health`、`/ready` 除外）；新增不含内部信息的 `/ready`；日志按天+大小轮转；关闭 `Server` 响应头。
4. **恢复**：隧道侧已有自动启动（服务 Auto）与自动重连（日志实测），无需重复安装；**服务侧补齐**了 `tools/install-service.ps1`（计划任务 + 看门狗 + 停止标志）、`tools/uninstall-service.ps1`、`tools/health-check.ps1`。实测发现 Task Scheduler 自带的"失败后重启"对本机被强杀的进程**不生效**（`lastResult=0xFFFFFFFF`、状态停在 `Ready`），因此崩溃恢复由看门狗负责——隔离环境实测：**杀掉服务进程后 2 秒内被重新拉起**。
5. 验收：`tests/proxy_matrix.py` **52/52**；`tests/recovery_check.ps1` **11/11**；既有 `smoke.py`、`signature_matrix.py`（25/25）、`auth_matrix.py`（25/25）、`launcher_startup.py`、`publish_checks.py` 全部通过。

## 1. 只读勘察：现有部署是什么

| 项 | 实测结果 | 证据 |
|---|---|---|
| 隧道服务 | `SakuraFrpService`，`State=Running`，`StartMode=Auto`，`PathName=C:\Program Files\SakuraFrpLauncher\SakuraFrpService.exe` | `Get-CimInstance Win32_Service` |
| 隧道进程 | `frpc.exe`（PID 63684），父进程即该服务 | `Get-CimInstance Win32_Process` |
| 隧道定义 | `[chispa, tcp]`，映射目标 `tcp(auto-https)://127.0.0.1:5115`，自动启动隧道 id `29671325` | `C:\ProgramData\SakuraFrpService\config.json`（`auto_start_tunnels`）、`Logs\SakuraFrpService.20261026.log` |
| TLS 终止位置 | **本机 frpc**。公网证书 `CN=*.rm7lwjwtg.nyat.app`，签发者 `ZeroSSL RSA DV SSL CA 2`，TLS 1.3，有效期 2026-10-26 → 2027-01-24 | 自查 `ssl.create_default_context()` 握手；frpc 日志"已从服务器为 teamviewer.rm7lwjwtg.nyat.app 加载证书" |
| 后端连接方式 | 明文 HTTP → `127.0.0.1:5115`，来源恒为 127.0.0.1 | 见下面的转发头实测 |
| 服务是否在运行 | **当时未运行**：5115 无监听；frpc 反复报 `dial tcp 127.0.0.1:5115: connectex: ... actively refused`，公网 `/health` 返回 502 | `Get-NetTCPConnection`、frpc 日志 10:09–11:32 |

`FrpcWorkingDirectory` 里那份 `teamviewer.rm7lwjwtg.nyat.app.crt` 是 **CN=SakuraFrp Automatic TLS 的自签证书**（frpc 的兜底/备用），公网实际呈现的是从服务端加载的 ZeroSSL 通配证书。记录这一点，免得上机的人看到自签文件误判成证书事故。

### 1.1 转发头实测（这是本轮最关键的证据）

在 5115 上临时起一个应答器（服务本来就没运行，窗口 25 秒），然后从公网地址发起两次请求，一次干净、一次带伪造头：

```
干净请求   → X-Forwarded-For: 183.195.64.167
             X-Forwarded-Proto: https
伪造请求   → Forwarded: for=6.6.6.6;proto=http          ← 原样透传（不可信）
             X-Real-Ip: 7.7.7.7                          ← 原样透传（不可信）
             X-Forwarded-For: 6.6.6.6, 183.195.64.167    ← 追加：最右才是真实
             X-Forwarded-Proto: https                    ← 覆盖（可信）
```

由此得出三条必须处理的问题：

1. 现有代码没有任何转发头处理 → `metrics` 与登录限流看到的都是 `127.0.0.1`：**限流实际是全局的**（一个人试错 5 次，所有人 15 分钟内登录被拒），"最近一次客户端连接"诊断也永远是 127.0.0.1。
2. `Request.IsHttps` 恒为 false（明文到达）→ `SameAsRequest` → **管理 Cookie 没有 Secure 标记**。公网虽然走 TLS，但标记缺失意味着浏览器会在任何 http 连接上把它带上。
3. 一旦有人"顺手"开始信任转发头，按常见写法（取最左 / 信 `X-Real-IP` / 信 RFC 7239 `Forwarded`）就会**被客户端伪造**。

## 2. 改动

### 2.1 只信任已配置的代理（`server/ProxyTrust.cs`）

- `CHISPA_TRUSTED_PROXIES` 接受 IP 或 CIDR，逗号/分号/空格分隔；**未设置 = 完全不信任任何转发头**（框架默认信任 loopback，这里被显式清空）。
- 启用时：`ForwardedHeaders = XForwardedFor | XForwardedProto`，`ForwardLimit = 1`，`KnownProxies`/`KnownIPNetworks` 只放配置项。
- `ForwardLimit=1` 是关键：代理追加的那条在最右，客户端写不了它；左侧的伪造条目被丢弃。
- 启动日志打印一行 `deployment: production=… trusted proxies: …`，部署后一眼能看出实际生效的配置。

现网应对应 `CHISPA_TRUSTED_PROXIES=127.0.0.1`（frpc 从 loopback 连入）。

### 2.2 HTTPS 闸门（`server/Program.cs`）

- 非 HTTPS 且非受信代理声明的 HTTPS → 403 `HTTPS required`（原有行为保留，但现在 `IsHttps` 能正确反映 `X-Forwarded-Proto`）。
- `/health`、`/ready` 始终豁免（本机看门狗需要明文轮询；两者都不含内部信息）。
- **生产模式（`CHISPA_PRODUCTION=1`）取消 loopback 明文豁免**：本机管理员也必须走公网 HTTPS 访问后台，不允许明文后台。

### 2.3 Cookie

| 模式 | 管理 Cookie / 反 CSRF Cookie |
|---|---|
| `CHISPA_PRODUCTION=1` | `Secure=Always`，`HttpOnly`，`SameSite=Strict`（CSRF 校验不变） |
| 本机开发（未设该变量） | `Secure=SameAsRequest`（`http://127.0.0.1:5115/admin` 仍可用） |

生产模式是显式开关而不是从绑定地址推断的：隧道后面这个服务仍然监听 127.0.0.1，"loopback" 并不能说明谁能访问它。

### 2.4 就绪检查与日志

- `GET /ready`：健康返回 `200 {"status":"ready"}`，不健康返回 `503 {"status":"not-ready"}`，**只有 status 字段**。检查内容：数据目录存在且可写、`users.json` 可解析；结果缓存 10 秒（`CHISPA_READINESS_CACHE_SECONDS`，设为 0 可关缓存），原因只写日志。
- `GET /health` 保持原样（存活探针）。
- 响应不再带 `Server: Kestrel`（`AddServerHeader=false`）。
- 日志轮转：`server/FileLog.cs`，`CHISPA_LOG_DIRECTORY` 启用，按天 `chispa-YYYYMMDD.log`，超过 32 MB 滚到 `.001/.002…`，保留 `CHISPA_LOG_KEEP_DAYS`（默认 14）天。ASP.NET 请求日志压到 Warning，避免每个下载分块写一行。

### 2.5 开机自启、崩溃恢复、隧道重连

| 组件 | 现状 | 处理 |
|---|---|---|
| 隧道进程 | 已由 SakuraFrp 服务 `Auto` 启动，且自带重连（日志实测 09:28:25 "网络波动…正在重试 → 重试成功"） | **不重复安装**，只记录证据 |
| 隧道后端不可达 | frpc 会持续重试连接 127.0.0.1:5115（日志 10:09–11:32 反复重试） | 只要服务在，frpc 自动连上 |
| Chispa 服务 | 原先没有任何自启/守护 | 新增安装/卸载脚本 + 看门狗 |

`tools/install-service.ps1`：
- 生成 `.local/service/start-chispa-server.ps1`（环境变量 + `dotnet <当前构建>.dll`），`-DryRun` 只打印不写盘；
- 注册计划任务 `Chispa.Server`（默认 `AtLogOn`；无人登录启动需显式 `-Trigger AtStartup -Credential (Get-Credential)`，凭据必须属于现有 DPAPI 密钥的 Windows 身份），动作是 `powershell -File tools/run-server-watchdog.ps1 -Launcher <生成的脚本> -StopFlag … -LogFile …`；
- 运行身份是**当前账号**（`Interactive`）：Data Protection 密钥用 DPAPI 保护，换身份就解不开；
- 任务本身还带 `RestartCount/RestartInterval`，作为看门狗本身挂掉时的第二层。

`tools/run-server-watchdog.ps1`：循环启动服务、记录退出码与运行时长、5 秒后重启；停止标志文件阻止继续重启；卸载随后停止任务，可能中断正在处理的请求，不保证优雅停机。

> **为什么不用 Task Scheduler 的"失败后重启"当主机制**：实测不生效。把服务进程强杀后，任务状态回到 `Ready`，`LastTaskResult=0xFFFFFFFF`，`RestartOnFailure`（`Count=999, Interval=PT1M`）**没有触发**，两分钟里没有重启。这是本轮实测结论，不是推测。看门狗绕开了这个行为。

`tools/uninstall-service.ps1`：写停止标志 → 等 3 秒 → 停任务 → 注销 → 清理标志与生成的启动脚本；数据与日志不动。

`tools/health-check.ps1`：轮询 `/ready`（可再指定 `-PublicUrl`），失败重试；**告警目的地是配置项**（`-AlertWebhook` / `-AlertFile`），不配置就只打印"未配置告警目的地，不发送任何消息"。告警负载只有 `{service,status,target,detail,at}`，`target` 只到 host 与 path，不含内部路径。

## 3. 配置项

| 变量 | 默认 | 作用 |
|---|---|---|
| `CHISPA_TRUSTED_PROXIES` | 空（不信任任何转发头） | 允许设置 `X-Forwarded-For/Proto` 的对端，IP 或 CIDR |
| `CHISPA_PRODUCTION` | 未设置 | `1` 时：Cookie 强制 `Secure`、取消 loopback 明文豁免 |
| `CHISPA_LOG_DIRECTORY` | 空（仅控制台） | 启用轮转文件日志 |
| `CHISPA_LOG_LEVEL` | `Information` | 全局最低级别 |
| `CHISPA_LOG_KEEP_DAYS` | `14` | 日志保留天数 |
| `CHISPA_READINESS_CACHE_SECONDS` | `10` | `/ready` 结果缓存窗口，`0` = 每次现算 |
| `CHISPA_SERVER_URL` | `http://127.0.0.1:5115` | 监听地址 |
| `CHISPA_SERVER_DATA` | `.local/server` | 数据目录 |
| `CHISPA_RELEASE_TRUST_KEYS` | `<data>/release-trust.json` | 发布签名校验公钥（任务 03） |

## 4. 验收

```powershell
./build.ps1
dotnet build tests/Chispa.Smoke.csproj -c Release
python tests/proxy_matrix.py          # 52/52
python tests/signature_matrix.py      # 25/25（回归）
python tests/auth_matrix.py           # 25/25（回归）
python tests/smoke.py                 # 11 项 PASS（回归）
powershell -File tests/recovery_check.ps1   # 11/11
python tests/launcher_startup.py      # PASS
python tests/publish_checks.py        # ALL PASS
```

`tests/proxy_matrix.py` 覆盖（每一项都对着上文实测出来的攻击面）：

| 检查 | 结果 |
|---|---|
| 未列入白名单的对端伪造 `X-Forwarded-Proto: https` / `X-Forwarded-For` / `X-Real-IP` / `Forwarded` | 仍然 403，不能改变信任判断（有白名单但地址不符、以及完全没有白名单，两种都测） |
| 受信代理声明 HTTPS | 200；声明 `http` 且转发真实公网地址 | 403 |
| 受信代理转发 `X-Forwarded-For: 203.0.113.7` | `/admin/metrics` 里记录的客户端地址就是 203.0.113.7 |
| 客户端自带 `X-Forwarded-For: 6.6.6.6, 198.51.100.23` | 记录的是 198.51.100.23（取代理追加的那条） |
| `X-Real-IP` / `Forwarded` 伪造 | 完全不影响结果 |
| 同一地址 5 次失败 → 第 6 次 429；换一个地址不受影响 | 限流按**有效来源**分桶 |
| 生产模式：loopback 明文访问 `/admin` | 403；`/ready` 仍 200；`X-Forwarded-Proto: https` 时 200 |
| 生产模式 Cookie | `secure; samesite=strict; httponly`（管理 Cookie 与反 CSRF Cookie 都验证） |
| 开发模式 Cookie | 不带 `secure`，其余不变；无 CSRF 的管理写操作仍 400 |
| `/ready` | 200/503、body 只有 `status`、不含路径/版本；`users.json` 损坏 → 503 → 修复后恢复 |
| 响应头 | 无 `Server` |
| `health-check.ps1` | 健康返回 0 且不发送；服务挂掉返回非 0 并说明"未配置告警目的地，不发送" |

`tests/recovery_check.ps1` 实测记录（隔离环境，临时任务名 + 临时数据目录 + 随机端口）：

```
PASS 安装脚本注册了任务
PASS 注册的定义能无人值守启动服务  (用时 1 秒)
PASS 服务进程确实由该任务拉起  (PID 19420)
PASS 结束进程后服务确实下线
PASS 监视进程自动把服务重新拉起  (用时 2 秒，新 PID 6864)
PASS 监视日志记录了这次崩溃与重启
PASS 卸载脚本注销了任务
PASS 卸载后服务已停止
PASS 重新安装并启动后服务恢复  (用时 1 秒)
PASS 运行的实例写出了滚动日志文件  (chispa-20261026.log)
PASS 日志记录了部署配置（代理信任与生产开关）
```

结果同时写入 `.local/qa-recovery-last.json`。

## 5. 正式环境部署步骤（待人工执行）

本轮**没有**改动正在使用的隧道，也没有启动正式服务（当时它本来就没运行）。正式切换按下面执行：

1. 确认数据库与密钥已备份（`.local/server`）。
2. 部署新服务端构建，然后用正式参数安装：
   ```powershell
   ./build.ps1
   ./tools/install-service.ps1 -Production -TrustedProxies 127.0.0.1 -DryRun   # 先看打印内容
   ./tools/install-service.ps1 -Production -TrustedProxies 127.0.0.1           # 登录后运行；无人登录启动另加 -Trigger AtStartup -Credential (Get-Credential)
   Start-ScheduledTask -TaskName Chispa.Server
   ```
3. 本机验证：`Invoke-WebRequest http://127.0.0.1:5115/ready` 返回 200；`Get-Content .local/server/logs/watchdog.log` 有 "watchdog started"。
4. 公网验证（在外部网络）：`https://teamviewer.rm7lwjwtg.nyat.app:24154/health` → 200；`/ready` → 200；后台登录后 `curl` 观察 `Set-Cookie` 带 `Secure`。
5. 真实来源验证：外部网络登录失败 6 次触发 429，换一个外部网络应不受影响（后台"客户端连接诊断"里应显示你的公网 IP，而不是 127.0.0.1）。
6. 正式停机演练（另行安排）：`Stop-Process` 杀服务 → 在看门狗重试与服务启动所需时间内恢复；`Stop-ScheduledTask` → 服务停止；`Start-ScheduledTask` → 恢复。

**回退**：`./tools/uninstall-service.ps1` 注销任务并停服务；需要回到手工方式时用 `START-SERVER.cmd`/`./run-local.ps1` 原样启动即可。服务端代码回退按任务 02/03 的说明（注意先降级服务端会影响新客户端的启动授权）。

## 6. 未执行 / 待人工

1. **正式环境停机演练未执行**（简报要求另安排）：第 5 节第 6 步是给正式环境的脚本，本轮只在隔离环境跑过。
2. **`AtStartup` 触发器未在正式环境验证**（需要管理员权限注册；测试用的是 `AtLogOn` + 手动 `Start-ScheduledTask`）。
3. **真实浏览器验证未做**：Cookie 标记是用 HTTP 响应头断言验证的，没有在浏览器里点过后台；生产模式下浏览器行为（Secure Cookie 只在 HTTPS 下回传）是标准行为，但没有实测记录。
4. **历史公网记录**：初次交付写作时曾出现公网 502；用户随后已确认正式地址的 HTTPS `/health` 正常，不应再将这段历史状态当成当前故障。本轮未重新验证公网端到端。TLS 与转发头的实测是在临时应答器上做的（同一个隧道、同一台 frpc）。
5. **隧道自身故障未演练**：不改隧道配置是本轮约束；现有证据是日志里的自动重连记录，不是受控演练。
6. 日志轮转只验证了"按天新建文件 + 启动即写"，未验证跨天滚动、超限滚动与删除（阈值 32 MB / 14 天，实际运行久了才触发）。

## 7. 已知边界

1. **frpc 不清理客户端转发头**，所以"最右一条"是唯一可用的可信值；如果哪天换了隧道实现（例如换成会清理头的反向代理），`ForwardedLimit`/`KnownProxies` 的配置要重新核对，不能照抄。
2. **同一台机器上的本地访问**在开发模式下仍可走明文；生产模式下已取消这条豁免，但 `chispa` 数据目录本来就在本机，物理/管理员级访问不在防护范围内。
3. **看门狗是 PowerShell 循环**，不是 Windows 服务：它随用户登录运行（`Interactive`），机器重启后依赖计划任务触发；用户注销会让它以 `Interactive` 身份停掉。当前安装脚本已支持 `-Trigger AtStartup -Credential (Get-Credential)`，使用同一 Windows 身份的 Password 计划任务。真实开机无人登录演练仍未执行。
4. **告警不会自己发出**：没有配置 `-AlertWebhook` 就只在本地记录，这是有意的（简报：告警目的地不明时做成配置项）。
5. **`/ready` 只反映数据目录与账号文件**，不检查隧道、DLL、磁盘剩余空间；隧道是否有问题要用 `-PublicUrl` 一起探测。
6. **限流仍是进程内存态**：服务重启后清零；多实例部署不支持（与既有会话模型一致）。
