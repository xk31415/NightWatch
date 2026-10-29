# NightWatch 部署记录

日期：2026-10-25。目标：阿里云 Windows Server（`<服务器公网 IP>`，入库时已脱敏）。

> **2026-10-27 23:53 已升级至 v1.4.1（发行版直拉，经 RDP 手工执行）**：钉钉/企微告警版式修正——主机、IP、告警项由三个独立引用块合并为单个连续竖条。SHA256 `350f1bab...0abec4` 与发布说明一致；`ProductVersion` 确认 `1.0.0+f1485cf`。包内无 `appsettings.json`。**升级过程中 ACL 一步静默失败，导致服务反复报「错误 5 拒绝访问」，最终以 `icacls /reset /T /C` 恢复后启动成功**，详见「2026-10-27 升级 v1.4.1 记录」。
>
> **2026-10-26 11:29 已升级至 v1.4.0（发行版直拉）**：告警带主机名与 IP、CPU 采样改历史差值、钉钉排版修复、Linux 支持。SHA256 `b8724bff...c5981f` 与发布说明一致，包内无 `appsettings.json`。生产配置原样沿用（无需修改），升级后 23 个条目（21 个程序文件 + `appsettings.json` + `logs`），心跳 200，横幅「通知通道 2」。旧版目录保留在 `C:\AppData\NightWatch-old` 作为回滚副本。
>
> **2026-10-25 21:07 已升级至 v1.2.0（发行版直拉，未本地打包）**：新增监控/告警时段、publish 排除本地配置（守卫 `EnsureNoLocalConfigInPublish`）。单测 144 全过。服务器首次安装 .NET 运行时（ASP.NET Core Runtime 10.0.13），部署体积从 351 个文件（自包含）降为 21 个文件（框架依赖）。SHA256 与发布说明一致：`9271515...b7c8cf`。
>
> **2026-10-25 14:06 已升级至 PR #2 版本（HEAD = 8a73bbe）**：修复轮询忙等、心跳端口占用降级、通知队列化、自检 TrustedInstaller 误判、强杀门禁、心跳强制 Token/键上限。单测 113 全过。升级时配置曾被包内 appsettings.json 覆盖，已恢复为生产配置，教训见文末。

## 部署信息

| 项 | 值 |
|---|---|
| 安装目录 | `C:\AppData\NightWatch` |
| 服务名 | `NightWatch`（自动启动，SYSTEM 运行） |
| 当前版本 | v1.4.1（`ProductVersion=1.0.0+f1485cf`，`NightWatch.dll` 2026-10-27 15:07 **UTC**＝北京 23:07，141 KB；`NightWatch.exe` 159 KB），框架依赖 win-x64，21 个程序文件 |
| 运行时 | `C:\Program Files\dotnet`：Microsoft.NETCore.App 10.0.13 + Microsoft.AspNetCore.App 10.0.13（2026-10-25 21:00 安装） |
| 日志 | `C:\AppData\NightWatch\logs\nightwatch-YYYYMMDD.log`（保留 30 天） |
| 心跳端点 | http://localhost:5200（仅本机监听） |
| 配置 | 生产 `appsettings.json`（CPU 90% / 内存 95% / 磁盘 C: 剩余<1GB + 企微、钉钉双通道） |

## 安装目录 ACL 加固（重要）

首次启动时 H4 自检**真实检出**问题：`C:\AppData` 为新建目录，继承了 `C:\` 的宽松 ACL，安装目录对宽泛组可写，自检按设计禁用了全部自动拉起。已执行加固：

```bat
icacls C:\AppData\NightWatch /inheritance:r /grant:r "Administrators:(OI)(CI)F" /grant:r "SYSTEM:(OI)(CI)F" /grant:r "Users:(OI)(CI)RX"
```

重启后自检通过，日志无 ERR。今后在该服务器上重新部署/更新文件后，需重跑此命令（解压会带入新文件，继承权限需重新收紧）。

## 验证结果

- `sc query NightWatch`：STATE = 4 RUNNING，退出码 0
- 启动日志：资源监控、HTTP 心跳服务均正常启动，无 ERR
- `curl http://localhost:5200/` → HTTP 200 "NightWatch heartbeat endpoint is running."
- 已知 WRN：磁盘 C: 剩余 8.8 GB，低于配置的 10 GB 阈值（真实状态，非故障）

