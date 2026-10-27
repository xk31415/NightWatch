<#
.SYNOPSIS
    创建 Gitee 发行版并上传资产。令牌从本地文件读取，不出现在命令行、日志或对话记录里。

.DESCRIPTION
    给 NightWatch 发布用：先建发行版（已存在则复用），再逐个上传资产，最后打印名称 / 大小 / SHA256 便于回验。
    令牌来源优先级：-TokenFile 参数 > 环境变量 GITEE_TOKEN > %USERPROFILE%\.nightwatch\gitee-token。

    一次性准备令牌文件（重置令牌后只需重做这一步）：
        $dir = "$env:USERPROFILE\.nightwatch"
        New-Item -ItemType Directory -Force $dir | Out-Null
        Set-Content "$dir\gitee-token" "你的新令牌" -NoNewline -Encoding ascii

.PARAMETER Tag
    版本 tag，必须已推送到 Gitee（例如 v1.4.1）。

.PARAMETER Name
    发行版标题，默认与 tag 相同。

.PARAMETER Body
    发行说明正文（字符串）。

.PARAMETER BodyFile
    发行说明正文文件（UTF-8）。与 -Body 二选一，-BodyFile 优先。

.PARAMETER Assets
    要上传的本地文件路径（可多个）。已上传同名资产的会跳过。

.PARAMETER Repo
    仓库全名，默认 xk31415/NightWatch。

.PARAMETER DryRun
    只打印将要执行的动作，不发起任何写请求。

.EXAMPLE
    .\tools\create-gitee-release.ps1 -Tag v1.4.2 `
        -Name "v1.4.2 — 一句话说明" `
        -BodyFile .\release-notes.md `
        -Assets .\NightWatch-v1.4.2-win-x64.zip, .\NightWatch-v1.4.2-linux-x64.zip

.EXAMPLE
    # 先空跑确认参数与资产路径
    .\tools\create-gitee-release.ps1 -Tag v1.4.2 -Assets .\a.zip -DryRun

.NOTES
    本文件必须以 UTF-8 with BOM 保存：PowerShell 5.1 会把无 BOM 脚本按 GBK 解析，中文报「字符串缺少终止符」。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Name,
    [string]$Body,
    [string]$BodyFile,
    [string[]]$Assets = @(),
    [string]$Repo = 'xk31415/NightWatch',
    [string]$TokenFile,
    [switch]$Prerelease,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$base = "https://gitee.com/api/v5/repos/$Repo"

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host "错误: $Text" -ForegroundColor Red; exit 1 }

function Get-ResponseFile {
    Join-Path $env:TEMP ("gitee-" + [guid]::NewGuid().ToString('N') + ".json")
}

# 容错：去掉 BOM、首尾空白，以及可能被一起复制的引号
# （PS 5.1 的 Set-Content -Encoding utf8 会写 BOM，而 Trim() 不去 BOM，带 BOM 的令牌会 401）
function Read-TokenFile([string]$Path) {
    $raw = [System.IO.File]::ReadAllText($Path)
    return $raw.Trim().TrimStart([char]0xFEFF).Trim().Trim('"', "'").Trim()
}

# ---------- 1. 令牌 ----------
$token = $null
$tokenSource = $null
if ($TokenFile) {
    if (-not (Test-Path $TokenFile)) { Fail "找不到令牌文件: $TokenFile" }
    $token = Read-TokenFile $TokenFile
    $tokenSource = $TokenFile
}
elseif ($env:GITEE_TOKEN) {
    $token = $env:GITEE_TOKEN.Trim().TrimStart([char]0xFEFF).Trim().Trim('"', "'").Trim()
    $tokenSource = '环境变量 GITEE_TOKEN'
}
else {
    $defaultPath = Join-Path $env:USERPROFILE '.nightwatch\gitee-token'
    if (Test-Path $defaultPath) {
        $token = Read-TokenFile $defaultPath
        $tokenSource = $defaultPath
    }
}

if ([string]::IsNullOrWhiteSpace($token)) {
    Fail @"
缺少 Gitee 令牌。请先写令牌文件（重置令牌后重做一次即可）：
    `$dir = "`$env:USERPROFILE\.nightwatch"
    New-Item -ItemType Directory -Force `$dir | Out-Null
    Set-Content "`$dir\gitee-token" "你的新令牌" -NoNewline -Encoding ascii
