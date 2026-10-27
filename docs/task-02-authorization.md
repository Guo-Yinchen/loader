# 任务 02 交付说明：主程序内部授权，补齐绕开加载器的路径

> 2026-10-26 验收修订：本文件保留初次交付记录，当前修复、测试范围和上线待办见 [验收简报](acceptance-review-2026-10-26.md)。

日期：2026-10-26。涉及两个仓库：`D:\desktoop\loader`（服务端 + 加载器）与 `D:\desktoop\wardogs chispa`（主程序，C++ / MSVC）。

## 0. 结论摘要

1. **主程序原本完全没有授权**：`Usermode\main.cpp` 的启动链是「参数分发 → 取 UIAccess → 打印横幅 → 连 DMA → 主循环」，没有账号、没有服务器、没有任何凭据处理。所以「直接双击下载出来的 EXE」这条路径在改动前是**零门槛**的，且**只改加载器无法触碰它**。
2. 已实现：**一次性票据（2 分钟）→ 短租约（30 分钟，滑动续期）→ 心跳（2 分钟）** 三段式。加载器签发票据，主程序自己兑票；直接双击时主程序自己在线登录、自己兑票。**任何一场启动都必须先拿到服务器当次的答复**，本地不存在「上次通过所以这次也通过」。
3. 已实现运行期控制：心跳会重新检查账号是否有效/到期、程序权限、发布版本是否可用、会话是否被退出登录。被判定拒绝时，覆盖层走既有的 `Render::RequestExit()` 正常关停（KMbox 掩码等清理照常执行）。
4. 验收：`tests/auth_matrix.py` **25/25 通过**，覆盖简报要求的直接启动、有效凭证、错误程序、过期凭证（客户端与服务端两种）、重放、退出登录撤销、账号过期、断网、无服务启动；既有 `smoke.py`（11 项）、`launcher_startup.py`、`publish_checks.py` 全部通过。
5. 仍需人工执行：真机 UAC 提权启动验证、生产部署、代码签名。**本地可执行文件仍然可被有能力者修改**，见 §10。

## 1. 审计：主程序原先缺什么

读 `D:\desktoop\wardogs chispa\CLAUDE.md`（仓库指引）、`Usermode\main.cpp`、`Usermode\Usermode.vcxproj`、`build.ps1`、`Usermode\GUI\Render\render.cpp` 后逐项确认：

| 检查项 | 现状（改动前） |
|---|---|
| 启动入口 | `main.cpp:80` `int main(int argc, char** argv)`，`main.cpp:585` `Render::PrepareForUIAccess()`（成功后会以相同命令行重启一次自身），随后 `Render::MainLoop()` |
| 账号 / 登录 | 无。全仓库没有用户名、密码、会话相关代码 |
| 服务器通信 | 只有 KMbox 的 UDP（`KmBox/`），没有 HTTP 客户端，没有 TLS |
| 到期 / 权限检查 | 无 |
| 版本号 | 二进制里没有程序版本常量；`Wardogs.rc` 只有图标 |
| 与服务端的联系点 | 无。发布版本号只存在于服务端发布记录里 |

**真正缺失的部分**（也就是本任务的实现清单）：

1. 主程序侧的**入口闸门**：在打开 DMA、进入覆盖层之前必须取得当次的在线授权结论。
2. **短时启动凭证**：加载器把「刚刚验证过的登录」转成程序可用的一次性凭证。
3. **服务端凭证接口**：签发、核销、续期、撤销；每次都要重查账号期限/权限/撤销。
4. **运行中撤销通道**：账号到期、权限收回、退出登录、版本下架后，运行中的程序要能停下来。
5. **离线规则**：断网时到底还能跑多久，必须是个明确、可实现的数字。
6. **版本/程序绑定**：凭证要绑到具体程序与版本，否则换个 EXE 也能用。

第 5 项原来不存在（无授权也就无离线规则），第 1–4、6 项全部新增。

## 2. 方案

### 2.1 三个东西，都不是长期凭证

