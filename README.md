# Chispa 加载器

当前成品：[客户端与独立服务端 1.0.4](https://github.com/Guo-Yinchen/loader/releases/tag/v1.0.4)；[软件 1.2.4](https://github.com/Guo-Yinchen/Wardogs/releases/tag/v1.2.4)。本地统一放在 `dist/client`、`dist/server`、`dist/software`，见 [工作区与发布说明](docs/workspace.md)。

> 2026-10-27：[加载器 1.0.4 默认管理员启动](docs/launcher-admin-1.0.4.md)。双击时自动请求管理员权限，确认 Windows UAC 后启动。

> 2026-10-27：[加载器 1.0.3 界面修复](docs/launcher-ui-1.0.3.md)。窗口按钮移入主矩形，账户信息分行独立显示，统一文字测量和缓冲绘制，修复文字裁切及 DPI 留白不足。

> 2026-10-27：[邮箱注册与激活码（加载器 1.0.2）](docs/registration-activation-2026-10-27.md)。支持邮箱格式账号注册、后台生成激活码和账户续费；修复到期时间布局、后台刷新闪烁和窗口按钮。

> 2026-10-27：[账户剩余时间、启动免重复登录与到期退出](docs/account-session-2026-10-27.md)。过期账号可登录查看，新版主程序菜单左下角显示账号与到期时间；更新需要配套发布服务端、加载器和 Wardogs 1.2.4。

> 最新验收与修复：[2026-10-26 验收简报](docs/acceptance-review-2026-10-26.md)，包含已修复问题、测试结果和正式上线待办。

这里是独立的 Windows 客户端和自建服务端。它直接启动 Chispa 的 EXE，不使用 Gamesense-Loader 的 DLL 注入代码，也没有改动 Wardogs/Chispa 的游戏功能源码。

## 已实现

- Chispa 使用独立的闪光标识，统一登录页、程序库、窗口、任务栏、EXE 和后台浏览器标签。矢量源文件及多尺寸图标在 `assets/branding/`，运行 `python tools/build_brand.py` 可重新生成。
- 下载器使用深蓝渐变与半透明玻璃质感面板；Windows 10/11 均使用无系统边框的圆角窗口，自绘最小化和关闭按钮，顶部空白处可拖动。支持显示密码、退出登录、键盘选择程序和重新安装修复。
- 自绘控件（按钮、勾选框、程序卡片、输入框、玻璃面板）在每次重绘时先重绘自身背景层，再叠加圆角填充与文字；页面渐变和页面装饰缓存在各页自己的位图中。因此悬停、按下、禁用、选中、勾选以及按钮文字变化都不会留下残影，圆角边缘也不会出现黑边。
- 后台采用五个导航分区；账号和程序可搜索，编辑表单默认折叠，并按授权类型显示相关选项。支持窄屏布局、连接状态反馈及防重复提交。
- 每次启动程序都会在线重新验证账号期限与程序权限，并对照服务端发布包核验本机 EXE、DLL、SYS、OCX、COM 的 SHA-256；文件被修改、缺失或多出这些可执行文件时阻止启动。退出登录会撤销服务端会话。旧发布包无需重新上传即可参与验证。
- **主程序本身也检查授权**（任务 02）：加载器把账号会话换成一张 2 分钟、只能核销一次的启动票据，程序用票据换一份 30 分钟的租约，并在运行中每 2 分钟心跳续期。直接双击下载出来的 EXE 也会走同一条闸门——没有票据就在控制台在线登录，**任何一次启动都必须先得到服务端当次的答复**。账号到期、权限收回、退出登录、版本下架都会让运行中的程序在下一个心跳关停；断网时按租约到期停止（默认 30 分钟）。详见 `docs/task-02-authorization.md`。
- 重新安装会重新下载并校验，原有版本目录保存在该程序安装根目录的 `backups/` 下，便于找回程序生成的配置。它们不会被自动重新启用。

- 服务端命令行创建账号；密码使用独立随机盐和 PBKDF2-SHA256 校验。为满足管理员查看密码的需求，新建或重置的账号还会在服务端保存一份由 ASP.NET Data Protection 加密的密码，后台需管理员登录并主动点击“显示用户密码”才能查看。请连同 `.local/server/keys/` 一起备份并严格保护；既有账号的旧密码只有哈希，无法还原，重置后才能查看新密码。
- 登录后签发 12 小时的内存会话；未登录不能查询版本或下载运行包。
- `/admin` 网页后台使用单独的管理员账号，可查看服务器状态、近 60 分钟请求趋势、登录和下载计数；创建、编辑和删除账号；到期时间精确到分钟，后台明文显示服务器本地时间与对应 UTC 时间并可直接修改；登记、编辑和删除程序；上传 ZIP 发布版本。统计从本次服务启动开始计数，重启后清零。
- 账号密码可由管理员在后台重设：必须先勾选确认，才解锁新密码输入框，保存时再确认一次；保存后该账号的旧登录会话立即失效。适合用户忘记密码时联系管理员处理，无需重建成账号。
- 网页把 ZIP 按 4 MB 分块上传，每块连接失败会重试；上传完成后服务器继续校验和发布，页面轮询结果。若整个包只套了一个外层文件夹（例如直接压缩了一个程序文件夹）、使用反斜杠路径或夹带 `__MACOSX`、`Thumbs.db`、`desktop.ini` 等系统文件，服务器会自动规范化后再发布；只有入口 EXE 不在顶层或路径不安全时才会拒绝，并提示当前 ZIP 顶层内容。
- 删除程序会一并删除它的全部发布版本，并从账号授权里剔除该程序；账号因此不再满足单程序/多程序条件时会自动降级为“未授权任何程序”，不会顺延成“全部程序”。
- 服务端可登记多个程序；每个程序有独立的名称、入口 EXE 和版本目录。发布包由签名工具打包成 ZIP 并签名，服务端只校验和保存。
- WinForms 客户端启动时只显示登录，登录页可直接测试客户端到服务器的连接。登录成功后展示带图标的程序卡片；选择程序可查看版本、下载或更新、启动。未发布的授权程序会显示为“即将推出”，下载和启动按钮不可用。程序列表每分钟同步一次，也可手动点击“同步程序”。下载时核对大小和 SHA-256，并安全解压到各程序自己的版本目录。启动一律请求管理员权限。
- 勾选“记住登录信息”并成功登录后，账号密码由 Windows DPAPI 加密保存在当前用户的 `%LOCALAPPDATA%\Chispa\launcher-credentials.json`。客户端界面不显示服务器请求地址；更换服务器地址后不会载入原地址保存的密码。
- 账号范围可设为全部稳定程序、单程序或多程序，另可独立开启 Beta 权限。每个 Beta 选项关联一个稳定程序；授权稳定程序并开启 Beta 后，账号能同时访问该稳定程序及其 Beta 选项。未开启 Beta 的“全部稳定程序”账号不能访问 Beta。权限在列表、版本查询和下载接口均检查，后台修改后对已有会话立即生效。
- `publish-current.ps1` 从 Wardogs 构建目录复制 EXE、DLL、`info.db`、资源和地图包，排除本机日志、设置、符号和中间文件；随后用 `tools/Chispa.Sign` 打包、签名并发布。
- `publish-client.ps1` 发布 Windows x64 单文件、自带 .NET 运行时的客户端 EXE。

## 本机试用

需要 .NET 10 SDK。脚本优先使用 `.tooling/dotnet/dotnet.exe`，没有则使用系统的 `dotnet`。

```powershell
./build.ps1
./run-local.ps1 admin-create manager
./publish-current.ps1 -Version 1.0.0
./run-local.ps1
```

`admin-create` 会要求输入至少 12 个字符的管理员密码；也可以双击 `CREATE-ADMIN.cmd` 完成这一步。最后一条命令需要保持运行；双击 `START-SERVER.cmd` 会在服务启动成功后自动打开 `http://127.0.0.1:5115/admin`。以管理员身份创建客户端账号并设置到期时间：按服务器本地时间填写，精确到分钟，后台会同时显示对应的 UTC 时间；留空表示永久。然后启动客户端，使用刚创建的账号登录，选择 Chispa，再点击下载和启动。

## 给用户的单文件客户端

双击 `PUBLISH-CLIENT.cmd`，或运行：

```powershell
./publish-client.ps1
```

默认就是**正式发布**：地址取自 `release/release.json` 的 `production` 档案（当前为 `https://teamviewer.rm7lwjwtg.nyat.app:24154`），不需要每次手工传地址。输出为 `dist/client/Chispa.Launcher.exe`，该目录中只有这一个文件，可直接复制给 Windows x64 用户；旁边的 `dist/client.manifest.json` 记录本次产物的地址、SHA-256、大小和构建时间。

打包流程内置三道保障：

1. **地址校验**：正式档案必须是公网 HTTPS 站点根地址。回环地址（`127.0.0.1`/`localhost`/`[::1]`）、远程明文 HTTP、带路径/查询/锚点、带账号密码、无效端口、输出目录指向仓库根，都会在开始构建前直接失败。`-ServerUrl` 只是覆盖来源，同样不能绕开这些规则。
2. **产物自检**：构建先写入暂存目录，通常运行产出的 EXE（`--print-server-url` / `--verify-server-url`）检查真实内嵌地址；普通权限进程遇到管理员启动要求时，直接检查成品的权限清单及单文件包内程序集元数据。两种方式都会用错误地址做反向验证，不以明文字符串搜索或构建参数代替成品检查。
3. **失败不动上一版**：只有构建和自检都通过，才会把暂存目录换成分发目录，旧产物保留在 `dist/client.previous/`；任何一步失败，分发目录逐字节保持不变。

本机联调不要用正式档案，显式指定本地档案：

```powershell
./publish-client.ps1 -Profile local
```

它只接受回环地址（必须有端口），产物写入 `.local/distribution/client-local/`，不会覆盖正式分发包。直接 `dotnet build` 得到的 `client/bin/.../Chispa.Launcher.exe` 同样是回环的本地开发版本，**不要分发**。

复核已有产物（不重新构建）：

```powershell
./publish-client.ps1 -VerifyArtifact dist/client/Chispa.Launcher.exe
```

地址写入 EXE，无需旁边放 `launcher.json`。运行时 .NET 可能将内置的原生库临时解压到系统临时目录；下载的程序则存入用户的 `%LOCALAPPDATA%/Chispa`，不会散落在加载器 EXE 旁边。客户端的“测试连接”会请求这个内置地址；后台“客户端连接诊断”显示最近一次成功探测的时间和来源地址。后台的“测试当前浏览器连接”只检查浏览器所在电脑到服务器的链路。测试公网访问时，应在外网电脑使用内置正式 HTTPS 地址的客户端点击“测试连接”。

## 管理程序与账号分类

在后台“程序与选项”里可修改客户端显示名称，使用最多 16 字符的文字图标（如 emoji），或选择 PNG、JPG、WebP、GIF、BMP、AVIF 图片作为图标。原图上限 20 MB；选择后会打开预览窗口，可拖动、缩放并确认正方形裁剪。浏览器会自动转换为不超过 128 KB、最大 256×256 的 PNG，再提交给服务器；必要时会进一步缩小尺寸。修改不会更换程序 ID 或入口 EXE，因此已安装版本仍然有效。关闭 JavaScript 时仍可直接提交符合旧限制的 PNG。

此工作区的本机数据已登记 `beta-slot` 和 `tool-slot` 两个占位程序，`beta-slot` 关联稳定程序 `chispa`；新部署可在后台自行登记。它们没有运行包，可用于测试 Beta、单程序和多程序授权；获授权的占位程序会显示在客户端，但无法下载或启动。创建账号时只选择稳定程序，单程序须选 1 项，多程序至少选 2 项；Beta 是独立开关。账号列表中可随时调整范围、勾选程序和 Beta 权限。

打开 `http://127.0.0.1:5115/` 可查看服务状态。服务端默认只监听本机 `127.0.0.1:5115`。账号、发布数据和后台会话密钥保存在 `.local/server/`，不会被 Git 跟踪。若忘记管理员密码，在本机终端运行 `./run-local.ps1 admin-reset manager`；重置后旧的后台登录会话失效。

`publish-current.ps1` 会为每个版本创建新的暂存目录，并登记 ID 为 `chispa` 的程序。正式分发前检查 `.local/staging/<版本号>/` 的内容。它会自动查找 `../wardogs chispa/bin/x64/Wardogs`；如果构建目录不同，可指定 `-SourceDirectory`。

添加其他程序可在后台“登记程序”，再上传包含完整运行目录的 ZIP。也可用命令行登记并发布，例如：

```powershell
./run-local.ps1 program-add tool-b "辅助工具" Tool.exe
./tools/Chispa.Sign keygen --out .local/signing --id release-2026-11   # 每个发布环境一次
dotnet tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll pack --source C:`\path`\to`\prepared-tool-folder --out .local/releases/tool-b-1.0.0.zip --executable Tool.exe
dotnet tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll sign --package .local/releases/tool-b-1.0.0.zip --program tool-b --version 1.0.0 --executable Tool.exe --key .local/signing/release-private.pem --key-id release-2026-11 --out .local/releases/tool-b-1.0.0.signature.json
./run-local.ps1 publish tool-b 1.0.0 .local/releases/tool-b-1.0.0.zip .local/releases/tool-b-1.0.0.signature.json
```

入口 EXE 必须放在发布目录的顶层。登记并授权后，程序即可作为占位选项出现在登录后的列表中；发布版本后才可下载和启动。每个程序可单独发布新版本，已有安装互不覆盖。

## 发布签名（任务 03）

下载服务器的 SHA-256 清单和被替换的包一起换掉时，普通校验无法分辨；签名可以。所以发布包由**发布环境**签名，客户端内置公钥验签：

1. `tools/Chispa.Sign keygen --out <目录> --id <密钥标识>` 生成 RSA-2048 密钥（PKCS#8 PEM）。**私钥只留在发布环境**：不进 Git、不进分发包、也不放在下载服务器上（服务端只拿到公钥，用来在发布前自查）。
2. 把 `keygen` 打印的那一行贴进 `client/ReleaseTrust.cs` 的 `BuiltIn` 数组并重新构建客户端。这一步就是信任链本身：客户端只认编译进去的公钥。
3. `chispa-sign pack` 打包（入口 EXE 在顶层、去掉系统垃圾文件），`chispa-sign sign` 签名。签名覆盖**固定格式的清单原文**（格式版本、程序 ID、版本、入口 EXE、包大小、包 SHA-256、文件清单摘要），不是 JSON 重序列化结果，所以没有字段顺序或空格造成的歧义。
4. 把 `package.zip` 和 `signature.json` 一起上传（后台发布页现在要求两个文件），或 `./run-local.ps1 publish <id> <version> <zip> <signature.json>`。服务端**不改写**已签名的包：需要规范化的包请用签名工具重新打包。

拒绝策略（都是明确拒绝，没有「警告后继续」）：

| 情况 | 结果 |
|---|---|
| 没有签名 | 拒绝安装（`这个版本没有签名`） |
| 密钥不在内置列表 | 拒绝（`签名密钥不受信任`） |
| 签名校验失败 | 拒绝（`签名校验失败，内容可能已被替换`） |
| 签名有效但程序 ID / 版本 / 入口 EXE / 大小 / 哈希与清单不一致 | 拒绝 |
| 文件清单与签名里的摘要不一致 | 拒绝（安装与启动都会查） |
| 上传时签名不匹配（服务端自查） | 发布失败，不落盘 |

**历史版本迁移**：任务 03 之前发布的包没有签名，新客户端会拒绝。就地补签名即可，不用重新打包：

```powershell
dotnet tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll sign --package .local/server/programs/chispa/releases/1.0.1/package.zip --program chispa --version 1.0.1 --executable Wardogs.exe --key .local/signing/release-private.pem --key-id release-dev-2026-10 --out .local/releases/1.0.1/signature.json
$env:CHISPA_SERVER_DATA = (Join-Path (Get-Location) '.local/server')
$env:CHISPA_RELEASE_TRUST_KEYS = (Join-Path (Get-Location) '.local/signing/release-public.json')
dotnet .local/server-builds/<当前构建ID>/Chispa.Server.dll sign-release chispa 1.0.1 .local/releases/1.0.1/signature.json
```

`sign-release` 会重新校验包与签名的对应关系，并把文件清单写进 manifest（旧 manifest 没有这一项，启动校验需要它）。

**密钥轮换**：先让客户端同时信任新旧两把公钥并发出去，再用新密钥签新版本，最后在后续客户端里删掉旧公钥——被删掉的公钥立刻不再被接受。

运行自动化联调：

```powershell
./build.ps1
$env:DOTNET_CLI_HOME = (Join-Path (Get-Location) '.tooling/home')
& ./.tooling/dotnet/dotnet.exe build tests/Chispa.Smoke.csproj -c Release
python tests/smoke.py
python tests/launcher_startup.py
python tests/auth_matrix.py               # 主程序授权的验收矩阵，约 2 分钟
python tests/signature_matrix.py          # 发布签名验收矩阵，约 1 分钟
python tests/proxy_matrix.py              # 代理信任 / Cookie / 就绪检查矩阵，约 1 分钟
python tests/backup_drill.py              # 备份→破坏→恢复→验证→损坏备份拒绝，约 2 分钟
python tests/admin_matrix.py              # 后台二次验证/CSRF/限流/审计/网络限制，约 1 分钟
python tests/version_matrix.py            # 推荐版本、撤回、禁止启动与加载器元数据，约 1 分钟
python tests/fault_matrix.py              # 断点续传、限流、包损坏、权限撤销，约 1 分钟
powershell -File tests/recovery_check.ps1 # 安装→崩溃→自动恢复→卸载，约 1 分钟
python tests/publish_checks.py            # 发布入口的地址校验矩阵，几秒完成，不打包
python tests/publish_checks.py --build    # 额外真实打包到 .local/qa-publish 并验证产物地址
```

## 主程序内部授权（任务 02）

只检查加载器的登录挡不住「复制已下载的 EXE 直接运行」，所以主程序自己也要一份授权。流程：

1. 加载器登录并校验完程序完整性后，请求 `POST /api/v1/programs/{id}/releases/{version}/launch-ticket`，得到一张 **2 分钟、只能核销一次** 的票据。
2. 票据由 Windows DPAPI 加密后写入 `%LOCALAPPDATA%\Chispa\auth\ticket-<程序>.bin`；**不走命令行，不走环境变量**，程序读后立即删除。
3. 程序（`Wardogs.exe`）在打开 DMA 之前调用 `/api/v1/launch/redeem` 换一份 **30 分钟租约**，之后每 **2 分钟** 用 `/api/v1/launch/heartbeat` 续期；退出时 `DELETE /api/v1/launch/lease` 交还。
4. 服务端在核销与每次心跳时都重新检查：账号有效期、程序授权范围、发布版本是否仍可用、租约是否已撤销、设备指纹是否一致。

行为规则：

| 场景 | 结果 |
|---|---|
| 直接双击（无票据） | 控制台登录账号后兑换票据；无控制台或登录失败则不启动（退出码 3） |
| 账号到期 / 权限收回 / 版本下架 | 下一个心跳被拒，覆盖层正常关停（退出码 4） |
| 在加载器里退出登录 | 该会话签出的运行授权全部撤销，运行中的程序 ≤2 分钟停止 |
| 断网（运行中） | 继续运行到租约到期（默认 30 分钟），不延长、不静默放行 |
| 断网（启动时） | 不启动（退出码 5）：新启动一律要求在线 |
| 同一票据第二次使用 | 拒绝（退出码 6，服务端 `ticket_used`） |
| 票据来自别的程序/版本 | 拒绝（退出码 7） |
| 服务端过旧（无这些接口） | 明确拒绝启动（退出码 8），不降级放行 |
| 服务端重启 | 租约是内存态，运行中的程序下一个心跳停止，需重新从加载器启动 |

维护要点：

- 版本号必须同时出现在二进制与发布记录里：`wardogs chispa` 里 `./build.ps1 -Version 1.4.2`，本仓库 `./publish-current.ps1 -Version 1.4.2`（后者会回读二进制里的章并比对）。不传 `-Version` 的构建是 `0.0.0`，**故意**无法启动覆盖层。
- 时间参数可用环境变量调整：`CHISPA_LAUNCH_TICKET_SECONDS`（默认 120）、`CHISPA_LAUNCH_LEASE_SECONDS`（默认 1800）、`CHISPA_LAUNCH_HEARTBEAT_SECONDS`（默认 120，且不超过租约的 1/3）。
- `python tests/auth_matrix.py` 会用独立临时目录和随机回环端口起一个短期服务（票据 10 秒 / 租约 30 秒 / 心跳 5 秒），把 `wardogs chispa/Usermode/Auth/*.cpp` 原样编译成 `auth-selftest.exe` 再逐条验证，不接触真实账号与真实分发包。
- 部署顺序：先服务端，再发布带章的程序，再分发新加载器。回退时不要只回退服务端而继续分发新程序（新程序会以「服务端过旧」拒绝启动）。

## 部署与恢复（任务 04）

当前正式形态（已实测确认，非猜测）：

```
公网 :24154  HTTPS（ZeroSSL *.rm7lwjwtg.nyat.app，TLS 1.3）
   ↓  隧道客户端 frpc 在本机终止 TLS
http://127.0.0.1:5115   明文，携带 X-Forwarded-For / X-Forwarded-Proto
```

隧道由 `SakuraFrpService`（开机自启）与 `frpc` 负责，**已有自动启动和自动重连，不要重复安装**。实测发现 frpc 会追加真实 IP、覆盖 `X-Forwarded-Proto`，但**不清理**客户端自带的 `X-Forwarded-For` / `X-Real-IP` / `Forwarded`——所以服务端只信任配置里列出的代理，并且只取代理追加的那一条：

```powershell
# 正式部署
./tools/install-service.ps1 -Production -TrustedProxies 127.0.0.1 -DryRun   # 先看，不写盘
./tools/install-service.ps1 -Production -TrustedProxies 127.0.0.1
Start-ScheduledTask -TaskName Chispa.Server
Invoke-WebRequest http://127.0.0.1:5115/ready -UseBasicParsing              # 200 {"status":"ready"}
```

| 相关配置 | 说明 |
|---|---|
| `CHISPA_TRUSTED_PROXIES` | 只信任这些对端的转发头；不设 = 一个都不信（默认） |
| `CHISPA_PRODUCTION=1` | 管理/CSRF Cookie 强制 `Secure`；取消 loopback 明文访问后台（`/health`、`/ready` 除外） |
| `CHISPA_LOG_DIRECTORY` | 启用按天+按大小的轮转日志（`chispa-YYYYMMDD.log`） |
| `CHISPA_READINESS_CACHE_SECONDS` | `/ready` 结果缓存，默认 10 秒 |

- 崩溃恢复由看门狗负责（`tools/run-server-watchdog.ps1`，服务退出后 5 秒拉起）：实测 Task Scheduler 自带的"失败后重启"在本机被强杀的进程上**不生效**，所以没有依赖它。
- `./tools/uninstall-service.ps1` 会先写停止标志让服务正常退出，再注销任务；数据与日志不动。
- `./tools/health-check.ps1 -Url http://127.0.0.1:5115/ready -PublicUrl https://…/ready` 做可用性检查；**告警目的地是配置项**（`-AlertWebhook`），不配置就只在本地记录，不自行发送。
- `/ready` 只返回 `{"status":"ready"}` 或 `{"status":"not-ready"}`，不含版本、路径或原因；原因写日志。

验证记录见 `docs/task-04-deployment.md`：`python tests/proxy_matrix.py`（52 项：伪造转发头、真实来源与限流、生产 Cookie、就绪检查）、`powershell -File tests/recovery_check.ps1`（11 项：安装→启动→杀进程→2 秒自动恢复→卸载→再启动）。

## 后台安全、版本状态与下载恢复（任务 06–08）

- **查看密码**保留，但必须先重新输入管理员密码换一个短时（120 秒 / 最多 3 次）服务端授权；缺授权、过期、换人都是 403。失败 5 次/15 分钟触发 429。查看页面禁止缓存、30 秒自动清空，并写明截图无法收回。
- **审计**：`<数据目录>/admin-audit.log`，只记谁在何时看了哪个账号、结果与来源地址，不含密码/密文/响应正文。
- **后台访问限制**：`CHISPA_ADMIN_ALLOWED_NETWORKS=203.0.113.0/24,...`（转发头处理后的真实地址）；本机永远允许，配错不会自锁。
- **版本状态**：每个程序可设推荐版本；版本可"撤回下载"（已安装仍能启动）或"禁止启动"（下载与启动都停，运行中的程序下个心跳退出）。推荐版本被撤回/删除时自动回落到最高可用版本。
- **加载器升级提示**：`/api/v1/launcher/latest` 返回当前加载器版本与下载入口（`CHISPA_LAUNCHER_VERSION`、`CHISPA_LAUNCHER_DOWNLOAD_URL`、`CHISPA_LAUNCHER_NOTES`）；只接受 https 链接，登录页显示提示、由用户点击打开，不自动下载或执行。
- **下载恢复**：加载器对网络中断做有界重试并用 Range 断点续传，完成后仍校验大小与签名哈希；认证/权限/签名/禁止版本错误不重试。安装加锁，避免两个实例同时写同一目录。
- **限流**：登录、普通 API、上传分别有每分钟上限与请求体上限（8 KB / 64 MB），按来源地址计；过期会话与失败记录由维护定时器清理。详见 `docs/task-06-07-08-admin-versions-faults.md`。
## 备份与恢复（任务 05）

`./tools/backup-server.ps1 -Target E:\ChispaBackups -IncludePath .local\signing`（或双击 `BACKUP-NOW.cmd`）产出 `chispa-<时间>/`，内含：账号与权限、管理员记录、Data Protection 密钥环、全部发布包与签名、发布公钥，以及额外指定的目录（发布签名私钥）。

- **一致性**：复制前后各做一次快照，复制时逐文件校验哈希，最后对副本做自检（JSON 可解析、发布目录自洽、包哈希与清单一致）。自检不过就删除副本并失败，不会交付半份备份。
- **不在备份里**：`uploads/`、`logs/`、`.stage-*`、`.readiness-probe`。
- **访问限制**：备份目录默认只给当前账号 + SYSTEM + 管理员组；清单里不含口令/盐值/密文；脚本不联网，异地副本由人来做。
- **恢复**：`./tools/restore-backup.ps1 -Backup <备份目录> -Target <新目录> -AdminUser <管理员>` —— 只写新目录（已存在且非空直接拒绝），先按 SHA-256 校验整份备份，再复制，然后在目标目录上起临时实例验证登录/权限/下载，通过后才提示切换。损坏的备份在写入前就被拒绝。
- **DPAPI 边界**：同机同账号可完整恢复（含"查看密码"）；换机器/换账号时账号、权限、发布包照常，"查看密码"不可用（需要管理员重设密码重建密文）。详见 `docs/task-05-backup.md`。
- 备份频率建议：每天一次 + 每次发布后一次；最大损失 = 距上次备份的时间；实测 279.8 MB 备份 1.8 秒、恢复复制+校验 0.8 秒。
## 上线前还需要

当前加固保护的是正常加载器的授权与启动流程，不等于无法破解。拥有本机控制权的人仍可修改加载器、绕过检查或直接运行下载出的 EXE；启动前的文件校验也不是持续的运行时防篡改。不要将客户端内的检查当作不可绕过的信任边界。此类限制可参考 [Microsoft 客户端安全说明](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0)。

请先更新并重启服务器，再分发新客户端。新客户端需要服务端的 `/api/v1/programs/{programId}/releases/{version}/launch` 接口，断网、旧服务端或授权失效时不会启动已安装程序。旧客户端不会自动获得此检查；真正阻止直接运行靠的是被分发程序自身的授权（任务 02，见上），而**改动前已经分发出去的旧 EXE 仍然可以被直接运行**，只能靠停止发布、删除旧版本和用户更新来处理。

1. ~~用 HTTPS 部署服务端~~ —— 公网入口已由隧道提供 TLS（任务 04，见上文「部署与恢复」）。仍需：按正式参数安装服务（`./tools/install-service.ps1 -Production -TrustedProxies 127.0.0.1`），并通过 `publish-client.ps1 -ServerUrl` 把正式地址写进客户端。
2. 保管和备份 `.local/server/`；该目录包含账号、发布数据和后台会话密钥。
3. ~~给更新包做**独立签名**~~ —— 已完成（任务 03，见下文「发布签名」）。仍需给 Windows EXE 和加载器做**代码签名**：那是操作系统层面的信任，和这里的发布包签名是两件事。
4. ~~在 **Chispa EXE 内**添加授权校验~~ —— 已完成（任务 02，见上文「主程序内部授权」）。注意它约束的是新构建：改动前已分发的旧 EXE 仍可直接运行。
5. 增加加密下载包与正式安装器。加密包运行前仍需解密，因此不能保证 EXE 完全无法提取。

不要复用旧 Gamesense 项目的注入、固定账号、结束 Steam 和检测调试器的逻辑。
