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

参考基线（v1.3.0）：Windows 179 通过 / 2 跳过；Linux 163 通过 / 18 跳过（ACL/SID 用例自动跳过）。构建应 0 警告 0 错误。

## 2. 打 tag 并推送

```powershell
# PS 5.1 的 Out-File -Encoding utf8 会写 BOM，git 仓库里会出现 \uFEFF 开头的 tag 消息。
# 统一用 .NET API 写无 BOM 的 UTF-8。
$msg = @'
v1.3.1 — 一句话主题

- 变更点 1
- 变更点 2

单测 181 通过。
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
$run = (Invoke-RestMethod "https://api.github.com/repos/xk31415/NightWatch/actions/runs?head_sha=$sha" -Headers $h).workflow_runs[0]
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

校验四项，任何一项不符就别发布：

- 条目数（Windows 20 / Linux 21）
- 含可执行文件（Windows `NightWatch.exe`、Linux `NightWatch`）
- **不含** `appsettings.json` / `appsettings.Development.json`
- 记录 SHA256（`Get-FileHash <zip> -Algorithm SHA256`）

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

令牌文件只需在重置令牌后重写一次：

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

四个下载（GitHub/Gitee × Windows/Linux）逐个算 SHA256，必须与本地一致；再用 Gitee 的 `/releases` 确认 `assets` 数量正确。

## 7. 收尾

```powershell
git push <github-url> --delete <merged-branch>
git branch -d <merged-branch>
```

- 如果 Gitee 令牌出现在聊天记录或脚本里，去 **Gitee → 设置 → 私人令牌** 删除重建。
- 更新部署记录（版本、制品、SHA256、升级时间），见 `docs/deploy-2026-10-25.md` 的格式。
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
| 发布包里混进真实配置 | Worker SDK 默认复制 `appsettings*.json` | csproj 已排除，且有 `EnsureNoLocalConfigInPublish` 守卫会直接让发布失败 |

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
