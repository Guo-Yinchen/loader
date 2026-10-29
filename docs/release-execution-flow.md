# 发布执行流（Wardogs → 加载器）

一条从「主程序已构建」到「正式分发目录可对外」的可重复执行流。每一步都给命令、可判定的成功输出、以及失败时的处置。以 `1.2.6` 为当前实例，换版本时只改 `$VERSION`。

发布环境约定（不要改）：

| 东西 | 位置 | 性质 |
|---|---|---|
| 签名私钥 | `.local/signing/release-private.pem` | 只留发布机，不进 Git、不进分发包、不上下载服务器 |
| 签名公钥 | `.local/signing/release-public.json` | 服务端自查用，`.local/server/release-trust.json` 是它的副本 |
| 客户端信任锚 | `client/ReleaseTrust.cs` 的 `BuiltIn` 数组 | 只在轮换密钥时才改，改完要重编客户端 |
| 打包/签名工具 | `tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll` | 由根 `build.ps1` 产出 |

前提：PowerShell 执行策略放开（本机全部作用域为 `Undefined`，等于 `Restricted`）：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

---

## 0. 变量

```powershell
$VERSION   = '1.2.6'
$PROGRAM   = 'chispa'
$EXE       = 'Wardogs.exe'
$KEYID     = 'release-dev-2026-10'
$LOADER    = 'D:\desktoop\loader'
$MAIN      = 'D:\desktoop\wardogs chispa'
$BUILD     = Join-Path $MAIN 'bin\x64\Wardogs'
$SERVERURL = 'https://teamviewer.rm7lwjwtg.nyat.app:24154'   # release/release.json 的 production.serverUrl
```

---

## 1. 构建戳核验（主程序侧）

```powershell
cd $MAIN
& (Join-Path $BUILD $EXE) --build-info
& (Join-Path $BUILD $EXE) --auth-build
```

必须同时满足，否则停在这里：

| 断言 | 期望 |
|---|---|
| `flavor` / `configuration` | `Production` / `Release` |
| `licenseRequired` / `diagnostics` | `true` / `false` |
| `version` | 等于 `$VERSION` |
| `--auth-build` 第一行 | `chispa $VERSION -> $SERVERURL` |

任何一个不符 → 回主程序重编，不要靠改参数绕过：

```powershell
cd $MAIN
.\build.ps1 -Version $VERSION -Flavor Production -Configuration Release
```

`build.ps1` 末尾会自己回读 `--auth-build` / `--build-info` 并打印 `Built: chispa $VERSION -> ...` 与 `Artifact: ...\bin\x64\Wardogs\Wardogs.exe`。

## 2. 占用与残留检查（加载器侧）

```powershell
cd $LOADER
Test-Path ".local\server\programs\$PROGRAM\releases\$VERSION"   # 必须 False：服务端不接受覆盖已发布版本
Test-Path ".local\staging\$VERSION"                            # 必须 False：publish-current.ps1 遇到已存在暂存目录会直接抛错
Test-Path '.local\signing\release-private.pem'                 # True
Test-Path '.local\server\release-trust.json'                   # True
Test-Path 'tools\Chispa.Sign\bin\Release\net10.0\Chispa.Sign.dll'  # True
```

`releases\$VERSION` 为 True 说明这个版本号已经上线，只能换新版本号重编；`staging\$VERSION` 为 True 说明上一轮中断过，确认它只是暂存（不含唯一副本）后再删掉重跑。

## 3. 打包 + 签名 + 登记 + 发布（一条命令）

```powershell
cd $LOADER
.\publish-current.ps1 -Version $VERSION
```

它按顺序做四件事，任何一步失败都不会继续：

1. **暂存**：只从 `$BUILD` 复制白名单文件到 `.local\staging\$VERSION`（`Wardogs.exe`、`*.dll`、`info.db`、`README.txt`、`LICENSE`、`FTDI-*`、`CHECK-RUNTIME.cmd`、`assets\`、`terrain-packs\`），并校验运行库齐全 —— 所以 `.pdb`、`.ilk`、`intermediate\`、`logs\`、`Symbols\` 不会进包。
2. **打包**：`chispa-sign pack` 生成 `.local\releases\$VERSION\package.zip`（入口 EXE 在顶层、去系统垃圾文件、拒绝非法路径）。
3. **签名**：`chispa-sign sign` 生成 `.local\releases\$VERSION\signature.json`，覆盖程序 ID、版本、入口 EXE、包大小、包 SHA-256、可执行文件清单摘要，并当场自检。
4. **发布**：`run-local.ps1 publish` 校验签名与包一致（服务端信任公钥）后原子落盘到 `.local\server\programs\$PROGRAM\releases\$VERSION\`。

成功输出形如：

```
Authorization stamp verified: chispa 1.2.6 -> https://teamviewer.rm7lwjwtg.nyat.app:24154
Prepared D:\desktoop\loader\.local\staging\1.2.6
已打包 23 个文件：D:\desktoop\loader\.local\releases\1.2.6\package.zip（93,xxx,xxx 字节）
SHA-256 <hex>
已签名 chispa 1.2.6：D:\desktoop\loader\.local\releases\1.2.6\signature.json
  keyId      release-dev-2026-10
  size       <字节数>
  sha256     <hex>
  binaries   <hex>
