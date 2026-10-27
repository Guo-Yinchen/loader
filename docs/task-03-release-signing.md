# 任务 03 交付说明：发布清单签名与客户端验签

> 2026-10-26 验收修订：本文件保留初次交付记录，当前修复、测试范围和上线待办见 [验收简报](acceptance-review-2026-10-26.md)。

日期：2026-10-26。改动集中在 `D:\desktoop\loader`（服务端、加载器、签名工具、测试）。主程序仓库本轮未改动。

## 0. 结论摘要

1. **签名覆盖的是清单原文，不是 JSON**。签名对象是一段固定格式的文本（格式版本 + 程序 ID + 版本 + 入口 EXE + 包大小 + 包 SHA-256 + 文件清单摘要），客户端用同一段字节做验签，然后逐字段比对服务端声称的值。因此不存在「JSON 重新序列化后签名对不上」这一类问题，也不允许字段顺序、空格、数字格式有任何自由度。
2. **私钥不在下载服务器里**。签名由独立工具 `tools/Chispa.Sign` 在发布环境执行；服务端只持有**公钥**，用于发布前自查。被攻陷的下载服务器既不能给任意包补签，也不能改写已经签名的包（改写会让签名失效）。
3. **客户端内置公钥**（`client/ReleaseTrust.cs`），支持多把、按 `keyId` 选择、可轮换；未知密钥、缺签名、签名错误、字段被替换一律**拒绝安装**，没有「警告后继续」。
4. 服务端把每文件哈希表也纳入了签名（`binariesSha256`），所以「被攻陷的服务器 + 用户磁盘上被改过的 DLL」这一组合也会被拦下——启动校验用的文件清单必须是签过名的那一份。
5. 验收：`tests/signature_matrix.py` **25/25**；既有 `smoke.py`（11 项）、`auth_matrix.py`（25/25）、`launcher_startup.py`、`publish_checks.py` 全部通过；本机真实发布链（`publish-current.ps1` 打包 93 MB → 签名 → 服务端校验 → 发布）与历史版本迁移（`sign-release` 给 1.0.0 / 1.0.1 补签）都实际跑过。

## 1. 审计：改动前的发布与清单逻辑

读 `server/Program.cs`、`server/AdminPages.cs`、`client/LauncherApi.cs`、`client/Installer.cs` 后确认：

| 项 | 改动前 |
|---|---|
| 发布 | `Store.Publish()`（目录→ZIP）/ `PublishArchiveAsync`（表单上传）/ `PublishArchiveFileAsync`（分块上传）三条路，都会**自动规范化** ZIP（去外层文件夹、去 `__MACOSX`/`Thumbs.db`/`desktop.ini`、反斜杠转正斜杠） |
| 清单 | `releases/<version>/manifest.json`：`programId / version / executable / sha256 / size`，无签名、无每文件哈希 |
| 下载校验 | 客户端比对 `size` 与 `SHA-256`，两者都来自同一台服务器 |
| 启动校验 | `/launch` 临时从 ZIP 里算出每文件 SHA-256 返回，客户端比对本地文件 |
| 信任根 | 无。服务器说的就是全部——服务器被替换，客户端无从分辨 |

缺的正是本任务要补的三件事：**清单的独立可信来源**（签名）、**每文件哈希的可信绑定**、**密钥的归属边界**（私钥不进下载服务）。

## 2. 签名覆盖的字节与格式版本

签名对象（`shared/ReleaseSignature.cs::ReleaseStatement.Build`），LF 分隔、无尾随换行、字段顺序固定：

```
chispa-release-v1
programId=chispa
version=1.2.3
executable=Wardogs.exe
size=93402304
sha256=123392e2…8b85f
binariesSha256=15711e28…7f84
```