| 名称 | 生命周期（默认） | 谁持有 | 能做什么 | 服务端存哪 |
|---|---|---|---|---|
| 票据 ticket | 120 秒（`CHISPA_LAUNCH_TICKET_SECONDS`，10–600） | 加载器 → 交给主程序 | **兑换一次**租约 | 内存，50k 上限 |
| 租约 lease | 1800 秒（`CHISPA_LAUNCH_LEASE_SECONDS`，30–7200），滑动续期 | 主程序（只在内存里） | 心跳续期；退出时交还 | 内存 |
| 心跳间隔 | 120 秒（`CHISPA_LAUNCH_HEARTBEAT_SECONDS`，5–900，且 ≤ 租约/3） | 服务端在应答里下发 | — | — |

三者都是 `RandomNumberGenerator` 生成的 32 字节不透明串（base64url），服务端只认自己在内存里发过的那一份；**没有 JWT、没有客户端可自证的签名、没有长期令牌**。

### 2.2 时序

加载器路径（PC 上有加载器时）：

```
加载器: 登录（已有） → 完整性校验（已有）
       POST /api/v1/programs/{id}/releases/{version}/launch-ticket   [Bearer 会话]
       ← 200 {ticket, expiresAt, serverTime}
       把 ticket 写进 DPAPI 文件（见 2.3）→ 提权启动 Wardogs.exe
主程序: 读文件 → 删除文件 → 校验来源/程序/版本/本地过期
       POST /api/v1/launch/redeem {ticket, programId, version, device}   [无凭据，票据即凭据]
       ← 200 {lease, leaseExpiresAt, heartbeatSeconds, account, programId, version}
       启动覆盖层；每 120 秒 POST /api/v1/launch/heartbeat  [Bearer 租约]
退出时: DELETE /api/v1/launch/lease  [Bearer 租约]（交还，不等过期）
```

直接双击路径（没有加载器时会走到这里）：

```
主程序: GET /api/v1/launch/capabilities   → 老服务端(404)立刻拒绝，不浪费票据
       （控制台）输入账号 + 密码（输入不回显，且不落盘、不记日志）
       POST /api/v1/session → POST .../launch-ticket（保留私有登录会话至主程序退出；失败则注销）
       POST /api/v1/launch/redeem（同上）
```

登录会话在兑票**之前**就被撤回：这样「会话」只用来换一张票据，不会在服务端留一个没人使用的登录态；租约从此独立存活，由心跳负责。

### 2.3 票据怎么交给程序（不走命令行）

命令行参数会被 WMI、进程浏览器、崩溃转储、shell 历史看到；环境块同理。所以：

- 加载器用 **Windows DPAPI（用户作用域，带固定 entropy `Chispa.LaunchTicket.v1`）** 加密一个 JSON 信封，写到
  `%LOCALAPPDATA%\Chispa\auth\ticket-chispa.bin`（可用 `CHISPA_AUTH_DIR` 覆盖整个目录，供测试）。
- 文件内容是 `{"v":1,"server","programId","version","ticket","expiresAt"}`，字段名由 C# 与 C++ 两侧**各自写死**（`LaunchTicketFile.TicketEnvelope` / `AuthLocal.cpp ConsumeLoaderTicket`），跨语言格式由测试直接覆盖（见 §8）。
- 主程序读完**立刻删除**（无论解析成功与否），文件本身也只值 2 分钟一次核销。
- 主程序只接受「信封里的 server 与本次编译进去的地址一致」的票据，所以被改过的加载器无法把它指向自己的服务器。

### 2.4 服务端在核销与心跳时重查什么

`POST /api/v1/launch/redeem`（`server/LaunchAuth.cs::Redeem`）：

1. 票据存在且未过期、未被使用（用过的票据有 10 分钟墓碑，重放返回 `409 ticket_used`）；
2. 票据里的 programId/version 与请求一致（连续 3 次不一致即作废该票据）；
3. `store.CurrentUser()` 非空 —— 覆盖账号删除、账号到期、管理员重置密码（改 hash）；
4. `store.CanAccess()` —— 覆盖授权范围/Beta 标志被改；
5. `store.GetRelease()` 非空 —— 覆盖版本被删除/下架；
6. 通过后**先删票据再建租约**（两个兑换并发只有一个成功），绑定 `device`、`SessionId`、版本。