Program registered.
Published chispa 1.2.6 (<字节数> bytes, SHA-256 <hex>, keyId release-dev-2026-10).
```

## 4. 落盘核验（只读，必须全过）

```powershell
cd $LOADER
$dir = ".local\server\programs\$PROGRAM\releases\$VERSION"

# 4.1 三件套齐备
Get-ChildItem $dir | Select-Object Name, Length

# 4.2 服务端副本签名有效（和客户端/服务端同一套校验）
.\.tooling\dotnet\dotnet.exe tools\Chispa.Sign\bin\Release\net10.0\Chispa.Sign.dll verify `
    --package "$dir\package.zip" --signature "$dir\signature.json" `
    --trust .local\signing\release-public.json

# 4.3 manifest 与磁盘实测一致
$m = Get-Content -Raw "$dir\manifest.json" | ConvertFrom-Json
$z = Get-Item "$dir\package.zip"
[pscustomobject]@{
  VersionMatches = $m.version -eq $VERSION
  SizeMatches    = $m.size -eq $z.Length
  ShaMatches     = $m.sha256 -eq (Get-FileHash $z.FullName -Algorithm SHA256).Hash.ToLower()
  KeyId          = $m.signatureKeyId
  Status         = $m.status
}

# 4.4 服务端确实会把它当成最新版（无 recommendedVersion 时取最高 active）
Get-Content -Raw ".local\server\programs\$PROGRAM\program.json" | ConvertFrom-Json |
    Select-Object id, recommendedVersion, isBeta
```

`manifest.json` 里的 `"signature": null`、`program.json` 里的 `"hasRelease": false` 都是正常现象：前者由 `GetRelease` 读取时把 `signature.json` 合进 DTO，后者是按 `Latest(id)` 现算的展示字段。

服务在跑时可确认健康度：`Invoke-WebRequest http://127.0.0.1:5115/health -UseBasicParsing` → `{"status":"ok"}`（`/api/v1/*` 未登录返回 401 属正常鉴权）。

## 5. 正式分发目录同步

只在确认 1.2.6 要对外时才做；`dist/` 里的包、签名、哈希都不可再变。

```powershell
cd $LOADER
$dist = 'dist\software'
Copy-Item ".local\releases\$VERSION\package.zip"    (Join-Path $dist "Wardogs-$VERSION.zip")
Copy-Item ".local\releases\$VERSION\signature.json" (Join-Path $dist 'signature.json') -Force

# 哈希清单：只列当前包的 zip 与 signature.json 两条，顺序固定
$zip = Join-Path $dist "Wardogs-$VERSION.zip"
@(
  "$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())  Wardogs-$VERSION.zip"
  "$((Get-FileHash (Join-Path $dist 'signature.json') -Algorithm SHA256).Hash.ToLower())  signature.json"
) | Set-Content (Join-Path $dist 'SHA256SUMS.txt')

Get-Content (Join-Path $dist 'SHA256SUMS.txt')
```

离线副本只留当前和上一稳定版：本次是 1.2.6 与 1.2.5；`Wardogs-1.2.4.zip` 及其旧签名在确认可恢复后再删。

## 6. 文档与清理

```powershell
cd $LOADER
# 版本表：软件一行改成当前版本与包名
Select-String -Path docs\workspace.md -Pattern 'Wardogs-'

# 暂存目录可留可删（打包产物已在 .local\releases）
# Remove-Item -Recurse -Force ".local\staging\$VERSION"

# 上一轮误签的死文件（手工压的 1.2.4，版本已被占用，无发布价值）
Test-Path '.local\releases\1.2.4\signature.json'
```

`docs/workspace.md` 的「软件」行要同步成 `1.2.6` / `dist/software/Wardogs-1.2.6.zip` / 对应 release tag。

## 7. 失败分支