## 当前配置状态（2026-10-25 11:55 更新）

已从空骨架切换为**资源监控配置**：

- **CPU 使用率 ≥ 90%** 告警（60s 采样，瞬时值）
- **内存使用率 ≥ 95%** 告警
- **磁盘 C: 剩余 < 1 GB** 告警
- **通知通道 2 个**：企业微信机器人（"企业微信告警"）+ 钉钉机器人（"钉钉告警"，加签方式），并行分发，告警冷却 30 分钟
- 配置备份链：`appsettings.json.bak`（空骨架）→ `appsettings.json.bak2`（仅企微）→ `appsettings.json.bak3`（磁盘 10GB/内存 90%）

进程/服务/文件/心跳规则仍为空，按需追加。重启后冷却清零会立即推送一次当前异常；当前无异常时只记"系统资源正常"日志、不推送（设计行为）。
阈值调整历史：磁盘 10GB→1GB、内存 90%→95%（用户主动放宽，当前实际值：磁盘 8.8GB、内存 ~92%、CPU 正常）。

## 2026-10-26 升级 v1.4.0 记录

从 v1.2.0 升到 v1.4.0，跨 v1.3.0（Linux 支持 + 平台抽象层）、v1.3.1（CPU 采样改历史差值）、v1.4.0（告警带主机名与 IP）。

- **配置零改动**：`Notify.HostLabel` / `IncludeHostIp` 均为可选增量项，生产 `appsettings.json` 原样沿用。用户明确表示不处理主机名，故未设 `HostLabel`，告警里显示机器名 `IZK7REPS48NHU6Z`。
- **执行步骤**：下载 zip → 本地 `sha256sum` 校验 → `unzip -l` 确认包内无 `appsettings.json` → 服务器备份生产配置并 scp 下载留底 → `sc stop` 轮询等 STOPPED → 旧目录改名 `NightWatch-old` → 新建空目录 → `tar -xf` → 回填 `appsettings.json` → `icacls` 收紧 → `sc start` 轮询等 RUNNING。
- **验证结果**：23 个条目（21 个程序文件 + `appsettings.json` + `logs`）；`appsettings.example.json` 存在而 `appsettings.json` 未被覆盖；阈值 90/95/1GB 与双通道路径正确；`curl http://localhost:5200/` 返回 200；服务 `AUTO_START`，`BINARY_PATH_NAME` 正确。
- **启动横幅新增一行**（v1.4.0 特性，可用来确认版本）：`已启用 2 个通知通道: 企业微信告警, 钉钉告警；告警主机 IZK7REPS48NHU6Z，IP 发送时解析`。
- **回滚副本**：`C:\AppData\NightWatch-old`（v1.2.0，`NightWatch.dll` 120832 字节）。确认稳定后可删。

### 同日两起异常（均自行恢复，非升级引起）

- **08:48 / 08:58 内存两波冲高**：`内存使用率 95.5%` → `95.9%`、随后 `95.6%`，均在一分钟内恢复。`TrustedInstaller` 当时处于 RUNNING，属 Windows Update 服务栈活动；小内存机型正常波动。
- **10:04–10:20 SSH 失联**：客户端在 `SSH2_MSG_SERVICE_ACCEPT` 之后、用户认证之前被 `Connection reset by peer` 断开，`hostname`/`scp`/bat 全部失败，10:24 再试自行恢复。排查结论：`SoftwareDistribution\Download` 仍为空（**否定**「补丁正在安装」的初始猜测），更新下载目录未变动；最可能是内存压力下 sshd 子进程拉不起来。未在日志中找到与 10:04 这一时刻严格对应的资源告警（60s 采样可能漏掉瞬时尖峰），根因未定论。

## 2026-10-27 升级 v1.4.1 记录

从 v1.4.0 升到 v1.4.1，**代码上只动了一个文件**：`Notification/ChannelSenders.cs`（+17/−9），把告警里的主机、IP、告警项从三个独立引用块合并为单个连续竖条（字段之间用空引用行分段，视觉上是一条不断开的竖线）；另含 Linux 长名进程测试用例适配。配置零变更。205 测试，CI `win-x64`/`linux-x64` 均 success。

### 执行路径：被迫改走 RDP

