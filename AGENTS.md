# 协作约定（供各 AI 会话 / 新同学参考）

NightWatch：.NET 10 跨平台看门狗服务（Windows 服务 + systemd）。平台差异集中在 `src/NightWatch/Platform/`。

## 常用命令

```powershell
dotnet build src\NightWatch\NightWatch.csproj -c Release
dotnet test  src\NightWatch.Tests\NightWatch.Tests.csproj -c Release
dotnet publish src\NightWatch\NightWatch.csproj -c Release -r win-x64 --self-contained false -o <dir>
```

## 凭据约定（重要）

- **Gitee 令牌**在 `%USERPROFILE%\.nightwatch\gitee-token`（单行、无 BOM）。读取方式：
  ```powershell
  $token = [IO.File]::ReadAllText("$env:USERPROFILE\.nightwatch\gitee-token").Trim()
  ```
  也支持环境变量 `GITEE_TOKEN`（优先级低于文件）。
- **只允许「读文件 → 直接用」**：不要 `Write-Host`/日志/命令行参数打印令牌，不要写进 `appsettings*.json`，不要提交。
- 需要把令牌带进 URL 时用 `curl --config <临时文件>`，用完删除。
- **发 Gitee 发行版一律用脚本**，它自己读令牌：`tools/create-gitee-release.ps1 -Tag vX.Y.Z -BodyFile <file> -Assets <zip...>`（`-DryRun` 先空跑）。
- 新机器 / 新用户 / 刚重置令牌：跑 `tools/set-gitee-token.ps1`（掩码输入 → 写文件 → 收紧权限 → 校验，只打印 login）。
- GitHub 的凭据在 Windows 凭据管理器里（`git credential fill` 可取），Gitee 的 `git push` 用账号密码，与上面的令牌无关。

## 服务器访问（SSH）

- 生产机连接信息（host / user / port / 私钥）在 `%USERPROFILE%\.nightwatch\server.json`，**不入库**。仓库、文档、聊天输出里都不得出现服务器 IP 或密钥内容；`docs/deploy-log.md` 保持脱敏（`<服务器公网 IP>`）。
- 统一用 `tools/ssh-server.ps1`，不要手搓 ssh 参数：
  ```powershell
  .\tools\ssh-server.ps1 -Command "sc query NightWatch"          # 执行单条命令
  .\tools\ssh-server.ps1 -Command "icacls C:\AppData\NightWatch" # 只读核对
  .\tools\ssh-server.ps1 -Upload .\pkg.zip -RemoteDir C:\Windows\Temp
  .\tools\ssh-server.ps1 -Interactive                            # 交互登录
  .\tools\ssh-server.ps1 -Command "..." -DryRun                  # 只打印、不连接（主机打码）
  ```
- **默认只读**：脚本会拒绝 `sc stop/start`、删改文件、改 ACL 等破坏性命令（exit 2）。确需执行要加 `-AllowDestructive`，并且**先取得用户明确同意**——删除、覆盖、重启服务都属于不可逆或影响线上。
- **连续多条命令用连接池**（每次新建 SSH 连续几次会被限流，表现为 `Connection reset ... port 22`、exit 255）：
  ```powershell
  .\tools\ssh-pool.ps1 -Command "sc query NightWatch"
  .\tools\ssh-pool.ps1 -Command 'dir /a "C:\Windows\Temp\nw-*"'   # 引号安全：命令走文件
  .\tools\ssh-pool.ps1 -Command '...' -AllowDestructive            # 破坏性命令仍需显式放行
  ```
  它复用一条长连接（本地 `127.0.0.1:19555`，后端 `%USERPROFILE%\.nightwatch\ssh_daemon.py`），
  未运行时自动拉起；依赖本机 `python` + `pip install --user paramiko`（均已就绪）。
  单条命令或上传文件仍用 `ssh-server.ps1`（它每次新建连接，别连着调很多次）。
- 被限流后的处置：等 5–10 分钟再试，或直接用连接池；不要连续重试刷连接。
- 常见只读核对：`sc query <svc>`、`certutil -hashfile <file> SHA256`、`icacls <path>`、`Get-Item ... VersionInfo.ProductVersion`、`Select-String` 数日志 `[ERR]`、`Get-ChildItem ... .Count`。
- 核对结果写回 `docs/deploy-log.md` 时，注明**复核日期**（升级时间与复核时间可能不同），数值照抄真实输出，不臆造。

## 提交与发布

- 提交前 `git status` 看清工作区；**只 `git add` 本次改动的具体文件**（可能有其他会话的在研改动）。
- 提交信息用文件传入：`git commit -F <消息文件>`（PS 5.1 会把消息里的 ASCII 双引号、中文弯引号当定界符）。
- 发布流程见 `docs/release-checklist.md`；部署后核对见 `docs/post-deploy-checks.md`；踩过的坑与检查清单见 `docs/lessons-learned.md`。
- 双平台 CI（`win-x64` + `linux-x64`）都绿才能合并；`main` 需在 GitHub 与 Gitee 保持同一 sha。

## 代码约定

- 平台差异放进 `Platform/` 的实现类，调用方不写裸 `if (OperatingSystem.IsWindows())`。
- 外部命令统一走 `Platform/ProcessRunner`（`ArgumentList` + 超时 + 并发读流）。
- 取消异常必须向上抛：`catch (OperationCanceledException) { throw; }` 放在降级 `catch` 之前。
- 常驻状态不要缓存易变的环境事实（如本机 IP）：发送时取 + 短 TTL 缓存。
- 涉及真实进程/文件的测试，失败信息要带 `exit code`、`stderr`、目标路径等可诊断信息。