`POST /api/v1/launch/heartbeat`：租约存在且未撤销、未过期、device 一致，然后**重复上面 3、4、5 三条**，最后把到期时间滑动到 `now + leaseSeconds`。

## 3. 行为规则（简报逐条对应）

| 场景 | 行为 | 依据 |
|---|---|---|
| **直接启动（双击）** | 没有票据 → 控制台在线登录（3 次机会）→ 兑票。拿不到凭证就不启动，退出码 3 | `LaunchAuth.cpp::SignIn`，矩阵 #1/#2 |
| **有效凭证** | 兑票成功 → 覆盖层启动；租约 30 分钟，每 120 秒续期 | 矩阵 #3 |
| **错误程序** | 信封 programId≠本程序 → 退出码 7，**不发出网络请求** | 矩阵 #4 |
| **错误版本** | 信封 version≠本程序版本 → 退出码 7；服务端另有一层 `ticket_mismatch` | 矩阵 #5 |
| **过期凭证** | 本地：`expiresAt + 120s 时钟容差 < now` → 退出码 4；服务端：`410 ticket_expired` | 矩阵 #6、#10 |
| **重放（同一凭证第二次）** | 服务端 `409 ticket_used` → 退出码 6 | 矩阵 #7 |
| **退出登录** | 加载器 `DELETE /api/v1/session` 同时撤销该会话签出的**待用票据及全部租约**；运行中的程序在**下一个心跳**（≤120 秒）被拒并关停 | 矩阵 #9 |
| **账号过期** | 心跳重查 `CurrentUser` → `403 account_expired` → 关停 | 矩阵 #8 |
| **权限/版本下架** | 心跳 `403 account_forbidden` / `404 program_unavailable` → 关停 | 同一条通道 |
| **断网（运行中）** | **继续运行到租约到期为止**（默认 30 分钟，从最后一次成功心跳/兑票算起）；期间不额外扣减，也不会延长时间 | 矩阵 #11 |
| **断网（启动时）** | **不启动**。新启动一律要求在线（没有「本地上次通过」这种缓存） | 矩阵 #12 |
| **运行中撤销** | 见上：≤120 秒生效，除非同时断网（那就只能等租约到期） | 矩阵 #8/#9 |
| **服务端重启** | 租约是内存态 → 心跳 `401 lease_unknown` → 关停，需从加载器重启程序。这是有意的严格规则（宁可让用户重开，也不留一条没人复核的长期授权） | 设计取舍，见 §10 |
| **服务端过旧（无新接口）** | `capabilities` 404 → 退出码 8，**明确拒绝**而不是跳过检查 | `LaunchAuth.cpp::Acquire` |
| **网关/隧道 5xx** | 判定为「连不上」（退出码 5），提示重试，不说成「被拒绝」 | `LaunchAuth.cpp::Acquire` |
| **时钟被改** | 租约剩余时间用「服务端自己的两个时间戳之差」换算成 `GetTickCount64()` 基准，改系统时间不延长授权 | `AuthJson.h::FindUnixSeconds` + `LaunchAuth.cpp` |
| **设备不符** | 心跳带 `X-Chispa-Device`（`SHA-256(MachineGuid|卷序列号)` 前 16 字节 hex）；服务端比对 → `403 lease_device_mismatch` | `AuthLocal.cpp::DeviceId` |

产品规则的两句话版本：**新启动必须在线；运行中允许最长一个租约（30 分钟）的断线宽限，到期必须停。**

## 4. 代码与接口变更

### 4.1 主程序（`D:\desktoop\wardogs chispa`）

