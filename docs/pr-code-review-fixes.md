# fix: 修复代码审查发现的忙等、强杀门禁、自检误报与心跳安全边界

<!--
分支：fix/code-review-high-priority → main
对比：https://github.com/xk31415/NightWatch/compare/main...fix/code-review-high-priority
规模：11 个提交 / 23 个文件 / +1587 −117
-->

## 概要

对 NightWatch 做了一轮完整代码审查，并把高优先级问题全部修掉。改动按**安全**、**可靠性**、**CLI/文档**三组拆分，单测从 44 增至 113（+69），构建 0 警告。

审查过程中用临时探针做了动态验证（已清理），修复后逐条复测：忙等、端口占用拖垮宿主、自检误判系统路径、`--test-message` 解析为空、`ProcessName` 不一致导致重复拉起。

## 问题与修复

### 安全

| 问题 | 修复 | 提交 |
|---|---|---|
| 自检把 `NT SERVICE\TrustedInstaller` 所有者判成「非特权」，`C:\Windows\System32\cmd.exe`、`C:\Program Files` 等被判不可信 → 加固过的机器上自动拉起被静默禁用并误发告警 | TrustedInstaller 与 `NT SERVICE\*` 视为特权所有者 | `1fa3916` |
| 系统目录继承项以 `0xA0000000`（`GENERIC_READ\|GENERIC_EXECUTE`）存储，只读掩码不含泛型位 → 纯只读权限被判成可写（只修 owner 仍会误报） | 判定前把 `GENERIC_*` 映射为具体权限位，Deny 同样映射 | `1fa3916` |
| `Host=0.0.0.0` 且未配 `Token` 时端点完全裸奔：任何人可伪造心跳、「必达缺失」告警可被压掉、可无限灌键 | 对外监听无 Token 时拒绝启动接收端并记 ERR；路径段校验（≤128、无控制符、不含 `/`）→ 400；新增 `MaxKeys`（默认 10000）→ 429 | `8b55c6d` |

### 可靠性

| 问题 | 修复 | 提交 |
|---|---|---|
| `CheckAllAsync` 抛异常会跳过 tick 等待，`while` 立即重跑 → 忙等（实测 500ms 内 375,501 次调用），CPU 打满并刷爆日志 | 把等待移出检查的 `try`，异常只记一次 ERR 后照常等待 | `7bfbc2f` |
| HTTP 心跳端口被占用会让**整个宿主**启动失败（Unhandled IOException），进程/服务监控一起停摆 | 捕获绑定失败，记 ERR 并释放 WebApplication，接收端降级关闭，其余监控继续 | `4b5971c` |
| `RestartIfNotResponding` 无条件强杀：`AllowLaunch=false` 或退避熔断时杀完不拉起，却仍报「已强杀并重启」；卡死路径每次清零退避，永不熔断 | 无响应进程统一走唯一入口 `HandleHungProcesses`：门禁（`AllowLaunch`/`FilePath`）在动杀之前，退避与去重同处；文案按实际结果生成；仅健康分支 Reset；旧实例 5 秒未退出则跳过重启避免双实例 | `e2f257d` `1b9d214` |
| 配置了 `FilePath` 却仍按 `ProcessName` 枚举进程，两者不一致时每个周期重复拉起（击穿防双实例） | 配置 `FilePath` 时按可执行文件名主干枚举，再按完整路径过滤 | `e2f257d` |
| 示例配置里的 `"WorkingDirectory": ""` 让子进程继承 `C:\Windows\System32` 作为工作目录，依赖相对路径的程序启动失败 | 空白工作目录回落到 exe 所在目录（新增 `ProcessStartInfoFactory`，两个监控器共用） | `e2f257d` |
| 通知内联 `await`，SMTP 15 秒超时会拖住同一监控器下所有规则的检查 | 新增 `QueuedNotifier`：有界队列（256）+ 后台投递；满队列丢弃并记 WRN，停机最多等 5 秒排空 | `886edfd` |
| `Notify=false` 分支提前 return，异常状态没记录，恢复通知可能被抑制分支吞掉 | 拆分 `_unhealthyRules`（状态）与 `_alertedRules`（真正推送过）：恢复时两者都要为真 | `6ec861b` |

### CLI / 文档