"@
}

Write-Step ("令牌已读取（长度 {0}，来源 {1}）" -f $token.Length, $tokenSource)

# ---------- 2. 正文 ----------
if ($BodyFile) {
    if (-not (Test-Path $BodyFile)) { Fail "找不到正文文件: $BodyFile" }
    $Body = [System.IO.File]::ReadAllText($BodyFile)
}
if ([string]::IsNullOrWhiteSpace($Body)) { $Body = $Tag }
if ([string]::IsNullOrWhiteSpace($Name)) { $Name = $Tag }

# ---------- 3. 资产预检 ----------
$assetList = @()
foreach ($a in $Assets) {
    if (-not (Test-Path $a)) { Fail "找不到资产文件: $a" }
    $full = (Resolve-Path -LiteralPath $a).Path
    $assetList += [pscustomobject]@{
        Path = $full
        Name = [System.IO.Path]::GetFileName($full)
        Size = (Get-Item -LiteralPath $full).Length
        Sha  = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLower()
    }
}

if ($DryRun) {
    Write-Step "DryRun：以下动作不会真正执行"
    Write-Host ("  仓库      : {0}" -f $Repo)
    Write-Host ("  tag       : {0}" -f $Tag)
    Write-Host ("  标题      : {0}" -f $Name)
    Write-Host ("  正文长度  : {0} 字符" -f $Body.Length)
    Write-Host ("  预发布    : {0}" -f $Prerelease.IsPresent)
    Write-Host ("  资产数量  : {0}" -f $assetList.Count)
    foreach ($x in $assetList) {
        Write-Host ("    - {0}  {1:N0} 字节  SHA256 {2}" -f $x.Name, $x.Size, $x.Sha)
    }
    exit 0
}

# ---------- 4. 已存在则复用 ----------
function Invoke-Gitee([string]$Method, [string]$Url, [string]$OutFile, [string[]]$ExtraArgs = @()) {
    $args = @('-sS', '-X', $Method, '-o', $OutFile, '-w', '%{http_code}') + $ExtraArgs + @($Url)
    $out = & curl.exe @args
    return [int](($out | Select-Object -Last 1) -replace '\s', '')
}

$existingId = $null
$existingAssets = @()
Write-Step "查询是否已有 $Tag 的发行版"
$listFile = Get-ResponseFile
try {
    $code = Invoke-Gitee 'GET' "$base/releases?per_page=100" $listFile
    if ($code -ne 200) { Fail "查询发行版列表失败（HTTP $code）" }
    $all = ([System.IO.File]::ReadAllText($listFile, [System.Text.Encoding]::UTF8)) | ConvertFrom-Json
    $hit = @($all) | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
    if ($hit) {
        $existingId = $hit.id
        $existingAssets = @($hit.assets | ForEach-Object { $_.name })
        Write-Step ("已存在发行版 id={0}，现有资产 {1} 个" -f $existingId, $existingAssets.Count)
    }
}
finally { Remove-Item $listFile -Force -ErrorAction SilentlyContinue }