新增 `Usermode\Auth\` 8 个文件，全部不依赖 ImGui/VMM/Logger，可单独编译：

| 文件 | 作用 |
|---|---|
| `ChispaAuthConfig.h` | 编译期身份（程序 id / 版本 / 服务器地址）+ 策略常量（超时、时钟容差、心跳上下限） |
| `AuthJson.h` | 只读顶层字段的严格 JSON 扫描器（含转义、嵌套跳过、ISO-8601 → Unix 秒）。不引入第三方 JSON 库 |
| `AuthLocal.h/.cpp` | 设备指纹（BCrypt SHA-256）、DPAPI 读取并删除信封、`CHISPA_AUTH_DIR` |
| `AuthTransport.h/.cpp` | WinHTTP 客户端：TLS 1.2+、**从不忽略证书错误**、**不跟随重定向**、超时、64 KB 应答上限；远程明文 HTTP 在发布构建里直接拒绝 |
| `LaunchAuth.h/.cpp` | 闸门本体：读票据/在线登录 → 兑票 → 心跳线程 → 拒绝即回调、交还租约 |

改动：

- `Usermode\main.cpp`：新增 `--auth-build`、`--auth-check`；在 `Render::PrepareForUIAccess()` 成功之后、`DMAInterface::ConnectToDMA()` 之前插入闸门；设置关停回调为 `Render::RequestExit()`；主循环返回后 `Auth::Shutdown()`；`return Auth::ExitCode()`；`--help` 增补两个授权模式。
- `Usermode\Usermode.vcxproj`：加入 3 个 `.cpp` / 5 个 `.h`；`winhttp.lib`；`ChispaAuthorization` 属性组（`ChispaProgramVersion`、`ChispaServerUrl` 默认值）与两个字符串宏。
- `build.ps1`：新增 `-Version`（写进二进制）与 `-ServerUrl`（本机联调用回环地址）；构建后**回读**二进制里的 `--auth-build` 输出，未盖章时给出显式警告。
- `tests/auth/auth_selftest.cpp`、`tests/auth/build-selftest.ps1`、`tests/auth/.gitignore`（新增）。

**为什么闸门放在 `PrepareForUIAccess()` 之后**：该函数会用**同一条命令行**重启进程一次并 `ExitProcess`。放在它之前，闸门会在两个进程里各跑一次，第二次拿着已经核销的票据 → 必然被当成重放。放在它之后，恰好只跑一次，而且是在最终那个提权进程里。这一点在 `main.cpp` 注释里写明了，避免以后被人「顺手挪回去」。

### 4.2 服务端与加载器（`D:\desktoop\loader`）

新增 `server/LaunchAuth.cs`（`LaunchGate`：票据/租约/心跳/撤销/限流/清扫），`server/Program.cs` 接入 5 个路由 + `BearerToken()` 辅助 + 退出登录时撤销租约。

| 接口 | 认证 | 成功 | 主要失败码 |
|---|---|---|---|
| `GET /api/v1/launch/capabilities` | 无 | `{api:1, ticketSeconds, leaseSeconds, heartbeatSeconds}` | — |
| `POST /api/v1/programs/{id}/releases/{version}/launch-ticket` | Bearer 会话 | `{ticket, expiresAt, serverTime, ticketSeconds}` | 401/403/404 `release_unknown` |
| `POST /api/v1/launch/redeem` | 票据 | `{lease, leaseExpiresAt, serverTime, heartbeatSeconds, leaseSeconds, account, programId, version, api}` | 400 `invalid_request`、401 `ticket_unknown`、403 `ticket_mismatch`/`account_expired`/`account_forbidden`、404 `program_unavailable`、409 `ticket_used`、410 `ticket_expired`、429 `rate_limited` |
| `POST /api/v1/launch/heartbeat` | Bearer 租约 + `X-Chispa-Device` | `{ok:true, leaseExpiresAt, serverTime, heartbeatSeconds, leaseSeconds}` | 401 `lease_unknown`、403 `lease_revoked`/`lease_device_mismatch`/`account_expired`/`account_forbidden`、404 `program_unavailable`、410 `lease_expired` |
| `DELETE /api/v1/launch/lease` | Bearer 租约 | 204 | — |

**旧接口保持不变**：`POST /api/v1/programs/{id}/releases/{version}/launch` 仍在（矩阵探测里确认返回 200），所以旧加载器不会因为服务端升级而失灵。

其它：

- `client/LaunchTicketFile.cs`（新）：DPAPI 保护写入、原子替换、读前清理 10 分钟以上的陈旧文件、启动失败时删除。
- `client/LauncherApi.cs`：新增 `RequestLaunchTicketAsync()`（30 秒超时，区分「版本不存在」与「服务端过旧」两种 404）。
- `client/Installer.cs::LaunchAsync`：完整性校验（原有）→ 请求票据 → 写交接文件 → 提权启动；启动抛异常时删掉票据。
- `publish-current.ps1`：发布前用 `Wardogs.exe --auth-build` **回读二进制里的版本**，与 `-Version` 不一致就中止（否则会发出「能下载、装完却拒绝启动」的包）。
- `tests/Program.cs`：新增 `--write-launch-ticket`（用生产代码写交接文件，供 C++ 侧读取）+ 加载器侧票据接口断言。
- `tests/auth_matrix.py`（新）：验收矩阵。

限流：`redeem` 按来源 IP 计 12 次失败/分钟；票据被错目标试探 3 次即作废；租约/票据/墓碑/失败窗口都有 60 秒清扫与 50k 上限。

## 5. 版本戳与构建/发布链

租约绑定 `programId + version`，程序把自己编译进去的版本与之比对。所以：

```powershell
# Wardogs 仓库
./build.ps1 -Version 1.4.2                       # 盖章；不传 -Version 就是 0.0.0（未盖章，拒绝启动覆盖层）
# loader 仓库
./publish-current.ps1 -Version 1.4.2             # 先回读二进制章，再打包发布
```

`0.0.0` 的构建**故意**无法运行覆盖层（`Result::Mismatch`，明确提示「没有版本章」）。这是防止「随手构建的二进制被当成正式包」的开关，代价是开发机必须显式传版本号，并且服务端要发布同号版本。本机联调还要一致地传 `-ServerUrl http://127.0.0.1:5115`（加载器本机档案用的就是这个地址，两边不一致会以 `Mismatch` 拒绝）。