| 问题 | 修复 | 提交 |
|---|---|---|
| README 里的 `--test-notify --test-message "文案"` 实际解析为空串（配置系统把下一个 token 当成 `--test-notify` 的值），发出空正文 | 新增 `CommandLineArgs` 手工解析 `--name value` / `--name=value`，空值回落默认文案 | `553433b` |
| `--test-notify` 所有通道都失败也打印「已发送」并返回 0 | 按实际结果返回：无可用通道或任一失败 → 退出码 1，逐条打印失败原因 | `886edfd` |
| README 与实际行为不符 | 同步强杀门禁、空工作目录、心跳端口降级、鉴权/容量上限、队列与退出码；示例配置补 `MaxKeys` | `bd70de1` `7319d61` |

## 行为变更（升级前必读）

1. **`Host` 非回环且未配 `Token`** → 接收端不再启动（日志有明确 ERR）。默认 `localhost` 不受影响。
2. **`--test-notify` 退出码**：失败从 `0` 变 `1`，脚本可直接判断。
3. **告警投递异步**：正常无感；停机最多等 5 秒排空；队列满（默认 256）会丢弃新告警并记 WRN。
4. **恢复通知**：只在本次异常期间确实推送过告警时发送，抑制路径不再产生多余恢复通知。
5. **`RestartIfNotResponding`**：安全自检禁用拉起或退避熔断时只告警，不再强杀进程。
6. **进程存活判定**：配置 `FilePath` 后以文件名主干 + 完整路径为准，`ProcessName` 不再参与枚举。

## 验证

- `dotnet build src/NightWatch -c Release`：**0 警告 0 错误**
- `dotnet test src/NightWatch.Tests -c Release`：**113 通过 / 0 失败**（基线 44，新增 69）
- 动态复测（临时探针，已清理）：

| 场景 | 修复前 | 修复后 |
|---|---|---|
| `CheckAllAsync` 抛异常 | 500ms 内 375,501 次调用（忙等） | 400ms 内 ≤2 次 |
| HTTP 端口被占用 | `Unhandled IOException`，进程退出 | 宿主持续运行，`ERR` 提示接收端降级 |
| `cmd.exe` / `C:\Program Files` 自检 | `untrusted=True`（误报） | `untrusted=False`；`Users` 可写目录仍 `True` |
| `--test-notify --test-message "文案"` | 正文为空 | 正文为「文案」 |
| `Host=0.0.0.0` 无 Token | 端点裸奔 | 拒绝启动，宿主继续运行 |
| `ProcessName` 与 exe 名不一致 | 每个周期重复拉起 | 按路径正确识别，只拉起一次 |

## 新增测试

| 文件 | 覆盖 | 用例 |
|---|---|---|
| `PollingLoopTests` | 异常后不忙等、正常停止 | 2 |
| `ProcessLaunchTests` | 映像名枚举、工作目录回落、协调器键，以及强杀整条决策路径（门禁/熔断/去重/杀失败/起失败） | 19 |
| `AclEvaluatorTests`（扩充） | TrustedInstaller / NT SERVICE 所有者、`GENERIC_*` 映射、系统目录 ACL 形态 | +8 |
| `HeartbeatApiDegradeTests` | 端口占用降级、未启用时启停 | 2 |
| `HeartbeatSecurityTests` | Token 强制矩阵、路径校验、容量与 400/429 | 21 |
| `CommandLineArgsTests` | `--test-message` 两种写法、缺失/相似名 | 6 |
| `NotificationDispatchTests` | 队列非阻塞、满队列丢弃、分发结果聚合 | 7 |
| `RecoveryNotificationTests` | 告警+恢复恰好一次、纯抑制不打扰、抑制夹在中间仍恢复 | 4 |

## 回滚

按提交粒度回滚，安全项（`1fa3916`、`8b55c6d`）与可靠性项互不依赖。若只需回退通知队列：把 `Program.cs` 里 `IEventNotifier` 的注册改回 `MultiChannelNotifier` 即可，`PollingMonitorBase` 与各监控器无需改动。

## 后续（不在本 PR）

- 启动期配置校验（空路径 / 端口范围 / `Expected` 格式）并 fail-fast
- `TimeProvider` 抽象：统一 UTC、抗时钟回拨、让冷却与退避可测
- ACL 判定的 ACE 顺序与 null DACL 语义（策略重写，需谨慎）
- 工程化：`.editorconfig`、`global.json`、发布时排除本机 `appsettings.json`

## 审查范围

`src/NightWatch` 全部 20 个源文件、6 个测试文件、`appsettings*.json`、`.github/workflows/build.yml`；结论见 Review 评论。