| 现象 | 原因 | 处置 |
|---|---|---|
| `running scripts is disabled` | 执行策略 `Restricted` | 先 `Set-ExecutionPolicy -Scope Process Bypass`，或 `powershell -ExecutionPolicy Bypass -File .\publish-current.ps1 -Version $VERSION` |
| `Wardogs.exe is stamped 1.2.5, but you are publishing 1.2.6` | 编译戳与发布版本不一致 | 回主程序 `.\build.ps1 -Version $VERSION` 重编 |
| `Publishing refused: only a licensed Production build...` | 不是 Production/Release，或诊断开着 | 用 `-Flavor Production -Configuration Release` 重编 |
| `The program server address differs from the ... profile` | EXE 里编译的服务器地址与 `release/release.json` 不符 | 用 `-ServerUrl $SERVERURL` 重编 |
| `Staging directory already exists` | 上轮中断留下 `.local\staging\$VERSION` | 确认无唯一数据后删目录重跑 |
| `这个版本已经发布` | 该版本已在服务端落盘 | 不能覆盖：换新版本号重编重发 |
| `签名与包内容不一致` / `签名校验失败` | ZIP 被重新打包或改过，签名绑定的是旧字节 | 重新 `pack` → `sign`，不要手改 ZIP |
| `签名密钥不受信任` | 服务端信任文件里没有这个 `keyId` | 检查 `.local/server/release-trust.json` 与签名用的 `--key-id` |
| `缺少发布签名文件` | 只传了 ZIP | ZIP 与 `signature.json` 必须成对提供 |

## 8. 一次跑完（1.2.6）

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
$VERSION   = '1.2.6'
$MAIN      = 'D:\desktoop\wardogs chispa'
$BUILD     = Join-Path $MAIN 'bin\x64\Wardogs'
$LOADER    = 'D:\desktoop\loader'
$PROGRAM   = 'chispa'
$SERVERURL = 'https://teamviewer.rm7lwjwtg.nyat.app:24154'

# 1. 构建戳
& (Join-Path $BUILD 'Wardogs.exe') --auth-build
& (Join-Path $BUILD 'Wardogs.exe') --build-info

# 2. 占用检查（两条都必须 False）
Test-Path "$LOADER\.local\server\programs\$PROGRAM\releases\$VERSION"
Test-Path "$LOADER\.local\staging\$VERSION"

# 3. 打包 + 签名 + 发布
cd $LOADER
.\publish-current.ps1 -Version $VERSION

# 4. 核验
$dir = ".local\server\programs\$PROGRAM\releases\$VERSION"
.\.tooling\dotnet\dotnet.exe tools\Chispa.Sign\bin\Release\net10.0\Chispa.Sign.dll verify `
    --package "$dir\package.zip" --signature "$dir\signature.json" `
    --trust .local\signing\release-public.json

# 5. 分发目录
$dist = 'dist\software'
Copy-Item ".local\releases\$VERSION\package.zip"    (Join-Path $dist "Wardogs-$VERSION.zip")
Copy-Item ".local\releases\$VERSION\signature.json" (Join-Path $dist 'signature.json') -Force
@(
  "$((Get-FileHash (Join-Path $dist "Wardogs-$VERSION.zip") -Algorithm SHA256).Hash.ToLower())  Wardogs-$VERSION.zip"
  "$((Get-FileHash (Join-Path $dist 'signature.json') -Algorithm SHA256).Hash.ToLower())  signature.json"
) | Set-Content (Join-Path $dist 'SHA256SUMS.txt')
Get-Content (Join-Path $dist 'SHA256SUMS.txt')
```

---

## 手工路径（不走 publish-current.ps1 时）

已有一个 ZIP、只想补 `signature.json`：

```powershell
cd D:\desktoop\loader
.\.tooling\dotnet\dotnet.exe tools\Chispa.Sign\bin\Release\net10.0\Chispa.Sign.dll sign `
    --package D:\desktoop\Wardogs.zip --program chispa --version 1.2.6 --executable Wardogs.exe `
    --key .local\signing\release-private.pem --key-id release-dev-2026-10 `
    --out .local\releases\1.2.6\signature.json

.\.tooling\dotnet\dotnet.exe tools\Chispa.Sign\bin\Release\net10.0\Chispa.Sign.dll verify `
    --package D:\desktoop\Wardogs.zip --signature .local\releases\1.2.6\signature.json `
    --trust .local\signing\release-public.json
```

签名没有默认输出路径，写哪里由 `--out` 决定；`sign` 成功时最后一行会打印绝对路径。签名覆盖 ZIP 字节，**先打包后签名**，任何重新压缩都要重签。

发布：

```powershell
./run-local.ps1 publish chispa 1.2.6 D:\desktoop\Wardogs.zip .local\releases\1.2.6\signature.json
```

或管理后台 `http://127.0.0.1:5115/admin` 发布页同时选 ZIP 与 `signature.json`；后台要求版本号与已发布内容不冲突。
