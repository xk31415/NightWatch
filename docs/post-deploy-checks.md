# 部署后验证

部署或升级完成后，按这份清单在 **30 分钟内**走一遍；每项都给出「命令 / 预期 / 不符合时怎么查」。
先看[常见问题](../README.md#常见问题)，仍解决不了再翻日志。

## 0. 先确认版本与启动

无论 Windows 还是 Linux，启动横幅都是判断「起来了没有」的第一依据：

```
守夜人(NightWatch)启动 | 进程规则 3 | 服务规则 1 | 文件规则 2 | 文件心跳规则 1 | HTTP心跳 1 | 通知通道 2
```

- 规则数量要和 `appsettings.json` 对得上（漏配、数组写成对象都会静默变成 0）。
- 通知通道数量要和配置一致；若为 0，检查 `Notify.Channels` 与通道初始化日志（邮件缺 `SmtpHost` 会被跳过并记日志）。
- 紧接着应有 `已启用 N 个通知通道: …；告警主机 <主机>，IP 发送时解析`——确认主机名符合预期（`HostLabel` 是否生效）。

日志位置：安装目录下 `logs/nightwatch-*.log`（按天滚动，保留 30 天）。

## 1. Windows

```powershell
sc query NightWatch                                  # 预期 STATE: 4 RUNNING
Get-Service NightWatch | Select Status,StartType     # 预期 Running / Automatic
Get-Process NightWatch | Select Id,StartTime         # 预期 1 个进程
Get-Content "D:\NightWatch\logs\nightwatch-$(Get-Date -f yyyyMMdd).log" -Tail 40
```

逐项检查：

| 检查 | 预期 | 不符合时 |
|---|---|---|
| 服务状态 | `RUNNING` | `net start NightWatch` 看报错；事件查看器 → 应用程序日志 |
| 启动类型 | `Automatic` | `sc config NightWatch start= auto` |
| 进程数 | 1 个 | 多于 1 个说明重复拉起（看日志是否有反复「已启动进程」），检查 `IntervalSeconds` 与冷却 |
| 安全自检 | 日志无「安全自检已禁用自动拉起」，也没收到「部署安全自检未通过」告警 | 按 [收紧目录权限](../README.md#收紧目录权限) 修 `icacls` 后重启 |
| 日志级别 | 无 `ERR` / `FTL` | 先看最后 40 行，再按关键字搜 |

## 2. Linux

```bash
systemctl status nightwatch --no-pager      # 预期 active (running)
systemctl is-enabled nightwatch             # 预期 enabled
ls -l /opt/nightwatch/NightWatch            # 预期 -rwxr-xr-x（缺失执行位就 chmod +x）
pgrep -a -f NightWatch                      # 预期 1 个进程
journalctl -u nightwatch -n 60 --no-pager
```

逐项检查：

| 检查 | 预期 | 不符合时 |
|---|---|---|
| 单元状态 | `active (running)` | `journalctl -u nightwatch -n 100` 看退出原因；`systemctl cat nightwatch` 核对 `ExecStart` |
| 开机自启 | `enabled` | `systemctl enable nightwatch` |
| 执行位 | `-rwxr-xr-x` | 从 Windows 交叉发布/zip 传输会丢执行位：`chmod +x /opt/nightwatch/NightWatch` |
| 属主 | `root root` | `chown -R root:root /opt/nightwatch`；否则安全自检会关掉自动拉起 |
| 自检开关 | 日志无「安全自检已禁用自动拉起」；`systemctl show nightwatch -p Environment` 能看到开关 | 用 unit 里的 `Environment=` 设置，**不要**用 `systemctl set-environment`（重启即丢） |
| 环境变量生效 | 上面那条命令能查到 `WatchDog__EnableStartupSecurityCheck=true` | 没生效说明写在了运行中的 manager 而非 unit |
| 干净停止 | `systemctl stop nightwatch` 后无残留进程 | 有残留说明取消信号未被正确处理，查日志尾部 |
| 磁盘挂载点 | `Disks[].Drive` 必须真实存在 | `df -h <挂载点>`；写错会每轮报读盘失败 |

## 3. 验证告警通路（最关键的一步）

```powershell
# Windows（在安装目录）
.\NightWatch.exe --test-notify --test-message "部署验证 $(Get-Date -f 'MM-dd HH:mm')"

# Linux（用运行服务的同一用户，通常 root）
/opt/nightwatch/NightWatch --test-notify --test-message "部署验证 $(date +'%m-%d %H:%M')"
```

- **退出码 0** = 所有通道成功；**1** = 至少一个通道失败（失败详情在标准输出里）。
- 微信/钉钉：到群里确认收到，且标题下方有「主机：…」「IP：…」两行（用于区分多台机器）。
- 邮件：确认到达收件箱（不在垃圾箱），正文含主机与 IP。
- 若某个通道失败：企微/钉钉多为 `WebhookUrl`/`Secret` 错误；邮件多为 `SmtpHost`/授权码（不是登录密码）/`UseSsl`。

## 4. 观察一个完整周期（15–30 分钟）

告警有冷却（`CooldownMinutes`），心跳/文件监控要等阈值时长，**部署后立刻下结论容易误判**。

- 日志里不应出现连续的「进程不存在 / 不存在服务」误报；若出现且进程确实在跑，检查 `ProcessName` 是否与真实进程名一致（Linux 注意内核 15 字符截断，配全名即可，程序会回退匹配）。
- 不应反复出现「已启动进程」——重复拉起说明识别失败或拉起后立即退出。
- 配置了 `Schedule` 时，窗口外**本来就不检查**（`Mode: Off` 完全停止）；确认当前时间是否在窗口内。
- 资源监控：等一个 `IntervalSeconds` 后确认无异常告警；Linux 容器内内存优先读 cgroup 限额属正常。

## 5. 升级场景的额外检查

- **`appsettings.json` 未被覆盖**：发布包本就不含配置，若发现配置变成示例值，说明整目录被替换了——从备份恢复。
- 新增配置项是否需要补：查对应版本的发行说明（例如 `v1.4.0` 新增 `Notify.HostLabel` / `IncludeHostIp`，不配也有默认行为）。
- 旧进程已停止：确认没有新旧两个进程同时监控（Windows `Get-Process NightWatch`、Linux `pgrep`）。
- 版本核对：以发行版文件名 / 部署时间 / 发行说明为准；启动横幅里的规则数量是最直接的「配置是否被读到」证据。

## 6. 失败对照表

| 现象 | 常见原因 | 处理 |
|---|---|---|
| 服务起不来，`--test-notify` 能跑 | 服务账户权限 / unit 的 `ExecStart` / 执行位 | Linux 查 `systemctl status` 的退出码与 `journalctl`；Windows 查事件日志与账户对安装目录的读权限 |
| 进程不停被拉起 | `ProcessName` 与真实进程名不一致（Linux 长名截断）、`FilePath` 路径错误 | 配 `FilePath` 精确匹配；日志里看「已启动进程」的 PID 是否每次变化 |
| 告警发不出，退出码 1 | Webhook 失效 / 令牌错误 / SMTP 授权码 | `--test-notify` 输出里的失败通道名 + 异常消息 |
| 邮箱超时 | 网络/端口被拦（587/465） | 试 `UseSsl` 组合，确认出网策略 |
| 一直「服务不存在」 | `ServiceName` 写错（Linux 要 unit 名如 `myapp.service`） | `systemctl is-active myapp.service` 手动验证 |
| 安全自检关闭了拉起 | 目录 other 可写 / 属主不是 root / 任一祖先目录不可信 | `chown -R root:root` + `chmod go-w` 后重启；告警正文里有具体路径 |
| 自检开关重启后失效 | 用了 `systemctl set-environment` | 改为 unit 里的 `Environment=`（`systemctl edit`） |
| 日志里 CPU/内存全是 0 | 采集器未启动（阈值都为 0）或容器限制 | 配置 `CpuPercent`/`MemoryPercent`；Windows 确认性能计数器可用 |
