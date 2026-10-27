# 任务 05 交付说明：自动备份及可验证恢复

> 2026-10-26 验收修订：本文件保留初次交付记录，当前修复、测试范围和上线待办见 [验收简报](acceptance-review-2026-10-26.md)。

日期：2026-10-26。改动在 `D:\desktoop\loader`（两个脚本、一个演练、文档）。

## 0. 结论摘要

1. **备份什么**：账号（含权限、到期、DPAPI 密码密文）、管理员记录、Data Protection 密钥环、程序登记与**全部发布包及其签名**、发布公钥，以及可选的额外目录（发布**签名私钥**）。上传中转、轮转日志、`.stage-*` 发布中间目录不入备份。
2. **一致性不是靠"应该没问题"**：备份前后各做一次快照（大小+修改时间），复制时逐文件算源哈希、写完再算目标哈希，两次快照不一致就整份作废重试；最后再对副本做自检（JSON 可解析、每个发布目录 manifest/package/签名齐备且哈希一致）。**自检不过就删除副本并失败**，绝不交付半份备份。
3. **恢复只写新目录**：目标已存在且非空直接拒绝；复制前逐个文件按 manifest 的 SHA-256 校验，**损坏的备份在写入任何目录之前就被拒绝**；复制完成后在目标目录上起临时实例做真实验证（登录、权限、下载校验），通过才提示切换。
4. **DPAPI 的边界有实测证据**：同机同账号可以完整恢复（密钥环可解、密码密文可解、管理员登录与 Cookie 都可恢复）；换机器/换账号时账号登录、权限、发布包照常，**"查看密码"不可用**。本机无法制造第二个身份，用"替换密钥环"复现了同一故障（见 §4），并给出迁移办法。
5. 验收：`tests/backup_drill.py` **36/36**；真实数据实测：`.local/server`（279.8 MB / 15 个文件）备份 **1.8 秒**，恢复复制+校验 **0.8 秒**（`-SkipVerify`）。既有套件（smoke / signature / auth / proxy / recovery / launcher / publish）本轮全部复跑通过。

## 1. 备份范围

| 备份内 | 说明 |
|---|---|
| `users.json` | 账号、PBKDF2 哈希、到期时间、授权范围（accessMode/programIds）、Beta 标志、`passwordCiphertext`（DPAPI + Data Protection 双层） |
| `admin.json` | 管理员口令哈希与盐 |
| `keys/key-*.xml` | ASP.NET Data Protection 密钥环，**密钥材料本身由 DPAPI 保护**（`<encryptedKey>`） |
| `programs/<id>/program.json` | 程序登记（名称、入口 EXE、图标、Beta 归属） |
| `programs/<id>/releases/<v>/` | `package.zip`、`manifest.json`（含逐文件哈希）、`signature.json`（发布签名，任务 03） |
| `release-trust.json` | 发布签名校验公钥（不是秘密，但恢复后要能验签） |
| `extra/<目录名>/`（`-IncludePath`） | 例如 `.local/signing/`（发布签名**私钥**）。丢了就无法再签新版本，但客户端仍然信任旧签名 |

**不备份**：`uploads/`（上传中转，发布失败的半成品）、`logs/`（轮转日志，恢复后重建）、`.stage-*`（发布中间目录）、`.readiness-probe`、`*.tmp`。这些要么是瞬态，要么是"恢复了也没用"。

## 2. 一致性：三件事

1. **两遍快照**：复制前记录每个文件（相对路径, 大小, 修改时间）；复制后再记录一次。两次不一致 → 整份删除重试（默认 3 次），仍不稳定就失败。服务端的写入模式是"写临时文件再改名"或"整目录移动"，所以单文件不会半截；剩下的风险是**不同文件之间的时间差**（例如刚发布的 manifest 有了、package 还没写入），快照比较正是挡这个。
2. **逐文件哈希**：复制时算源哈希，写完再算目标哈希，不相等立即失败（挡磁盘/网络盘的坏拷贝）。
3. **副本自检**：JSON 全部可解析；每个发布目录必须 `manifest.json + package.zip + signature.json` 齐备；`package.zip` 的大小与 SHA-256 必须等于 manifest 里记录的值；`admin.json`、`keys/`、`users.json` 的存在性检查。**任何一项不过 → 删除这份副本、退出非零**。演练里专门造了一个"删掉 package.zip 但留下 manifest"的半发布状态，备份被正确拒绝。

## 3. 敏感内容与访问限制

- 备份里**没有明文口令**：账号口令只有 PBKDF2 哈希；"可解密的口令"是 DPAPI + Data Protection 双层密文，只有原机器的原 Windows 账号能解开。
- 备份清单 `backup-manifest.json` 只记录**文件名、大小、SHA-256、时间**，不记录任何口令、盐值或密文内容（演练里断言了这一点）。
- 备份目录默认收紧 ACL（`/inheritance:r`，只给当前账号 + `SYSTEM` + 管理员组，用 SID 写法避免中英文系统组名差异）。
- 目标在仓库工作区内时**默认拒绝**（`-AllowRepositoryTarget` 仅供测试）；备份脚本**不访问网络**，不会上传到任何地方——异地副本要人来做（移动硬盘/另一台机器/离线介质）。

