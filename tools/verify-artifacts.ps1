<#
.SYNOPSIS
    发布制品校验：可执行文件、本地配置泄漏、条目数、SHA256、tag 是否在 main 上、双平台 main sha 是否一致。

.DESCRIPTION
    发布清单里这几项原先是手工抄数字/凭眼睛看，容易漏、也容易抄错（条目数会随依赖 dll 漂移）。
    本脚本把第 3 节的「四项校验」和第 6 节的「双平台 sha 对齐」变成一次可重复执行的检查：
    任一项不符就以退出码 1 失败，并打印具体是哪一项。

    校验项：
      1) zip 根目录含可执行文件（Windows: NightWatch.exe；Linux: NightWatch）
      2) zip 内不含本地配置 appsettings.json / appsettings.Development.json
         （appsettings.example*.json 是给部署者抄的模板，允许保留）
      3) 记录条目数 + SHA256；给了 -ExpectedWinEntries / -ExpectedLinuxEntries 就严格比对，
         否则只校验条目数不低于 -MinEntries，防「空壳/截断 zip」
      4) 给了 -Tag：git merge-base --is-ancestor <tag> <MainBranch> 必须成立
      5) 给了 -CheckRemotes：GitHub 与 Gitee 的 main 必须同一 sha（必要时与本地 main 对照）

.PARAMETER WinZip
    Windows 制品的 zip 路径（NightWatch-vX.Y.Z-win-x64.zip）。

.PARAMETER LinuxZip
    Linux 制品的 zip 路径（NightWatch-vX.Y.Z-linux-x64.zip）。

.PARAMETER Tag
    可选。给了就校验该 tag 是否落在 main 上（发布只允许从 main 打 tag）。

.PARAMETER MainBranch
    主干分支名，默认 main。

.PARAMETER CheckRemotes
    可选。调 GitHub / Gitee 公开 API 比对两边 main 的 sha（不写任何数据，只读）。

.PARAMETER GitHubRepo
    GitHub 仓库全名，默认 xk31415/NightWatch。

.PARAMETER GiteeRepo
    Gitee 仓库全名，默认 xk31415/NightWatch。

.PARAMETER ExpectedWinEntries
    Windows 制品的期望条目数；0（默认）= 不严格比对，只记数并校验下限。

.PARAMETER ExpectedLinuxEntries
    Linux 制品的期望条目数；0（默认）= 不严格比对，只记数并校验下限。

.PARAMETER MinEntries
    条目数下限，默认 5。用于发现空壳或被截断的 zip。

.EXAMPLE
    # 最小用法：只看两个 zip 的内容与哈希
    .\tools\verify-artifacts.ps1 -WinZip "$env:TEMP\nw-win.zip" -LinuxZip "$env:TEMP\nw-linux.zip"

