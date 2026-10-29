# 发布清单（NightWatch）

发一次版本要动的东西都在这，按顺序走即可。命令以 PowerShell 为主（Windows 开发机）；GitHub/Gitee 用 REST API，不依赖 `gh`。

## 0. 版本号

| 改动性质 | 版本 | 例子 |
|---|---|---|
| 只修 bug / 文档 | patch | `v1.3.0` → `v1.3.1` |
| 新功能、新平台支持 | minor | `v1.2.0` → `v1.3.0` |
| 破坏性变更（配置/行为不兼容） | major | `v1.x` → `v2.0.0` |

tag 一律从 `main` 打，annotated（`git tag -a`），tag 消息就是发行说明初稿。

## 1. 合并与验证

1. PR 合并后，确认 `main` 上的 CI **两个任务都绿**（`Build (Release, win-x64)` 与 `Build (Release, linux-x64)`）。
2. 本地同步并跑测试：

```powershell
git switch main
git pull --ff-only
dotnet test src\NightWatch.Tests\NightWatch.Tests.csproj -c Release
```

参考基线：两平台跑**同一套**用例，总数相同，差别只在跳过集合——Windows 跳过 `LinuxFact`（/proc 解析、真实进程名截断等），Linux 跳过 `WindowsFact`（ACL/SID、性能计数器）。因此恒有：

- `Windows 通过数 = 总数 − LinuxFact 数`
- `Linux 通过数 = 总数 − WindowsFact 数`

**具体数字一律以当次 CI 输出的「测试总数 / 通过数 / 跳过数」为准，不要照抄本文档**——用例和平台专属测试会持续变化，抄数字只会换来一次误判（本分支实测：Windows 307 通过 / 6 跳过 / 总 313；Linux 需看该次 CI 输出）。构建应 0 警告 0 错误。对不上时先看是不是新增或调整了平台专属用例，别直接判成回归。

## 2. 打 tag 并推送

```powershell
# PS 5.1 的 Out-File -Encoding utf8 会写 BOM，git 仓库里会出现 \uFEFF 开头的 tag 消息。
# 统一用 .NET API 写无 BOM 的 UTF-8。
$msg = @'
v1.3.1 — 一句话主题

- 变更点 1
- 变更点 2

单测全部通过（照抄 CI 输出的数字）。
'@
[IO.File]::WriteAllText("$env:TEMP\nw-tag.txt", $msg, [Text.UTF8Encoding]::new($false))

git tag -a v1.3.1 -F "$env:TEMP\nw-tag.txt"
git push https://github.com/xk31415/NightWatch.git main v1.3.1
git push https://gitee.com/xk31415/NightWatch.git main v1.3.1
```

> `origin` 配了 gitee 与 github 两个 push URL，`git push origin` 会同时推两边。显式写 URL 更可控，也能避免一边失败另一边被跳过。

## 3. 下载 CI 制品并校验

```powershell
$h = @{ Authorization = "token $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'NightWatch-Release' }
$sha = git rev-parse HEAD

# 必须带过滤：不过滤时 workflow_runs[0] 可能是 PR 触发的、还在跑的、甚至失败的那次 run，
# 等于拿错制品去发布。status=success + event=push + branch=main 才是「main 上绿掉的那次」。
$runsUrl = "https://api.github.com/repos/xk31415/NightWatch/actions/runs?head_sha=$sha&branch=main&event=push&status=success"
$run = (Invoke-RestMethod $runsUrl -Headers $h).workflow_runs | Select-Object -First 1
if (-not $run) { throw "没找到 $sha 上成功的 push run：先确认 CI 已绿，必要时 git fetch 后重取" }
if ($run.conclusion -ne 'success') { throw "run $($run.id) 的 conclusion 是 $($run.conclusion)，不是 success，禁止发布" }
if ($run.head_sha -ne $sha) { throw "run 的 head_sha（$($run.head_sha)）与本地 HEAD（$sha）不一致" }
Write-Host "使用 run $($run.id)（$($run.head_branch) @ $($run.head_sha)，$($run.conclusion)）"

$arts = (Invoke-RestMethod "https://api.github.com/repos/xk31415/NightWatch/actions/runs/$($run.id)/artifacts" -Headers $h).artifacts
$arts | Select-Object name, @{n='MB';e={[math]::Round($_.size_in_bytes/1MB,2)}}, id
```