- 第一行是**格式版本**。将来若改格式就是 `chispa-release-v2`；只认 v1 的客户端会拒绝 v2，而不是猜着解析。
- `binariesSha256` = 每文件哈希表的规范文本（按名称排序、`名字=小写哈希`、LF 连接）的 SHA-256。它把「启动时要校验哪些文件、每个文件应该是什么哈希」固定在签名里。
- 解析同样是严格的：恰好 7 行、标签与顺序完全一致、大小写与位数校验；任何偏差都是「不支持的签名清单格式」而不是宽容解析。
- **为什么不是 JSON**：签名覆盖字节而非语义。若签名对象是 JSON，客户端必须重新序列化才能验签，而不同序列化器在属性顺序、空白、数字格式、转义上有各自的自由度——两个写法的字节不同但语义相同，验签要么误拒，要么被迫先「规范化」再比对，而规范化本身就是绕过点。固定行格式没有这些自由度。

签名算法：**RSA-2048 / PKCS#1 v1.5 / SHA-256**，实现用 `System.Security.Cryptography.RSA`（`SignData`/`VerifyData` + `RSASignaturePadding.Pkcs1`）。不自创算法，也不引入第三方密码库。

信封（`chispa-release-signature-v1`，JSON，字段名固定在共享代码里）：

```json
{"format":"chispa-release-signature-v1","keyId":"release-dev-2026-10",
 "algorithm":"RSA-2048-PKCS1-SHA256","statement":"<上面那段原文>","signature":"<base64>"}
```

API 里以 **base64(信封 JSON)** 作为 `Release.signature` 传输：一个不透明字符串，避免嵌套 JSON 的序列化差异；`signatureKeyId` 只用于显示，客户端不会据此决定信任。

## 3. 信任模型与密钥

| 位置 | 持有什么 | 说明 |
|---|---|---|
| 发布环境（打包机） | **私钥** `release-private.pem`（PKCS#8，可用密码文件加密） | 只在 `chispa-sign sign` 时使用；不进 Git、不进分发包、不放在下载服务器 |
| 下载服务器 | 公钥（`CHISPA_RELEASE_TRUST_KEYS` 或 `<dataRoot>/release-trust.json`） | 只用于发布前自查，不能签名 |
| 加载器（客户端） | 公钥，**编译进 EXE**（`client/ReleaseTrust.cs`） | 真正的信任锚：从服务器或旁边的文件读信任锚等于不设防 |
| 签名工具 | 私钥路径、公钥文件 | `tools/Chispa.Sign`，不被打包进任何分发产物 |

拒绝策略（`ReleaseSignature.TryOpen` + `Matches`，全部是拒绝，没有降级）：

| 情况 | 客户端结果 |
|---|---|
| 没有 `signature` | `这个版本没有签名，已拒绝安装` |
| `keyId` 不在内置列表 | `签名密钥不受信任：<id>` |
| 信封格式/算法不认 | `签名格式或算法不受支持` |
| 签名不匹配原文 | `签名校验失败，内容可能已被替换` |
| 原文里的程序 ID / 版本 / 入口 EXE / 包大小 / 包哈希与收到的清单不一致 | `签名里的…与清单不一致` |
| 每文件哈希表摘要与 `binariesSha256` 不一致 | `文件清单与签名不一致，已拒绝安装或启动。` |

**轮换**：`ReleaseTrust` 是数组，可以同时放多把公钥；顺序是先让新客户端同时信任新旧两把 → 用新密钥签新版本 → 之后在客户端里删掉旧公钥（被删掉的立刻不再被接受）。删公钥是「退役一把密钥」的唯一手段，也因此是客户端发布才有的事。

## 4. 服务端

**校验顺序（`server/SignedPublish.cs`）**：

1. 信封能否解析、格式/算法是否受支持、`keyId` 是否在信任列表里、签名是否匹配原文（`ReleaseSignature.TryOpen`）；
2. 原文里的 `programId / version / executable / size / sha256` 是否等于**服务器自己从上传文件算出来的**值（`Matches`）——以磁盘上的字节为准，不以签名命令的参数为准；
3. `binariesSha256` 是否等于服务器从包内各可执行文件算出的哈希表摘要；
4. 布局检查（入口 EXE 在顶层、无 `..`/盘符/绝对路径、无重复路径、无系统垃圾文件、没有外层包装文件夹）；
5. 全部通过才落盘：`package.zip`（逐字节原样）、`signature.json`（信封原文）、`manifest.json`（含 `binaries` 与 `signatureKeyId`）。

