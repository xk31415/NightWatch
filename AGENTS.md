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
- GitHub 的凭据在 Windows 凭据管理器里（`git credential fill` 可取），Gitee 的 `git push` 用账号密码，与上面的令牌无关。

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