## 4. DPAPI 绑定：实测到哪一步

代码事实：`server/Program.cs` 用 `AddDataProtection().PersistKeysToFileSystem(keys).ProtectKeysWithDpapi()`，默认作用域是 **CurrentUser**。密钥环文件是 XML，其中 `<encryptedKey>` 元素里就是 DPAPI blob（实测：`keys/key-658c972d-….xml`，1887 字节，98% 可打印，含 `encryptedKey`）。账号口令的 `passwordCiphertext` 同样由这个密钥环加密。

| 场景 | 账号登录 | 权限 / 发布包 / 下载校验 | 管理员登录（Cookie 票据） | 查看密码 |
|---|---|---|---|---|
| **同机同账号**（演练实跑） | ✅ | ✅ | ✅（密钥环可解） | ✅（密文可解） |
| **密钥环不匹配**（演练实跑：把 `keys/` 换成空目录，等价于异机/异账号） | ✅（PBKDF2 与 DPAPI 无关） | ✅ | ✅（Data Protection 自动生成新密钥环） | ❌（密文解不开） |
| **异机/异账号**（**未实测**，本机无法制造第二个身份） | 预期 ✅ | 预期 ✅ | 预期 ✅（重新登录；旧 Cookie 失效） | 预期 ❌ |

**因此不能宣称"复制 keys 文件夹即可异机恢复"**。可以负责地说：`keys/` 是**同机同账号**恢复的必要组成部分；跨机器时它解不开，Data Protection 会静默生成新密钥环，旧的密码密文随之失效。

**跨机器迁移的办法**（恢复后执行）：
1. 按 §5 恢复并验证。验证脚本会明确报出"密码密文可解密"这一项失败——这就是异机的信号。
2. 用管理员在后台对每个账号点"重设密码"（`POST /admin/users/{name}/password`）：新口令会用**新密钥环**重新加密，从此"查看密码"恢复可用。
3. 也可以不重设：账号登录、权限、发布包都不受影响，只是"查看密码"对这些账号不可用。
4. 旧机器还在时，可先在旧机器上用后台"查看密码"导出需要的口令，再在新机器上重设。

## 5. 恢复步骤

```powershell
# 1. 备份（双击 BACKUP-NOW.cmd 等价）
.\tools\backup-server.ps1 -Target E:\ChispaBackups -IncludePath .local\signing

# 2. 恢复到新目录并验证（口令从环境变量或交互输入，不写进命令行）
$env:CHISPA_RESTORE_ADMIN_PASSWORD = '<管理员密码>'
$env:CHISPA_RESTORE_USER_PASSWORD  = '<某个账号的密码>'
.\tools\restore-backup.ps1 -Backup E:\ChispaBackups\chispa-20261026-121500 -Target D:\restore-20261026 -AdminUser manager -UserAccount someuser
```

恢复脚本做的事：先验证全部数据与附加文件的安全路径、大小和哈希 → 在私有 ACL 目录中复制 → 起临时实例 → 验证 `/ready`、管理员登录、密码密文可解密、账号登录、授权范围、下载包的大小与哈希、至少一个发布包完整校验 → 额外目录恢复到独立目录 → 输出切换步骤。任何一项失败 → 非零退出，**生产数据没有被触碰**。

切换（人工，需要停服务）：

```powershell
./tools/uninstall-service.ps1                       # 阻止看门狗重启并停止任务；可能中断在途请求
Rename-Item D:\...\.local\server server.before-restore   # 保留，不删除
Move-Item D:\restore-20261026 D:\...\.local\server
./tools/install-service.ps1 -Production -TrustedProxies 127.0.0.1
Start-ScheduledTask -TaskName Chispa.Server
Invoke-WebRequest http://127.0.0.1:5115/ready -UseBasicParsing
```

发布签名私钥由 `-ExtraTarget`（默认 `<目标>-extras`）单独恢复，**不会**被放进数据目录——它不该跟着数据目录被打包或分发。

## 6. 频率、可接受损失与恢复时间