**不再规范化**。这是本轮的行为变化：以前服务器会「帮你修好」包的布局，现在签名覆盖字节，修一次就废一次签名。因此需要规范化的包会被明确拒绝并提示用 `chispa-sign pack` 重新打包（`服务器不再改写已签名的包`）。附带好处是「服务器上的包」与「发布环境签的包」永远逐字节相同。

**接口与命令变化**：

| 位置 | 变化 |
|---|---|
| `Release` DTO | 新增 `signature`（base64 信封）与 `signatureKeyId`（仅显示）；`binaries` 现在在发布时算好并持久化，`/launch` 不再每次重算（旧发布无 `binaries` 时仍会重算） |
| CLI | `publish <id> <version> <package.zip> <signature.json>`（只接受签名包）、新增 `sign-release <id> <version> <signature.json>`（迁移）、新增 `trust-keys`（打印当前信任公钥） |
| `POST /admin/uploads` | 起始请求新增 `signature` 字段（信封 JSON 文本，≤64 KB）；没有它直接 400 |
| `POST /admin/uploads/{id}/complete` | 用会话里携带的签名发布 |
| `POST /admin/releases`（无 JS 回退） | 新增必填文件字段 `signature` |
| 后台发布页 | 增加「发布签名 signature.json」文件项；提示改为「必须由发布环境签名」 |
| 错误呈现 | `InvalidDataException` 的文案现在直接展示（以前统一显示「ZIP 文件无法读取或格式无效」，会把签名错误说成 ZIP 错误） |

存储布局（不变的部分也列出，便于核对）：

```
<dataRoot>/programs/<id>/releases/<version>/
    package.zip        # 逐字节原样，签名覆盖它
    signature.json     # 信封原文
    manifest.json      # programId/version/executable/sha256/size/binaries/signatureKeyId
```

`GetRelease` 在读取时把 `signature.json` 合进 DTO；没有签名文件的发布仍会被**下发**，但客户端会拒绝安装（旧的、未迁移的版本就处在这个状态）。

## 5. 客户端

| 时机 | 检查 | 位置 |
|---|---|---|
| 安装前（下载任何字节之前） | 验签 + 字段比对；这样后面用于比对下载内容的 SHA-256 就是签过名的那个 | `Installer.InstallAsync` |
| 下载中/后 | 大小与 SHA-256（原有，保留） | `Installer.InstallAsync` |
| 每次启动前 | 验签 + 字段比对 + **文件清单摘要比对**，再逐文件比对本地二进制 | `Installer.VerifyInstalledAsync` |

`Installer()` 无参构造使用 `ReleaseTrust.Keys`（编译进客户端的公钥）；另有一个 `Installer(IReadOnlyList<TrustedReleaseKey>)` 供测试用。**没有任何环境变量或文件能改变生产客户端的信任锚**——测试通过自己的入口点注入临时密钥，而不是让被测程序读一个可被替换的配置。

## 6. 修改文件清单

新增：

| 文件 | 作用 |
|---|---|
| `shared/ReleaseSignature.cs` | 链路核心：清单原文构造/严格解析、信封签名/验签、字段比对、文件哈希表摘要、信任密钥文件读写。服务端、客户端、签名工具**共用这一份**（各自 `<Compile Include="../shared/…">`），避免三处实现对「签名覆盖什么」产生分歧 |
| `tools/Chispa.Sign/` | 发布环境工具：`keygen` / `pack` / `sign` / `verify` |
| `server/SignedPublish.cs` | 签名发布路径：校验顺序、包描述（大小/哈希/每文件哈希）、布局规则 |
| `client/ReleaseTrust.cs` | 内置公钥（信任锚）与轮换说明 |
| `tests/signature_matrix.py` | 验收矩阵（25 项，含密钥轮换） |
| `docs/task-03-release-signing.md` | 本文 |