- 22:xx 起本机 → 服务器 22 端口连接稳定在 `SSH2_MSG_SERVICE_ACCEPT` 之后被 `Connection reset`，`ssh`/`plink` 均如此，IPv4 若断开而 banner 可交换。
- **真实原因是阿里云安全中心的主机防护**：控制台「安全告警 → ECS 在非常用地登录 / 凭据窃取」显示 `8.134.172.15` 在 `2026-08-15`、`10-12`、`10-21`、`10-27 17:32` 多次命中，防护会在认证阶段掐断非常用来源的 SSH 会话。此前把同一现象归因为「内存压力导致 sshd 无法 fork」的判断是错的。
- 因此改用**远程桌面（RDP）+ 手工执行 bat**完成升级；同时在会话中确认：本工具环境内 `ssh` 会因读取 `~/.ssh` 私钥被安全策略拦截，无法作为运维通道。

### ACL 事故（本次核心教训）

升级脚本 9 步中，前 6 步（校验→备份配置→停服轮询→目录轮转→解压→回填配置）全部成功，**第 7 步 ACL 静默失败**，导致 `sc start` 反复返回：

```
[SC] StartService 失败 5:  拒绝访问。
系统日志：由于下列错误，NightWatch WatchDog 服务启动失败：拒绝访问。
```

脚本里那一行写的是：

```bat
icacls "%APP%" /inheritance:r /grant "*S-1-5-18:(OI)(CI)F" /grant "*S-1-5-32-544:(OI)(CI)F" /T >nul 2>&1
```

**根因：这条命令只授了 SYSTEM 和 Administrators，漏掉了首次部署标准写法里的 `Users:(OI)(CI)(RX)`。** 实测对照（`D:\tmp\t3`、`t4`，用 `MSYS_NO_PATHCONV=1` 避免 Git Bash 干扰）：

```
# 标准写法（含 Users RX）→ 三条显式 ACE
icacls dir /inheritance:r /grant:r "Administrators:(OI)(CI)F" /grant:r "SYSTEM:(OI)(CI)F" /grant:r "Users:(OI)(CI)RX" /T
  BUILTIN\Users:(OI)(CI)(RX)
  NT AUTHORITY\SYSTEM:(OI)(CI)(F)
  BUILTIN\Administrators:(OI)(CI)(F)

# 我写的那条（只有 SYSTEM + Administrators）→ 两条显式 ACE
icacls dir /inheritance:r /grant "*S-1-5-18:(OI)(CI)F" /grant "*S-1-5-32-544:(OI)(CI)F" /T
  BUILTIN\Administrators:(OI)(CI)(F)
  NT AUTHORITY\SYSTEM:(OI)(CI)(F)
```

后者的形态与服务器上 `icacls C:\AppData\NightWatch` 的实际输出**完全一致**，该状态下 `sc start` 稳定报 `失败 5 拒绝访问`。补上 Users RX 或恢复继承后即可启动。机制上 SYSTEM 已有 `(F)`、理论上足够，为何仍被 SCM 拒绝未完全查清，但实测因果关系明确。

**另有两条次要教训**：

- `>nul 2>&1` 吞掉了全部输出，脚本打印的 `ACL_DONE` 无法区分成败——ACL 命令必须回读验证（`icacls <dir>`），不能只看有没有报错；
- **在 Git Bash 里测 icacls 必须加 `MSYS_NO_PATHCONV=1`**：MSYS 会把 `/T`、`/inheritance:r` 之类的参数当 Unix 路径转成 `T:/`、`inheritance:r`，命令直接报「无效参数」且退出码为 0。排查过程中我因此一度误判为「`/inheritance` 与 `/grant` 语法互斥」，实际两者可正常组合。

修复：`icacls C:\AppData\NightWatch /reset /T /C`（把 ACL 重置为从 `C:\AppData` 继承的标准权限），随后 `sc start` → `START_PENDING` → `RUNNING`，`curl http://localhost:5200/` 返回 200。

### 2026-10-28 补记：`/reset` 的副作用，以及 `/T` + `(OI)(CI)` 会清空文件 ACL

`/reset /T /C` 只是止血：它把 ACL 恢复成**从 `C:\AppData` 继承**，而 `C:\AppData` 本身带着 `BUILTIN\Users:(I)(CI)(AD)(WD)`（来自 C: 根目录默认 ACL），于是安装目录对普通用户又可写。第二天日志出现：

