# Chispa Server 1.0.4 — Windows x64

此包自带 .NET 运行时，无需安装 SDK。解压后先在该目录打开终端：

```
Chispa.Server.exe admin-create administrator
```

按提示设置至少 12 位管理员密码，然后双击 `START-SERVER.cmd`，打开
`http://127.0.0.1:5115/admin` 登录。首次安装没有任何预置账户或密码。

数据写入 `data/`。升级现有服务时，先备份原数据，再通过环境变量
`CHISPA_SERVER_DATA` 指向原数据目录。不要用空目录覆盖原账号、权限或密钥。

`release-trust.json` 仅为发布验签公钥，不是签名私钥；用于校验已有签名的软件包。
如使用自己的签名密钥，请替换为对应公钥。

公网部署需配置 HTTPS 反向代理或隧道，以及 `CHISPA_PRODUCTION`、
`CHISPA_TRUSTED_PROXIES` 等环境变量。完整部署、备份与恢复说明：
https://github.com/Guo-Yinchen/loader/blob/main/docs/task-04-deployment.md

此发行包不含账户数据库、管理员凭据、激活码记录或签名私钥。