| 项 | 建议 / 实测 |
|---|---|
| 备份频率 | **每天一次**（凌晨低峰）+ **每次发布新版本后立即一次**（发布包是最难重建的部分）。用计划任务跑 `BACKUP-NOW.cmd` 或直接调脚本 |
| 最大可能数据损失（RPO） | 日常操作 = **距上次备份的时间**（建议 ≤ 24 小时）：期间新建的账号、改过的权限/到期、新上传的发布包会丢。发布包另外有 `publish-current.ps1` 的 `.local/releases/<版本>/` 原件和仓库构建产物，可重新发布补回 |
| 恢复时间（RTO） | 实测：279.8 MB / 15 文件备份 1.8 s；恢复复制+校验 0.8 s（`-SkipVerify`）。加上运行时验证（起实例、登录、下载校验）通常 **10–30 秒**。真实 RTO = 上述 + 人工切换（停服务、改目录、起服务，约 2–5 分钟） |
| 保留 | `-Keep 14`（默认）份，按目录时间倒序裁剪；超出后自动删除最旧的、且**只删带本脚本 manifest 的目录** |

计划任务示例（每天 03:10，注意任务计划里不要放口令）：

```powershell
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File "D:\desktoop\loader\tools\backup-server.ps1" -Target E:\ChispaBackups -IncludePath "D:\desktoop\loader\.local\signing"'
$trigger = New-ScheduledTaskTrigger -Daily -At 03:10
Register-ScheduledTask -TaskName 'Chispa.Backup' -Action $action -Trigger $trigger -RunLevel Limited
```

## 7. 验收：实际跑过的演练

`python tests/backup_drill.py`（临时目录、临时账号、临时签名密钥，不碰 `.local/server`）：

```
PASS 备份脚本执行成功 / 生成了一个备份目录
PASS 备份清单格式正确
PASS 备份包含账号、管理员、密钥环、发布包与发布签名
PASS 备份清单里没有口令或盐值
PASS 备份清单提示了 DPAPI 依赖
PASS 发布签名私钥被一起备份 / 签名私钥确实在备份目录里
PASS 原始数据已被破坏（用于演练）        ← 删掉 users.json、keys/、发布目录
PASS 恢复脚本成功完成
PASS 恢复验证包含「备份文件完整」「目标目录是新的」「能启动服务」
PASS 恢复验证包含「管理员能登录（Data Protection 密钥环可用）」
PASS 恢复验证包含「的密码密文可解密」
PASS 恢复验证包含「能登录（PBKDF2」「授权程序列表」「并通过大小与哈希校验」
PASS 恢复输出给出切换步骤
PASS 签名私钥恢复到数据目录之外的独立目录
PASS 恢复后的数据能独立启动服务
PASS 恢复后账号能用原密码登录
PASS 恢复后账号的授权范围仍然是单程序      ← 权限（accessMode/programIds）随备份完整
PASS 恢复后下载的发布包与清单哈希一致
PASS 恢复后发布包的签名仍然有效
PASS 换了密钥环后管理员仍能登录（口令是 PBKDF2，与 DPAPI 无关）
PASS 换了密钥环后密码密文无法解密（这就是异机恢复的边界）
PASS 换机器后账号照常登录、权限与发布包不受影响（本项只验证登录）
PASS 恢复没有改动原数据目录
PASS 损坏的备份被拒绝 / 被拒绝时没有留下可用的恢复目录
PASS 半发布状态的数据目录被拒绝备份
PASS 保留策略只保留最新 2 份
PASS 已存在且非空的目标被拒绝 / 被拒绝时原文件仍在
36/36 checks passed
```

另外对**真实数据**跑过一次（只读源目录，写到系统临时目录，事后清理）：`backup-server.ps1 -DataRoot .local/server -Target %TEMP%\chispa-backup-measure` → 15 个文件 / 279.8 MB / 1.8 秒，自检通过，报告"1 个账号的密码密文依赖 DPAPI"。

## 8. 未执行 / 已知边界

1. **异机、异账号恢复未实测**：本机没有第二个 Windows 身份可切；用"替换密钥环"复现了同一故障（§4），但没有真正把备份搬到另一台机器上验证过。**正式结论只在"同机同账号"这一档有实测支撑。**
2. **备份期间不停服务**：靠两遍快照 + 自检保证一致性，但如果备份时正好有人在连续发布，可能连续三次都失败（脚本会明确报错，不会交付半份）。建议把每日备份放在低峰，发布后另跑一次。
3. **异地副本没有自动化**：脚本只写本地目录；拷到移动硬盘/另一台机器/离线介质要人做（简报要求不擅自上传到外部服务）。
4. **备份未加密**（除 DPAPI 密文本身）：靠 NTFS ACL 保护。如果要把备份放到共享盘或移动硬盘，考虑 BitLocker/VeraCrypt 之类的卷加密——DPAPI 密文在异机本来也解不开，真正敏感的是发布包与账号结构。
5. **恢复不切换**：脚本只恢复到新目录并验证，切换是人工步骤（故意如此）。
6. **没有备份完整性巡检**：只有备份时与恢复时校验；长期存放的介质建议定期跑一次 `restore-backup.ps1 -SkipVerify -Target <临时空目录>` 做抽样校验。
7. **`-IncludePath` 会把私钥一起备份**：这正是目的，但也意味着备份目录的访问权限等于私钥的访问权限（已收紧 ACL，务必配合介质加密）。