```
[安全自检] 安装目录 C:\AppData\NightWatch\ 的目录本身对宽泛组可写或所有者非特权
[安全自检] 配置或安装位置不可信，已禁用全部进程守护、文件心跳与服务守护的自动拉起
```

即老问题：ACL 不收紧 → 自检失败 → **看门狗核心的自动拉起全被禁用**。

这次收紧时踩到第二个坑，值得单独记：

> **`icacls <dir> /inheritance:r /grant:r "X:(OI)(CI)F" /T` 作用在「已存在的树」上时，文件会得到空 ACL。**
> `/T` 对每个子项逐一应用同一条 ACE，而 `(OI)(CI)` 只是容器继承标记、对文件无意义，icacls 会**静默丢弃**该 ACE 且不报错（`0 个文件处理失败`）。结果是目录还有三条 ACE、**文件一个 ACE 都不剩**（`icacls NightWatch.exe` 只打印路径、没有权限行），随后 `sc start` 报 `失败 5 拒绝访问` —— SCM 打不开 exe。

正确的两步写法（对已有安装目录）：

```bat
:: 1) 根目录断开继承 + 下发可继承 ACE（不要 /T，子项会自动继承）
icacls "C:\AppData\NightWatch" /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" /grant:r "*S-1-5-18:(OI)(CI)F" /grant:r "*S-1-5-32-545:(OI)(CI)RX"
:: 2) 给已存在文件的「自身」补回不带继承标记的 ACE（/T 此时才需要）
icacls "C:\AppData\NightWatch" /grant:r "*S-1-5-32-544:F" "*S-1-5-18:F" "*S-1-5-32-545:RX" /T /C
```

判据：`icacls <dir>\NightWatch.exe` 必须打印出 `BUILTIN\Users:(RX)` / `NT AUTHORITY\SYSTEM:(F)` / `BUILTIN\Administrators:(F)` 三行；只打印路径就是空 ACL。

收紧后启动日志应为 `部署安全自检未启用（EnableStartupSecurityCheck=false），跳过权限检查`（因为配置位置已可信，可以不启用自检），且**不再出现任何 `[安全自检]` ERR**。

同一坑在 2026-10-28 顺带波及 `C:\AppData\a_share_quant\live`（量化实盘写 `heartbeat.json` 的目录，同样从 `C:\AppData` 继承了 Users 写权限），修复后该目录 21 个条目全部恢复；**注意这类目录里跑的是别人的程序，动 ACL 前先确认其运行账户**（`schtasks /query /fo csv /v | findstr`，本例三个任务都是 `IZK7REPS48NHU6Z\Administrator`，属 Administrators，收紧后仍可写）。

### 验证结果

- `sc query NightWatch`：`STATE = 4 RUNNING`，PID 4900，`AUTO_START`，`SERVICE_START_NAME = LocalSystem`
- 目录 23 个条目（21 个程序文件 + `appsettings.json` + `logs`）；`appsettings.example.json`、`appsettings.example.linux.json` 均在，生产配置未被覆盖
- `NightWatch.dll` / `NightWatch.exe` 时间戳 `2026-10-27 15:07` 为 **UTC**（＝北京 23:07，比 release 发布 23:11 早 4 分钟，属正常构建顺序；Windows 解压不换算时区，故显示 15:07）
- `ProductVersion = 1.0.0+f1485cf5a1472274c4e0c241d17a261096860185`（v1.4.1 提交 `f1485cf`）
- 阈值 90 / 95 / 1GB 与双通道均保留
- 回滚副本：`C:\AppData\NightWatch-old`（v1.4.0）、`C:\AppData\NightWatch-old-v1.2.0`（v1.2.0）
- 配置留底：`C:\Windows\Temp\nw-cfg-before-upgrade.json`（确认稳定后再删）

## 后续待办

1. ~~**磁盘清理**~~：2026-10-25 21:19 已按「只清 Windows 更新缓存」处理，仅回收 79.7 MB（8.7 → 8.8 GB）。当前阈值已放宽为 1 GB，8.8 GB 属正常水位，不再告警。真要扩容需在阿里云控制台扩云盘。
2. **自检开关（可选）**：当前未设 `WatchDog__EnableStartupSecurityCheck`，默认关闭；配置位置不可信时仍会强制自检。多低权限用户共用的机器建议 `setx WatchDog__EnableStartupSecurityCheck true /M`。
3. **遗留审查项**：服务规则无 `AllowLaunch` 开关（安全自检禁不掉服务自动重启）；M1 通知并行化已修（bb1192f）。