修改：`server/Program.cs`（`Release` DTO、发布路径、CLI、`GetRelease` 合并签名）、`server/AdminPages.cs`（签名上传、错误文案）、`server/Ui/app.js`（把签名文件带进上传起始请求）、`client/LauncherApi.cs`（DTO 字段）、`client/Installer.cs`（两处验签）、`client/Chispa.Launcher.csproj` 与 `server/Chispa.Server.csproj`（共享源文件）、`build.ps1`（构建签名工具）、`publish-current.ps1`（打包+签名+发布、`-InstallTrustKey`）、`tests/Program.cs`（`--verify-release`、`--install-check`、信任密钥注入）、`tests/smoke.py` 与 `tests/auth_matrix.py`（改为签名发布）、`README.md`。

## 7. 兼容与迁移

**部署顺序**：服务端 → 构建并发布带签名的包 → 分发新加载器。服务端先升级不会影响旧客户端（它们只是不验签）；新客户端需要服务端提供 `signature` 字段（同一个服务端版本提供）。

| 组合 | 结果 |
|---|---|
| 新服务端 + 新客户端 + 已签名包 | 正常安装与启动 |
| 新服务端 + 新客户端 + **旧（未签名）包** | 拒绝安装，提示「这个版本没有签名」→ 用 `sign-release` 补签后即可 |
| 新服务端 + **旧客户端**（无验签代码）+ 任意包 | 旧客户端照旧安装——它没有验签逻辑，**签名不能回溯约束它**。处置：升级客户端；或停止发布旧版本 |
| 旧服务端 + 新客户端 | `/latest` 不带 `signature` → 拒绝安装（fail closed）。所以**不要只升级客户端** |
| 客户端 + 未配置信任公钥的服务端 | 发布时报错并拒绝落盘（fail closed） |

**历史版本迁移（已实际执行）**：

```powershell
# 1. 对已发布的包就地签名（不重新打包，不改变包字节）
dotnet tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll sign `
  --package .local/server/programs/chispa/releases/1.0.1/package.zip `
  --program chispa --version 1.0.1 --executable Wardogs.exe `
  --key .local/signing/release-private.pem --key-id release-dev-2026-10 `
  --out .local/releases/1.0.1/signature.json

# 2. 让服务端校验并写入 manifest（同时补上旧 manifest 没有的文件清单）
$env:CHISPA_SERVER_DATA = (Join-Path (Get-Location) '.local/server')
$env:CHISPA_RELEASE_TRUST_KEYS = (Join-Path (Get-Location) '.local/signing/release-public.json')
dotnet .local/server-builds/<构建ID>/Chispa.Server.dll sign-release chispa 1.0.1 .local/releases/1.0.1/signature.json
```

本轮在本机 `.local/server` 上实际执行：`1.0.0`、`1.0.1` 补签成功；`1.2.3` 走完整 `publish-current.ps1`（打包 23 个文件 / 93,402,304 字节 → 签名 → 服务端校验 → 发布）。

## 8. 验收：命令与结果

```powershell
./build.ps1                                   # 服务端 / 客户端 / 签名工具，均 0 警告 0 错误
dotnet build tests/Chispa.Smoke.csproj -c Release
python tests/signature_matrix.py              # 25/25 PASS
python tests/smoke.py                         # 11 项 PASS
python tests/auth_matrix.py                   # 25/25 PASS（任务 02 回归）
python tests/launcher_startup.py              # PASS
python tests/publish_checks.py                # ALL PASS
```

`signature_matrix.py` 覆盖的内容（对应简报的验收清单）：