## 6. 秘密处理

- **客户端没有任何签名私钥**，也没有任何长期令牌：票据/租约都是服务端内存里的不透明串，靠 TLS 保证传输。简报里「签名私钥不得放在客户端」因此是**结构性满足**，不是靠纪律。
- 也没有引入非对称凭证（如 RSA 签名的租约）。原因写在这里，免得以后被误解为遗漏：本方案不在客户端持久化租约，离线宽限只依赖**本次运行内存里**那份租约的到期时间，没有「本地文件可信」这一环，签名能防的伪造面并不存在；而任何本地检查都可以被改二进制绕过（§10），加签名不会改变这个边界。若将来要做「断网后仍可重新启动」，**那时**才需要服务端签名 + 内置公钥，并且要同时接受「离线启动」这个产品规则。
- 票据不落命令行、不落环境变量、不落普通日志；交接文件是 DPAPI 密文且读后立即删除。
- 日志只写账号、程序、版本、来源 IP、结果；**不写**票据、租约、密码、应答正文（`LaunchAuth.cs` 里所有 `LogInformation/LogWarning` 都只带这些字段）。
- 管理端已有的「查看用户密码」功能未改动、未削弱。

## 7. 兼容顺序与旧版本处置

| 服务端 | 加载器 | 主程序 | 结果 |
|---|---|---|---|
| 新 | 新 | 新 | 正常：票据 → 租约 → 心跳 |
| 新 | 旧（无票据） | 新 | **仍需在线授权**：程序发现没有票据 → 控制台登录 → 兑票。可用但体验差（提醒：这是旧加载器的用户） |
| 新 | 新 | 旧（无内置检查） | 加载器签名/完整性照旧，程序**不检查**——这是本次无法回溯修复的部分，见下 |
| 旧 | 新 | 新 | 加载器在 `launch-ticket` 收到 404 时**明确拒绝启动**（不降级、不放行）；程序端 `capabilities` 404 也明确拒绝 |
| 旧 | 旧 | 旧 | 与改动前一致：仍然可以绕开（既成事实） |