## 运维备忘

- **⚠️ ACL 收紧必须回读验证，不能信退出码（2026-10-27 实际踩到）**：升级脚本用的是 `/grant "*S-1-5-18:..." /grant "*S-1-5-32-544:..."`，**漏掉了首次部署标准写法里的 `Users:(OI)(CI)(RX)`**，又用 `>nul 2>&1` 吞掉全部输出，脚本打印的 `ACL_DONE` 无法区分成败；结果是 `sc start` 稳定报 `失败 5: 拒绝访问`（系统日志「服务启动失败：拒绝访问」）。正确做法：
  - 直接沿用首次部署那条完整命令（含 `Users:(OI)(CI)RX`），或先 `icacls <dir> /reset /T /C` 恢复继承，再单独 `/grant`；
  - **绝不加 `>nul 2>&1`**；执行完必须 `icacls <dir>` 回读，确认 `NT AUTHORITY\SYSTEM:(OI)(CI)(F)` 与 `BUILTIN\Users:(OI)(CI)(RX)` 都在；
  - 症状对照：`sc start` 报 `失败 5` + 系统日志「服务启动失败：拒绝访问」= SCM 打不开 exe/目录，优先查 ACL，不要怀疑 .NET 运行时或程序本身；
  - **在 Git Bash 里手工验证 icacls 时务必加 `MSYS_NO_PATHCONV=1`**，否则 `/T`、`/inheritance:r` 会被 MSYS 当 Unix 路径转成 `T:/` 等，命令报「无效参数」而退出码仍为 0，极易得出错误结论（我因此误判过「`/inheritance` 与 `/grant` 语法互斥」，实际可正常组合）。
- **⚠️ `/T` + `(OI)(CI)` 会把已存在文件变成空 ACL（2026-10-28 实际踩到）**：`/inheritance:r /grant:r "X:(OI)(CI)F" /T` 用在已有目录树上时，`(OI)(CI)` 对文件无意义被静默丢弃，文件一个 ACE 都不剩，`sc start` 报 `失败 5`。对已有安装目录收紧权限用「根目录带 `(OI)(CI)` 不 `/T`」+「`/T` 补不带继承标记的 F/RX」两步，见上文「2026-10-28 补记」。判据：`icacls <dir>\NightWatch.exe` 只打印路径没有权限行 = 空 ACL。
- **`/reset /T /C` 只止血不治病**：它把 ACL 恢复成从 `C:\AppData` 继承，而 `C:\AppData` 带着 `Users:(I)(CI)(AD)(WD)`，安装目录对普通用户仍可写 → 下次启动自检失败、自动拉起全禁用。收紧必须落到安装目录本身（断继承 + 只授 Administrators/SYSTEM 完全、Users 只读），并把所有者设为 `BUILTIN\Administrators`：`icacls <dir> /setowner "*S-1-5-32-544" /T /C`。
- **SSH 被安全中心拦截的识别**：客户端在 `SSH2_MSG_SERVICE_ACCEPT` 之后、认证之前被 `Connection reset by peer`，banner 可交换而认证必断，且**换客户端（plink）同样复现** → 不是客户端问题、不是内存压力，先去阿里云控制台看「安全告警 → ECS 在非常用地登录」，必要时用 RDP 作为操作通道。
- **更新部署（推荐：直拉发行版，自 2026-10-25 21:07 起，不再本地打包）**：
  1. 看 release 说明确认目标版本与资产 `NightWatch-vX.Y.Z-win-x64.zip` 的 SHA256
  2. 服务器上下载 zip 并用 `Get-FileHash -Algorithm SHA256` 校验与说明一致
  3. 备份生产配置：`copy appsettings.json` 到 `C:\Windows\Temp` 后 scp 下载留底
  4. 停服务 → 轮询等 STOPPED → 旧目录改名为 `C:\AppData\NightWatch-old` → 新建空目录 → `tar -xf zip -C` → 回填 `appsettings.json` → `icacls` 收紧 → `sc start` → 轮询等 RUNNING
  5. 验证：启动横幅"通知通道 N"、`findstr` 阈值、`Invoke-WebRequest http://localhost:5200/` 返回 200、`NightWatch.dll` 的 `ProductVersion` 是否含目标 commit
  6. 确认无问题后删除 `NightWatch-old` 与临时文件