.EXAMPLE
    # 发布前完整校验：制品 + tag 在 main 上 + 两平台 main 对齐
    .\tools\verify-artifacts.ps1 `
        -WinZip .\NightWatch-v1.4.2-win-x64.zip `
        -LinuxZip .\NightWatch-v1.4.2-linux-x64.zip `
        -Tag v1.4.2 -CheckRemotes

.EXAMPLE
    # 只校验 Windows 制品（另一个平台还没下载下来时）
    .\tools\verify-artifacts.ps1 -WinZip "$env:TEMP\nw-win.zip"

.NOTES
    本文件必须以 UTF-8 with BOM 保存：PowerShell 5.1 会把无 BOM 脚本按 GBK 解析，中文报「字符串缺少终止符」。
    退出码：0 = 全部通过；1 = 至少一项不符（逐项打印原因）。
#>
[CmdletBinding()]
param(
    [string]$WinZip,
    [string]$LinuxZip,
    [string]$Tag,
    [string]$MainBranch = 'main',
    [switch]$CheckRemotes,
    [string]$GitHubRepo = 'xk31415/NightWatch',
    [string]$GiteeRepo = 'xk31415/NightWatch',
    [int]$ExpectedWinEntries = 0,
    [int]$ExpectedLinuxEntries = 0,
    [int]$MinEntries = 5
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:failures = @()
$script:checked = 0
$script:artifacts = @()

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Stop-Fatal([string]$Text) { Write-Host "错误: $Text" -ForegroundColor Red; exit 1 }
function Pass([string]$Text) { $script:checked++; Write-Host "  [OK]   $Text" -ForegroundColor Green }
function Add-Failure([string]$Text) { $script:failures += $Text; Write-Host "  [FAIL] $Text" -ForegroundColor Red }

# ---------- 单个 zip 的校验 ----------
function Test-Artifact {
    param(
        [string]$Path,
        [string]$Label,
        [string]$ExeName,
        [int]$ExpectedEntries
    )

    Write-Step ("{0} 制品: {1}" -f $Label, $Path)
    $full = (Resolve-Path -LiteralPath $Path).Path
    $size = (Get-Item -LiteralPath $full).Length
    $sha = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()

    $zip = $null
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($full)
        # 统一成 / 分隔，避免 zip 用反斜杠时路径判断失效
        $entries = @($zip.Entries | ForEach-Object { ($_.FullName -replace '\\', '/') })
    }
    catch {
        Add-Failure ("{0}: 不是可读的 zip（{1}）" -f $Label, $_.Exception.Message)
        return
    }
    finally {
        if ($zip) { $zip.Dispose() }
    }

    $count = $entries.Count
    Write-Host ("  大小   : {0:N2} MB ({1} bytes)" -f ($size / 1MB), $size)
    Write-Host ("  条目数 : {0}" -f $count)
    Write-Host ("  SHA256 : {0}" -f $sha)

    # 1) 可执行文件必须在根目录（发布 zip 是 publish 目录的平铺）
    if ($entries -ccontains $ExeName) {
        Pass ("{0}: 含可执行文件 {1}" -f $Label, $ExeName)
    }
    else {
        $similar = @($entries | Where-Object { $_ -clike "*$ExeName" })
        $hint = ''
        if ($similar.Count -gt 0) { $hint = ("（发现疑似路径 {0}，但不在 zip 根目录）" -f ($similar -join ', ')) }
        Add-Failure ("{0}: 缺少根目录可执行文件 {1}{2}" -f $Label, $ExeName, $hint)
    }

    # 2) 本地配置泄漏（含真实凭据，解压会覆盖服务器生产配置）
    $configNames = @($entries |
        ForEach-Object { [System.IO.Path]::GetFileName($_) } |
        Where-Object { $_ -like 'appsettings*.json' } |
        Sort-Object -Unique)
    $leaks = @($configNames | Where-Object { $_ -notlike 'appsettings.example*' })
    if ($leaks.Count -eq 0) {
        if ($configNames.Count -gt 0) {
            Pass ("{0}: 不含本地配置（仅示例 {1}）" -f $Label, ($configNames -join ', '))
        }
        else {
            Pass ("{0}: 不含任何 appsettings*.json" -f $Label)
        }
    }
    else {
        Add-Failure ("{0}: 含本地配置 {1}（会覆盖服务器上的生产配置，禁止发布）" -f $Label, ($leaks -join ', '))
    }

    # 3) 条目数：期望值优先，否则校验下限
    if ($ExpectedEntries -gt 0) {
        if ($count -eq $ExpectedEntries) {
            Pass ("{0}: 条目数 {1} 与期望一致" -f $Label, $count)
        }
        else {
            Add-Failure ("{0}: 条目数 {1}，期望 {2}（依赖变化时同步更新期望值，别盲改）" -f $Label, $count, $ExpectedEntries)
        }
    }
    elseif ($count -lt $MinEntries) {
        Add-Failure ("{0}: 条目数只有 {1}，低于下限 {2}，制品可能是空壳或被截断" -f $Label, $count, $MinEntries)
    }
    else {
        Pass ("{0}: 条目数 {1}（不低于下限 {2}）" -f $Label, $count, $MinEntries)
    }

    $script:artifacts += [pscustomobject]@{
        Label = $Label
        Name  = [System.IO.Path]::GetFileName($full)
        Sha   = $sha
        Count = $count
        Size  = $size
    }
}

# ---------- tag 是否落在主干上 ----------
function Test-TagAncestry {
    param([string]$TagName, [string]$Branch)

    Write-Step ("tag 是否从 {0} 打出: {1}" -f $Branch, $TagName)

    $inside = (@(& git rev-parse --is-inside-work-tree 2>$null) -join '').Trim()
    if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') {
        Add-Failure '当前目录不在 git 仓库内，无法校验 tag 与主干的关系'
        return
    }

    & git rev-parse -q --verify ("refs/tags/{0}" -f $TagName) *> $null
    if ($LASTEXITCODE -ne 0) {
        Add-Failure ("找不到 tag {0}（先 git fetch --tags）" -f $TagName)
        return
    }

    # 主干引用按「本地分支 → origin 分支 → 任意 rev」依次解析：
    # detached HEAD 的检出通常没有本地 main；这样也便于用 tag/其他 rev 做负例。
    $branchCandidates = @(
        ("refs/heads/{0}" -f $Branch),
        ("refs/remotes/origin/{0}" -f $Branch),
        ("{0}^{{commit}}" -f $Branch)
    )
    $branchRef = $null
    foreach ($candidate in $branchCandidates) {
        & git rev-parse -q --verify $candidate *> $null
        if ($LASTEXITCODE -eq 0) { $branchRef = $candidate; break }
    }
    if (-not $branchRef) {
        Add-Failure ("找不到主干引用 {0}（先 git fetch，或改用 -MainBranch）" -f $Branch)
        return
    }
    if ($branchRef -ne ("refs/heads/{0}" -f $Branch)) {
        Write-Host ("  主干引用解析为 {0}（不是本地分支，可能处于 detached HEAD）" -f $branchRef) -ForegroundColor DarkGray
    }

    & git merge-base --is-ancestor $TagName $branchRef *> $null
    $code = $LASTEXITCODE
    if ($code -eq 0) {
        Pass ("tag {0} 是 {1} 的祖先（tag 从主干打出）" -f $TagName, $Branch)
    }
    elseif ($code -eq 1) {
        Add-Failure ("tag {0} 不在 {1} 上：不能发布非主干提交" -f $TagName, $Branch)
    }
    else {
        Add-Failure ("git merge-base 执行失败（退出码 {0}）" -f $code)
    }
}

# ---------- 双平台 main sha 一致性（只读公开 API） ----------
function Test-RemoteMainSha {
    param([string]$GhRepo, [string]$GeRepo)

    Write-Step '双平台 main sha 一致性'

    $ghSha = $null
    try {
        $gh = Invoke-RestMethod -Uri ("https://api.github.com/repos/{0}/git/ref/heads/main" -f $GhRepo) `
            -Headers @{ 'User-Agent' = 'NightWatch-Release'; 'Accept' = 'application/vnd.github+json' } `
            -TimeoutSec 30
        $ghSha = $gh.object.sha
    }
    catch {
        Add-Failure ("GitHub main 查询失败（{0}）" -f $_.Exception.Message)
        return
    }

    $geSha = $null
    try {
        $ge = Invoke-RestMethod -Uri ("https://gitee.com/api/v5/repos/{0}/branches/main" -f $GeRepo) -TimeoutSec 30
        $geSha = $ge.commit.sha
    }
    catch {
        Add-Failure ("Gitee main 查询失败（{0}）" -f $_.Exception.Message)
        return
    }

    Write-Host ("  GitHub main : {0}" -f $ghSha)
    Write-Host ("  Gitee  main : {0}" -f $geSha)

    if ($ghSha -and $geSha -and ($ghSha -eq $geSha)) {
        Pass '两平台 main 同 sha'
    }
    else {
        Add-Failure '两平台 main sha 不一致：先让两边对齐再发布'
    }

    $local = (@(& git rev-parse -q --verify refs/heads/main 2>$null) -join '').Trim()
    if ($LASTEXITCODE -eq 0 -and $local) {
        Write-Host ("  本地   main : {0}" -f $local)
        if ($local -ne $ghSha) {
            Add-Failure ("本地 main 与 GitHub main 不一致（本地 {0}）" -f $local)
        }
        else {
            Pass '本地 main 与远端一致'
        }
    }
}

# ---------- 主流程 ----------
if (-not $WinZip -and -not $LinuxZip) {
    Stop-Fatal '至少给一个制品：-WinZip <path> 和/或 -LinuxZip <path>（详见 Get-Help .\tools\verify-artifacts.ps1 -Full）'
}

if ($WinZip) {
    if (-not (Test-Path -LiteralPath $WinZip)) { Stop-Fatal ("找不到 Windows 制品: {0}" -f $WinZip) }
    Test-Artifact -Path $WinZip -Label 'Windows' -ExeName 'NightWatch.exe' -ExpectedEntries $ExpectedWinEntries
}

if ($LinuxZip) {
    if (-not (Test-Path -LiteralPath $LinuxZip)) { Stop-Fatal ("找不到 Linux 制品: {0}" -f $LinuxZip) }
    Test-Artifact -Path $LinuxZip -Label 'Linux' -ExeName 'NightWatch' -ExpectedEntries $ExpectedLinuxEntries
}

if ($Tag) { Test-TagAncestry -TagName $Tag -Branch $MainBranch }
if ($CheckRemotes) { Test-RemoteMainSha -GhRepo $GitHubRepo -GeRepo $GiteeRepo }

# ---------- 汇总（可直接抄进部署记录） ----------
Write-Step '汇总'
foreach ($a in $script:artifacts) {
    Write-Host ("  {0,-8} {1}  条目 {2}  {3}" -f $a.Label, $a.Name, $a.Count, $a.Sha)
}

if ($script:failures.Count -eq 0) {
    Write-Host ("全部校验通过（{0} 项）" -f $script:checked) -ForegroundColor Green
    exit 0
}

Write-Host ("校验失败：{0} 项不符（通过 {1} 项）" -f $script:failures.Count, $script:checked) -ForegroundColor Red
$i = 0
foreach ($f in $script:failures) {
    $i++
    Write-Host ("  {0}. {1}" -f $i, $f) -ForegroundColor Red
}
exit 1