| 场景 | 期望 | 实现方式 |
|---|---|---|
| 合法包可安装 | 安装成功 | 逐字节签署后 `Chispa.Smoke --install-check` |
| 修改包 | 拒绝 | 服务端存放的 `package.zip` 换成另一份，并同步改 manifest 的 size/sha256 |
| 修改清单字段 | 拒绝 | 客户端白盒：程序 ID / 版本 / 入口 EXE / 大小 / 哈希逐项替换；端到端：只改 manifest |
| 替换程序 ID | 拒绝 | 客户端白盒（签名原文 programId=helper）；服务端：用 helper 的签名发布为 chispa |
| 未知密钥 | 拒绝 | 第二把专用临时密钥签名（客户端不认识；服务端也拒绝发布） |
| 缺失签名 | 拒绝 | 删除 `signature.json` |
| 篡改签名 | 拒绝 | 翻转签名字节 |
| 替换每文件哈希表 | 拒绝 | 清单里 `binaries` 换成一份假哈希 |
| 未配置信任公钥时发布 | 拒绝 | 去掉 `CHISPA_RELEASE_TRUST_KEYS` 后发布 |
| 历史版本迁移 | 补签后可安装 | 删签名 → 拒绝 → `sign-release` → 安装成功 |
| 密钥轮换 | 双公钥客户端接受新密钥；旧客户端拒绝 | 生成第二把密钥、双钥信任文件验签、旧信任文件验签 |

测试使用的都是**每次运行新生成的临时密钥**（`keygen` 到临时目录），没有任何测试密钥进入仓库。

**未执行的验证**（不要当作已通过）：

- 浏览器里的后台上传（JS 字段改动）——HTTP 层（multipart 与分块两条路径）已由 `smoke.py`/矩阵覆盖，但没有在真实浏览器里点过。
- 旧客户端（任务 02 之前/03 之前的加载器二进制）的行为——只做了代码层面推断，没有旧产物可测。
- 生产部署、EXE 代码签名（Windows Authenticode 是另一件事，本轮不做）。
- 高并发发布、超大包（>1 GB）签名耗时。

## 9. 已知边界

1. **客户端二进制仍可被修改**：有能力者可以改掉验签调用或替换内置公钥再重新打包。签名保护的是「诚实客户端」和「诚实分发的包」，不是抗篡改运行时。
2. **私钥的保管就是全部安全性**：私钥泄露 = 攻击者可以签任何包。工具支持加密私钥（`--password-file`），私钥文件权限、备份与发布机的隔离需要运维落实。
3. **服务端仍可拒绝服务、可回放旧包**：它不能伪造新包，但可以让用户装到旧版本（客户端会装，只要那个版本签名有效）。若要防回滚，需要在签名里加单调版本或撤销列表（未实现）。
4. **旧客户端不受约束**：没有验签代码的产物无法被服务端「教会」验签，只能停发或让用户升级。
5. **版本号不可重复**：发布目录已存在即拒绝，所以同一版本无法用新包顶替（也不应该）。
6. **ZIP 字节不确定性**：工具打出的 ZIP 含时间戳，同一目录两次打包字节不同。这不影响正确性（签的就是实际字节），但意味着「重建同一个包再比对签名」不是有效操作，请保留 `package.zip` 原件。
7. **信任锚更新需要重新分发客户端**：这是设计取舍，不是遗漏。

## 10. 回退

- 回退客户端：分发上一版加载器即可（它不验签）；服务端保持新版本，两类客户端都能工作。
- 回退服务端：新客户端会因 `/latest` 无 `signature` 而拒绝安装，因此回退服务端前必须先回退/停发新客户端。
- 某个版本要撤回：删除 `releases/<version>`（发布包与签名一起消失），客户端`/latest` 会拿到别的版本。
- 密钥事故（私钥丢失/泄露）：生成新密钥对 → 更新 `client/ReleaseTrust.cs` 并发布客户端 → 用新密钥重签在架的版本（`sign-release`）→ 旧客户端在新客户端铺开前会拒绝新签名，因此这一步要配合分发节奏。