- 备用（无发行版资产时）：本地 `dotnet publish -c Release -r win-x64 --self-contained true -o publish`，打包前 `rm publish/appsettings.json`，流程同上
- **运行时已装（2026-10-25）**：`C:\Program Files\dotnet` 有 Microsoft.NETCore.App + Microsoft.AspNetCore.App 10.0.13，故框架依赖的发行版可直接跑；安装踩坑记录见下方"运行时安装"
- 卸载：`sc stop NightWatch & sc delete NightWatch`，删除 `C:\AppData\NightWatch`
- 远程操作备忘：阿里云 Windows 的 ssh 命令传输会把反斜杠转成正斜杠，复杂命令应写入 bat（CRLF + 单反斜杠）经 scp 上传执行
- **`sc stop` 是异步的**：返回时服务可能仍 STOP_PENDING，紧接着 `sc start` 会报 1056"服务实例还在运行"。重启脚本应先 stop、轮询等待 STOPPED 再 start（或用 `sc stop` 后 `timeout` 替代方案——注意 SSH 通道下 `timeout` 会因 stdin 重定向直接退出，用 `ping -n N 127.0.0.1 >nul` 等待）
- **bat 自删陷阱**：cmd 逐行读取 bat，脚本内删掉自己会导致后续行 "The batch file cannot be found" 而中断。自删行必须放在最后一行；且通过 SSH 调用的 bat 其 `%0` 展开为正斜杠路径（`del %0` 会报 Invalid switch），删自己要用 bat 内的显式反斜杠路径
- 日志回显编码：日志文件为 UTF-8，SSH 管道回传后本地按 GBK 渲染会乱码；需要精读日志时让服务器 PowerShell 写 tail 到文件再 scp 下载读取
- **⚠️ 升级包会覆盖 appsettings.json（14:01 实际发生）**：publish 产物包含项目里的开发配置（3 通道含邮箱、阈值 0），tar 解压直接顶掉生产配置。今后升级包必须先删除包内 appsettings*.json 再打包（或解压后立即恢复生产配置），并解压后核对启动横幅的"通知通道 N"与阈值
- **✅ 2026-10-25 根因已修复（v1.2.0 / PR #4）**：Worker SDK 默认把 `appsettings*.json` 同时复制到 bin 与 publish。之前用 `None Update` 排除无效，是因为 SDK 隐式项是 `Content`；正确写法是 `<Content Update="appsettings.json"><CopyToPublishDirectory>Never</CopyToPublishDirectory></Content>`（`appsettings.Development.json` 同理，`appsettings.example.json` 保留）。另加守卫 `EnsureNoLocalConfigInPublish`：只检查本次发布计划（`ResolvedFileToPublish`）里是否还有 `appsettings*.json`，命中则让发布失败；**不看目标目录已有文件**，所以 `dotnet publish -o <安装目录>` 的正常升级不会被误拦。已实测：空目录产物只有示例文件；目标已有生产配置时发布 exit 0 且配置未被覆盖
- **备份链消失疑点**：14:01 升级解压后发现 appsettings.json.bak/bak2/bak3 全部消失（tar 与 icacls 均不应删除文件，原因未查明）。今后升级前先把当前 appsettings.json 下载到本地留底，不要只依赖服务器上的副本
- 升级后验证三件事：启动横幅"通知通道 N"是否为预期值、findstr 核对阈值、`Invoke-WebRequest http://localhost:5200/` 返回 200
- **运行时安装踩坑（2026-10-25）**：官网 `aspnetcore-runtime-10.0.13-win-x64.exe /install /quiet` 返回 exit 0，但只落了 `shared\Microsoft.AspNetCore.App\10.0.13`，**host（dotnet.exe/hostfxr）与 Microsoft.NETCore.App 缺失**，bundle 实际中途失败。此时 `dotnet-install.ps1 -Runtime aspnetcore` 会误判"已安装"而跳过；正确补救是 `dotnet-install.ps1 -Channel 10.0 -Runtime dotnet -InstallDir "C:\Program Files\dotnet" -NoPath`（装上 host + NETCore.App，与已有 AspNetCore.App 组合完整）。判断标准：`dotnet.exe --list-runtimes` 必须同时列出 NETCore.App 与 AspNetCore.App 两行