下载（token 放 `--config` 文件，避免出现在命令行）：

```powershell
$cfg = "$env:TEMP\nw-curl.cfg"
[IO.File]::WriteAllText($cfg, "header = `"Authorization: Bearer $env:GITHUB_TOKEN`"`nheader = `"Accept: application/vnd.github+json`"", [Text.UTF8Encoding]::new($false))
curl.exe -sSL --retry 3 --config $cfg -o "$env:TEMP\nw-win.zip"   "https://api.github.com/repos/xk31415/NightWatch/actions/artifacts/<winId>/zip"
curl.exe -sSL --retry 3 --config $cfg -o "$env:TEMP\nw-linux.zip" "https://api.github.com/repos/xk31415/NightWatch/actions/artifacts/<linuxId>/zip"
Remove-Item $cfg
```

下载后**用脚本校验**，不要再靠眼睛数条目——手工四项耗时且容易抄错（条目数还会随依赖 dll 漂移）：

```powershell
.\tools\verify-artifacts.ps1 `
  -WinZip "$env:TEMP\nw-win.zip" -LinuxZip "$env:TEMP\nw-linux.zip" `
  -Tag v1.4.2
```

脚本逐项检查，任何一项不符就以**退出码 1** 失败并打印是哪一项：

- 根目录含可执行文件（Windows `NightWatch.exe`、Linux `NightWatch`）；
- **不含** `appsettings.json` / `appsettings.Development.json`（`appsettings.example*.json` 是给部署者抄的模板，允许保留）；
- 条目数与 SHA256：默认只要求条目数不低于下限（防空壳/截断 zip）并打印实际数量；要锁死就加 `-ExpectedWinEntries N -ExpectedLinuxEntries N`，依赖变化时同步更新期望值，别盲改；
- `-Tag` 时校验该 tag 是否落在 `main` 上（`git merge-base --is-ancestor`），非主干提交直接拒发。

脚本最后会打印两行「文件名 + 条目数 + SHA256」，直接抄进部署记录。

> **两个平台的条目数应当一致。** 早期 Linux 制品比 Windows 多一条 `logs/nightwatch-*.log`——那是启动冒烟在 `publish/logs/` 留下的运行日志被一起打包了。CI 现在会在冒烟成功后清掉 `publish/logs`，若再看到两边条目数不一致，先查这一条。

## 4. GitHub Release

```powershell
$payload = @{ tag_name = 'v1.3.1'; name = 'v1.3.1 — 主题'; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json
$rel = Invoke-RestMethod -Method Post -Uri 'https://api.github.com/repos/xk31415/NightWatch/releases' `
  -Headers $h -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json; charset=utf-8'

# 资产名带上版本号，方便直接给用户下载链接
Invoke-RestMethod -Method Post -Headers $h -ContentType 'application/zip' -InFile "$env:TEMP\nw-win.zip" `
  -Uri "https://uploads.github.com/repos/xk31415/NightWatch/releases/$($rel.id)/assets?name=NightWatch-v1.3.1-win-x64.zip"
```

发行说明建议包含：新增 / 修复 / 升级注意 / 验证 / 下载（文件 + SHA256）。模板见 [v1.3.0 的 Release](https://github.com/xk31415/NightWatch/releases/tag/v1.3.0)。

## 5. Gitee Release

**推荐直接用脚本**：`tools/create-gitee-release.ps1`（令牌从 `%USERPROFILE%\.nightwatch\gitee-token` 或 `-TokenFile` / 环境变量 `GITEE_TOKEN` 读取，不进命令行与日志；发行版已存在则复用、同名资产跳过；最后打印各资产 SHA256 供回验）。

```powershell
# 改完令牌文件后先空跑，确认 tag / 正文 / 资产路径
.\tools\create-gitee-release.ps1 -Tag v1.4.2 -Name "v1.4.2 — 主题" `
  -BodyFile .\release-notes.md `
  -Assets .\NightWatch-v1.4.2-win-x64.zip, .\NightWatch-v1.4.2-linux-x64.zip -DryRun

# 确认无误后正式执行（重复执行安全）
.\tools\create-gitee-release.ps1 -Tag v1.4.2 -BodyFile .\release-notes.md `
  -Assets .\NightWatch-v1.4.2-win-x64.zip, .\NightWatch-v1.4.2-linux-x64.zip
```

令牌文件只需在重置令牌后（或换机器 / 换 Windows 用户时）重写一次，用脚本更稳：

```powershell
.\tools\set-gitee-token.ps1      # 掩码输入 → 写文件（无 BOM、无换行）→ 收紧权限 → 调 /user 校验
```

手工写法（不想用脚本时）：

```powershell
$dir = "$env:USERPROFILE\.nightwatch"
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Content "$dir\gitee-token" "你的新令牌" -NoNewline -Encoding ascii
```

手工调 API 的做法（脚本不可用时）：

```powershell
$base = 'https://gitee.com/api/v5/repos/xk31415/NightWatch'
# 1) 先看列表判断是否已存在——releases/tags/{tag} 不存在时返回 null，不要用真值判断
$exists = (Invoke-RestMethod "$base/releases?per_page=50") | Where-Object { $_.tag_name -eq 'v1.3.1' }

# 2) 创建：手工拼表单 + UTF-8 字节，哈希表会被按非 UTF-8 编码导致中文乱码
$pairs = @(
  "access_token=$([uri]::EscapeDataString($env:GITEE_TOKEN))",
  'tag_name=v1.3.1',
  "name=$([uri]::EscapeDataString('v1.3.1 — 主题'))",
  "body=$([uri]::EscapeDataString($body))",
  'target_commitish=main',
  'prerelease=false'
)
$rel = Invoke-RestMethod -Method Post -Uri "$base/releases" `
  -Body ([Text.Encoding]::UTF8.GetBytes(($pairs -join '&'))) -ContentType 'application/x-www-form-urlencoded; charset=utf-8'

# 3) 附件：access_token 必须放在 query，放 form 字段会返回 401「登录失效」
#    用 HttpClient 的 multipart，避免 token 出现在命令行
```

## 6. 回验

四个下载（GitHub/Gitee × Windows/Linux）逐个算 SHA256，必须与发布前录的值一致；再用 Gitee 的 `/releases` 确认 `assets` 数量正确。

```powershell
# 对下载回来的 zip 复跑同一个脚本：内容项会再校一遍，SHA256 打在「汇总」里
.\tools\verify-artifacts.ps1 `
  -WinZip .\NightWatch-v1.4.2-win-x64.zip -LinuxZip .\NightWatch-v1.4.2-linux-x64.zip
```

四个 SHA256 必须**两两相同**（GitHub 与 Gitee 传的是同一份字节，本地那份就是基准）；不一致说明传输/上传出了问题，重下重传，别凑合发。

> 顺带：`-CheckRemotes` 可以比对 GitHub 与 Gitee 两边 `main` 的 sha（只读公开 API，不写任何数据），发布前后各跑一次，确认「两边同 sha」这条不再靠记忆。

## 7. 收尾

```powershell
git push <github-url> --delete <merged-branch>
git branch -d <merged-branch>
```

- 如果 Gitee 令牌出现在聊天记录或脚本里，去 **Gitee → 设置 → 私人令牌** 删除重建。
- 更新部署记录（版本、制品、SHA256、升级时间），见 `docs/deploy-log.md` 的格式（持续累积的部署记录）。
- 已部署实例升级：Windows 覆盖程序文件即可；Linux 解压后记得 `chmod +x`。
- 部署/升级完成后按 [post-deploy-checks.md](post-deploy-checks.md) 逐项核对（服务状态、告警通路、观察一个完整周期）。

## 已知坑（都是实测踩过的）

| 现象 | 原因 | 处理 |
|---|---|---|
| 提交信息/tag 消息开头多一个隐藏字符 | PS 5.1 `Out-File -Encoding utf8` 写 BOM | 用 `[IO.File]::WriteAllText(..., UTF8Encoding($false))` |
| `.ps1` 脚本报「字符串缺少终止符」 | PS 5.1 对无 BOM 的脚本按 GBK 解析 | 脚本存成 UTF-8 **with BOM** |
| 写 Markdown 时反引号消失、出现莫名换行 | PS 双引号字符串里 `` ` `` 是转义符（`` `n `` 会变换行） | 内容用单引号 here-string `@'...'@` |
| `git push --force-with-lease <url>` 报 stale info | 裸 URL 没有跟踪引用可比较 | 显式 `--force-with-lease=refs/heads/<b>:<远端sha>`，或先 `git fetch <url> <b>:refs/remotes/origin/<b>` |
| Gitee `attach_files` 返回 401「登录失效」 | token 放在了 multipart 表单里 | token 放 query：`attach_files?access_token=...` |
| Gitee 发行版「已存在」误判 | `releases/tags/{tag}` 不存在时返回 `null` 而不是 404 | 改用 `releases?per_page=50` 列表判断 |
| Gitee 返回的中文是乱码 | PS 5.1 按 Latin-1 解码响应 | 用 `curl -o` 落盘后 `[IO.File]::ReadAllText(path, UTF8)` |
| GitHub 资产下载连接超时 | `release-assets.githubusercontent.com` 偶发不可达 | `curl --retry 3 --connect-timeout 30` 重试 |
| 删掉某个 tag 后，对应的 Release 也消失了 | GitHub 的 Release 绑定在 tag 上，删除 tag 会连带删除引用它的 Release（`GET /releases/tags/{tag}` 变成 404） | 换 tag 类型（lightweight ↔ annotated）时，删 tag 后**必须重建 Release 并重新上传资产**；更省事的做法是一开始就只建 annotated tag（`git tag -a`），别用 API 建 lightweight 再改 |
| `git commit -m "..."` 报 `pathspec 'xxx' did not match any file(s)` | PS 5.1 会把提交信息里的 ASCII 双引号、甚至中文弯引号当成字符串定界符，把后半截消息拆成独立参数 | 提交信息一律写进文件再用 `git commit -F <消息文件>`，文件用无 BOM 的 UTF-8 写 |
| 发布包里混进真实配置 | Worker SDK 默认复制 `appsettings*.json` | csproj 已排除，且有 `EnsureNoLocalConfigInPublish` 守卫；CI 两平台都断言 `publish/` 不含本地配置，发布前再用 `tools/verify-artifacts.ps1` 复核 zip |
| 取制品时拿到「错的那次 CI」 | `workflow_runs[0]` 没过滤，可能命中 PR 触发的、还在跑的、甚至失败的 run | 用 `event=push&branch=main&status=success` 过滤，并断言 `conclusion -eq 'success'` 且 `head_sha` 与本地 HEAD 一致（见第 3 节） |
| CI 一直挂着不结束 | 某个用例或冒烟挂起，job 默认 6 小时超时 | job 已设 `timeout-minutes: 20`，测试带 `--blame-hang-timeout 120s`；失败时从 `test-results-win-x64` / `test-results-linux-x64` 产物里取 trx 与 Sequence 文件 |
| Linux 制品比 Windows 多一条 `logs/...` | 启动冒烟把运行日志写进 `publish/logs/`，随后被一起打包 | CI 已在冒烟成功后删除 `publish/logs`；手工打包时也别把 `logs/` 塞进去 |
| 本地 main 与远端不一致还照发 | 手抖推错、或一边推送失败 | 发布前后各跑一次 `.\tools\verify-artifacts.ps1 ... -CheckRemotes`，比对 GitHub/Gitee/本地三方 main 的 sha |

## 回滚

```powershell
# 本地与远端 tag
git tag -d v1.3.1
git push https://github.com/xk31415/NightWatch.git --delete v1.3.1
git push https://gitee.com/xk31415/NightWatch.git --delete v1.3.1

# Release：GitHub 先查 id 再 DELETE /releases/{id}，gitee DELETE /releases/{id}
```

> 删 tag 会连带删掉引用它的 GitHub Release。回滚时如果还要留发行版，先删 Release 再删 tag；反过来就会像上面「已知坑」里那条一样，得重建 Release 并重传资产。

已部署实例回滚：把上一个版本的 zip 解压覆盖程序文件（保留 `appsettings.json` 与 `logs`），重启服务。