**旧 EXE 的限制**：已经分发出去、没有内置检查的 `Wardogs.exe`，双击仍然能跑。能做的处置只有三种，都需要人工决策：

1. 只发布带检查的新版本，旧版本在服务端**删除**（删除后加载器无法再下载/启动它）——已下载到用户磁盘上的旧 EXE 不受影响；
2. 公告 + 版本号提升，让用户主动更新；
3. 与账号体系配合（例如到期后不再续），但**不要**声称「禁止启动」能拦住旧 EXE。

这也是简报里「旧版本仍可能绕开新流程」的正面回答：新流程只能约束**新构建**。

## 8. 验收：实际运行的命令与结果

前置：`dotnet` 走仓库自带 `.tooling\dotnet`；服务端 Release 构建；`tests/Chispa.Smoke` 构建；MSVC（`D:\vscode\vstudio`）。

```powershell
# loader 仓库
./build.ps1                                            # 服务端 + 加载器（均 0 警告 0 错误）
dotnet build tests/Chispa.Smoke.csproj -c Release      # 验收要用的 C# 交接文件写入器
python tests/smoke.py                                  # 11 项 PASS，exit 0
python tests/launcher_startup.py                       # PASS
python tests/publish_checks.py                         # ALL PASS（未打包）
python tests/auth_matrix.py                            # 25/25 PASS，exit 0
```

`auth_matrix.py` 会：起一个临时数据目录 + 随机回环端口的服务（票据 10 秒 / 租约 30 秒 / 心跳 5 秒，只为让过期与断线在几十秒内可观测）→ 用 `tests/auth/build-selftest.ps1` 把 `Usermode\Auth\*.cpp` **原样**编译成 `auth-selftest.exe` → 逐条跑：

```
PASS direct start without a ticket or a terminal is refused          (exit 3)
PASS direct start with account sign-in is allowed                    (exit 0)
PASS the hand-off file carries no plain-text ticket
PASS the loader's own ticket file is accepted by the program         (C# 写 → C++ 读)
PASS the hand-off file is consumed when the program reads it
PASS a ticket issued for another program is refused                  (exit 7)
PASS a ticket for another version is refused                         (exit 7)
PASS a ticket that expired before redemption is refused              (exit 4)
PASS a fresh ticket works once / the same ticket again is replayed    (exit 6, ticket_used)
PASS an account that expires mid-session stops the program            (exit 4)
PASS signing out of the loader stops a running program               (exit 4, signed_out)
PASS the service refusing an expired ticket is reported              (exit 4, ticket_expired)
PASS a running program survives a network outage on its lease        (>15s 仍 allowed)
PASS ...and then stops when the lease lapses                         (exit 4, lease lapsed)
PASS starting with no service at all is refused                      (exit 5)
25/25 checks passed
```

主程序侧另做的人工（非自动化）检查：

```powershell
# wardogs 仓库
./build.ps1 -Version 1.2.3
bin\x64\Wardogs\Wardogs.exe --auth-build    # chispa 1.2.3 -> https://...
bin\x64\Wardogs\Wardogs.exe --auth-check    # 生产地址当前 502 → "the license server answered HTTP 502"，exit 5
```

**未执行的验证**（不要当成已通过）：

- 真机「加载器提权启动 → 子进程读同一用户 DPAPI 文件」这一跳（需要 UAC 交互，且当前机器未安装分发目录）。矩阵覆盖了 C# 写 / C++ 读的格式与 DPAPI 熵一致性，未覆盖 `Process.Start(Verb=runas)` 本身。
- 生产部署与公网访问：写作时 `https://teamviewer.rm7lwjwtg.nyat.app:24154/health` 返回 **502**，因此本轮不对生产可用性作任何声明。
- Windows 11 / ARM64、代理与 TLS 检查型代理环境下的表现。
- 中文输入法下的控制台密码输入（该路径显式只接受 ASCII 可打印字符，见 §10）。

