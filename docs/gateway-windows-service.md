# Windows 网关部署包

要求 Windows、本机固定 NTFS 目录、PowerShell 7.2 及以上。生成和预演不要求管理员；实际 SCM 安装、ACL 设置及卸载要求管理员终端。当前仅完成发布、配置及非提权验证，SCM 安装/启动/停止/自动恢复仍须在管理员环境验收。

## 生成包

在源码仓库执行，输出目录必须为空：

```powershell
pwsh -File .\tools\deployment\Build-GatewayPackage.ps1 -OutputPath D:\ScadaPackages\gateway-1 -Runtime win-x64
```

默认框架依赖部署，目标主机需要 .NET 10 **ASP.NET Core Runtime**；加 `-SelfContained` 可带运行时。支持 `win-x64` 和 `win-arm64`，每种架构须分别验收。包包含网关程序、安装/卸载脚本和本文 `INSTALL.md`。发布目录与数据目录必须独立，拒绝根目录、网络共享、重解析点以及旧数据目录的直接接管。

## 核对与安装

在中心“组态配置 → 采集网关”创建身份并下载密钥记录 JSON。它包含 `gatewayId`、`serverUrl`、`token`；通过受信渠道交给部署人员。核对 HTTPS 地址，下载记录和安装配置都须按凭据保管。

```powershell
# 先预演：不注册服务、不创建数据目录、不写密钥或 ACL。
pwsh -File D:\ScadaPackages\gateway-1\Install-GatewayService.ps1 `
  -ProgramPath D:\ScadaPackages\gateway-1 -DataPath D:\ScadaGatewayData\edge-1 `
  -IdentityFile D:\Private\edge-1-key.json -ServiceName ScadaGateway_Edge1 -WhatIf

# 在管理员终端执行；省略 -Start 时仅安装，随后可手动启动。
pwsh -File D:\ScadaPackages\gateway-1\Install-GatewayService.ps1 `
  -ProgramPath D:\ScadaPackages\gateway-1 -DataPath D:\ScadaGatewayData\edge-1 `
  -IdentityFile D:\Private\edge-1-key.json -ServiceName ScadaGateway_Edge1 -Start
```

服务以独立虚拟账户 `NT SERVICE\ScadaGateway_Edge1` 运行，不配置 LocalSystem 或交互用户口令。程序目录对服务只读，数据目录允许修改；ACL 仅授权 SYSTEM、Administrators 和该服务 SID。先限制目录权限，再写含密钥的 `gateway-service.json`；另有不含密钥的 `gateway-installation.json` 清单。安装不会清理下载的原始密钥记录，部署人员需自行安全保管或移除。

安装先以独占新建方式预留程序清单及数据目录的 `installation.json`，再写入受限密钥配置；并发安装不能覆盖另一安装的身份。预留文件不含密钥。失败后这些文件会保留，须先核对实际状态，不能把目录清空后重试。

实际部署应将已核对的包放入管理员控制的固定位置，例如 `C:\Program Files\ScadaGateway\edge-1`，并将示例中的 `ProgramPath` 一并替换；不要从普通用户可替换的源码/下载目录注册服务。脚本把选定程序和数据目录及程序成员的所有者设为 Administrators，但不会修改其父目录；祖先目录的替换权限须在部署环境核对。

SCM 命令行只含程序和 `--GatewayConfig=<绝对路径>`，不包含密钥。配置文件模式只读取指定文件中的 `GatewayAgent`，不采用机器环境中的其他网关身份或数据目录；文件缺失、格式错误或超过 64KB 时失败，不回退到环境身份。服务名称由该文件设置；控制台旧环境变量方式继续可用。

可用 `Scada.Gateway.Host.exe --GatewayConfig=<绝对路径> --ValidateConfig=true` 单独校验配置，成功返回 0、无效返回 1；预检不创建数据目录、不初始化队列，也不连接现场设备。安装脚本在注册 SCM 服务之前调用此预检。

安装设置延迟自动启动，并配置异常退出后的 5/15/60 秒重启，连续 24 小时无失败后重置计数；启用非崩溃失败恢复，宿主将捕获的致命错误退出码报告给 SCM。管理员安装时预先创建 Application 日志的 `Scada.Gateway.Host` 事件源，避免虚拟账户启动后无法创建日志源。收到中心撤权时保留进程的 Blocked 状态，不依靠重复重启绕过撤权。脚本不覆盖同名服务、安装配置或已有队列。安装中断后保留现场，先查服务和清单再处理，不直接重跑覆盖。

## 停止、改密与卸载

```powershell
Stop-Service ScadaGateway_Edge1
Get-Service ScadaGateway_Edge1
Start-Service ScadaGateway_Edge1
Restart-Service ScadaGateway_Edge1

pwsh -File D:\ScadaPackages\gateway-1\Uninstall-GatewayService.ps1 -ProgramPath D:\ScadaPackages\gateway-1 -WhatIf
pwsh -File D:\ScadaPackages\gateway-1\Uninstall-GatewayService.ps1 -ProgramPath D:\ScadaPackages\gateway-1
```

卸载先核对清单、服务路径和虚拟账户，正常停止并确认没有运行进程，再删除服务注册；程序、配置、密钥、PKI 和队列全部保留。它不强制结束未退出的现场 I/O，不自动删除目录，也不释放中心会话。数据仍须按原安装归属恢复，不能换个编号继续使用旧队列。

轮换中心密钥后，管理员先正常停止现场服务，确认进程已退出，再在原受限 `gateway-service.json` 内更新 `GatewayAgent.Token`，保持编号、中心地址、服务名与数据路径不变，然后启动并在线重新确认配置。不要将新密钥写入命令行或重新运行安装覆盖原目录。Data Protection 密钥按服务账户 DPAPI 保护；换账户/主机时不能假设原离线配置仍可解密，迁移与恢复见 `gateway-deployment.md`。

## 验证

源码中的 `Test-GatewayDeployment.ps1 -ProgramPath <发布目录>` 直接执行包内脚本，在普通用户终端验证预演无修改、DACL 构造/恢复、无效位置/旧目录/不安全中心地址拒绝、模拟 SCM 查询下卸载归属拒绝，以及实际控制台进程读取文件、忽略其他环境身份、错误输出不含密钥和崩溃重启保留数据流。它使用合成凭据和自己的临时目录，不注册或停止现场服务；非提权的 DACL 测试不改变测试目录所有者，不能替代管理员安装的权限验收。

管理员环境还需完成：查询 SCM 的账户/带引号路径/恢复策略；启动后核对新鲜 `status.json` 与中心配置确认；停止后确认实际驱动退出和队列保留；重启确认离线配置及 DPAPI 解密；强制终止**测试服务**确认 SCM 恢复；无管理员权限的其他用户不能读取密钥或改程序；最后验证卸载保留数据。控制台测试不能替代这些验收。

SCM 行为参考 Microsoft 的 [sc.exe create](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-create)、[虚拟服务账户](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/configure-windows-service-accounts-and-permissions) 与 [Service SID](https://learn.microsoft.com/en-us/sql/relational-databases/security/using-service-sids-to-grant-permissions-to-services-in-sql-server) 文档。