# ---------- 5. 创建发行版 ----------
if (-not $existingId) {
    $pairs = @(
        "access_token=$([uri]::EscapeDataString($token))",
        "tag_name=$([uri]::EscapeDataString($Tag))",
        "name=$([uri]::EscapeDataString($Name))",
        "body=$([uri]::EscapeDataString($Body))",
        'target_commitish=main',
        ("prerelease=" + $Prerelease.IsPresent.ToString().ToLowerInvariant())
    )
    $bodyPath = Join-Path $env:TEMP ("gitee-form-" + [guid]::NewGuid().ToString('N') + ".txt")
    [System.IO.File]::WriteAllText($bodyPath, ($pairs -join '&'), [System.Text.ASCIIEncoding]::new())
    $respFile = Get-ResponseFile
    try {
        Write-Step "创建发行版 $Tag"
        $code = Invoke-Gitee 'POST' "$base/releases" $respFile @('--data-binary', "@$bodyPath", '-H', 'Content-Type: application/x-www-form-urlencoded; charset=utf-8')
        if ($code -lt 200 -or $code -ge 300) {
            $detail = if (Test-Path $respFile) { [System.IO.File]::ReadAllText($respFile, [System.Text.Encoding]::UTF8) } else { '' }
            Fail "创建发行版失败（HTTP $code）：$detail"
        }
        $created = [System.IO.File]::ReadAllText($respFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $existingId = $created.id
        Write-Step ("创建成功 id={0}" -f $existingId)
    }
    finally {
        Remove-Item $bodyPath -Force -ErrorAction SilentlyContinue
        Remove-Item $respFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------- 6. 上传资产 ----------
$uploaded = @()
foreach ($x in $assetList) {
    if ($existingAssets -contains $x.Name) {
        Write-Step ("跳过已存在的资产 {0}" -f $x.Name)
        $uploaded += $x
        continue
    }

    $ok = $false
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $respFile = Get-ResponseFile
        try {
            $url = "$base/releases/$existingId/attach_files?access_token=$([uri]::EscapeDataString($token))"
            $code = Invoke-Gitee 'POST' $url $respFile @('-F', "file=@$($x.Path)")
            if ($code -ge 200 -and $code -lt 300) {
                Write-Step ("上传成功 {0}（HTTP {1}）" -f $x.Name, $code)
                $ok = $true
                break
            }
            $detail = if (Test-Path $respFile) { [System.IO.File]::ReadAllText($respFile, [System.Text.Encoding]::UTF8) } else { '' }
            Write-Host ("    第 {0} 次失败（HTTP {1}）：{2}" -f $attempt, $code, $detail) -ForegroundColor Yellow
        }
        catch {
            Write-Host ("    第 {0} 次失败：{1}" -f $attempt, $_.Exception.GetBaseException().Message) -ForegroundColor Yellow
        }
        finally { Remove-Item $respFile -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds (5 * $attempt)
    }

    if (-not $ok) { Fail ("上传 {0} 失败（已重试 4 次）" -f $x.Name) }
    $uploaded += $x
}

# ---------- 7. 结果核对 ----------
$verifyFile = Get-ResponseFile
try {
    $code = Invoke-Gitee 'GET' "$base/releases?per_page=100" $verifyFile
    if ($code -ne 200) { Fail "复核失败（HTTP $code）" }
    $all = ([System.IO.File]::ReadAllText($verifyFile, [System.Text.Encoding]::UTF8)) | ConvertFrom-Json
    $rel = @($all) | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
    if (-not $rel) { Fail "复核未找到 $Tag 的发行版" }

    Write-Host ''
    Write-Step ("发行版已就绪: {0}" -f $Tag)
    Write-Host ("  id       : {0}" -f $rel.id)
    Write-Host ("  标题     : {0}" -f $rel.name)
    Write-Host ("  资产     : {0} 个" -f @($rel.assets).Count)
    foreach ($a in $rel.assets) { Write-Host ("    - {0}  {1:N1} KB" -f $a.name, ($a.size / 1KB)) }
    Write-Host ''
    Write-Host '本地文件校验值（发布说明里建议照抄）:'
    foreach ($x in $uploaded) { Write-Host ("  {0}`n    SHA256 {1}" -f $x.Name, $x.Sha) }
    Write-Host ''
    Write-Host ("发行版页面: https://gitee.com/{0}/releases/tag/{1}" -f $Repo, $Tag)
}
finally { Remove-Item $verifyFile -Force -ErrorAction SilentlyContinue }

exit 0