## 9. 部署与回退

**部署顺序（必须按此顺序）**：

1. 备份 `.local/server/`（账号、发布数据、Data Protection 密钥）。
2. 部署新服务端，验证 `GET /api/v1/launch/capabilities` 返回 `{"api":1,...}`（老服务端是 404）。
3. 在 Wardogs 仓库 `./build.ps1 -Version X`，再在 loader 仓库 `./publish-current.ps1 -Version X` 发布（脚本会先比对版本章）。
4. 发布/分发新加载器（`publish-client.ps1`）。
5. 用户在加载器里「下载 / 更新」到新程序；**在此之前发布的程序没有内置检查，升级才会生效**。

**回退**：

- 回退程序：重新发布上一个**带检查**的版本号（接口向后兼容）；**不要**重发无检查的旧构建。
- 回退加载器：旧加载器 + 新服务端可用（老接口仍在），只是新程序会要求控制台登录。
- 回退服务端：新程序会以 `Unsupported`（capabilities 404）拒绝启动，旧程序不受影响。因此**不要只回退服务端而保留新程序的分发**；若必须回退服务端，先暂停新程序分发。
- 回退后运行中的程序：`lease_unknown`，下一个心跳关停，需从加载器重启。

**仍需人工**：上面第 2–5 步的实际执行、真机 UAC 验证、EXE 代码签名（简报任务 03/05 的范围）。

## 10. 遗留问题与已知边界

1. **本地可执行文件仍可被修改**：有本机控制权的人可以改二进制（把闸门调用改成 nop、或直接改 `ExitCode` 返回值）、挂调试器、改内存。本方案把「正常双击一个诚实副本」变成必须联网授权，把「拷给别人就能用」变成拷过去也没有账号与票据；它**不是**防篡改运行时保护，也不宣称无法破解。
2. **诊断模式（`--probe-*`、`--scan-*`、`--dump-raw`、`--peek` 等）没有加闸门**。理由：它们在 `Render::PrepareForUIAccess()` 之前就返回，覆盖层不会启动，且是现场排障工具；给它们加检查会破坏开发者流程。这是已知的口子（能用 DMA 读内存），若要收紧，正确做法是把 UIAccess 重启提到参数分发之前，再统一加闸门——那会改变所有诊断模式的前置条件，属于产品决策，本轮不做。
3. **租约是服务端内存态**：服务端重启 = 所有运行中的程序在一个心跳内被停掉；多实例部署不支持（单实例假设与现有内存会话一致）。需要「重启不打断」时应改为持久化租约，属于另一轮改动。
4. **心跳间隔决定撤销时延**：默认 2 分钟。若要求更快，调小 `CHISPA_LAUNCH_HEARTBEAT_SECONDS`，代价是请求量上升；低于 5 秒会被拒绝。
5. **断网撤销不可达**：断网期间只能等租约到期（默认 30 分钟）。这是「允许离线宽限」的必然代价，写在这里而不是藏在实现里。
6. **控制台登录只接受 ASCII 可打印字符**（密码里含中文会被忽略按键）。加载器里的登录不受此限。若要支持，需要 `_getwch` + 宽字符处理，属于后续改进。
7. **加载器与程序不是同一 Windows 账号时**（例如加载器普通账号、程序用另一个管理员账号提权）：DPAPI 解不开，程序会回退到控制台登录并提示原因；若该账号也被拒绝，则拒绝启动。
8. **管理端没有「立即踢掉这个账号正在运行的程序」的按钮**。撤销通过账号状态达成：改到期时间、改授权范围、删账号、退出登录（会话撤销）、删除发布版本，全都在下一次心跳生效。若需要显式踢人按钮，是在 `LaunchGate` 上包一层管理接口的小改动。
9. **版本号必须与发布号一致**：`publish-current.ps1` 已经拦截不一致，但开发者手工 `dotnet publish`/直接拷贝 EXE 时仍可能造出章不符的包。
10. **`tests/auth/bin` 是每次编译产物**（已 `.gitignore`），不随包分发。
