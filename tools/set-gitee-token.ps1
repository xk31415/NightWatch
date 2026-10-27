<#
.SYNOPSIS
    写入 / 更新本机 Gitee 令牌文件，并校验令牌有效。换机器、换 Windows 用户、重置令牌后跑一次即可。

.DESCRIPTION
    令牌来源优先级：-Token 参数 > 环境变量 GITEE_TOKEN > 交互输入（掩码，不回显）。
    写入默认路径 %USERPROFILE%\.nightwatch\gitee-token：单行、无 BOM、无换行。
    尽量把文件权限收紧到「当前用户 + SYSTEM + Administrators」。
    默认调用 Gitee GET /user 校验，只打印 login 与长度，绝不打印令牌本身。

.PARAMETER Token
    直接给出令牌。注意：会留在命令行历史/进程参数里，交互场景请省略此参数改用提示输入。

.PARAMETER Path
    令牌文件路径，默认 %USERPROFILE%\.nightwatch\gitee-token。

.PARAMETER SkipVerify
    跳过网络校验（离线或刚生成令牌还没配好网络时用）。

.PARAMETER Force
    已存在时直接覆盖，不再询问。

.EXAMPLE
    .\tools\set-gitee-token.ps1

.EXAMPLE
    # 自动化：从环境变量读取，覆盖且不校验网络
    $env:GITEE_TOKEN = 'xxx'
    .\tools\set-gitee-token.ps1 -Force -SkipVerify

.NOTES
    UTF-8 with BOM 保存：PS 5.1 对无 BOM 脚本按 GBK 解析，中文会报「字符串缺少终止符」。
#>
[CmdletBinding()]
param(
    [string]$Token,
    [string]$Path = (Join-Path $env:USERPROFILE '.nightwatch\gitee-token'),
    [switch]$SkipVerify,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host "错误: $Text" -ForegroundColor Red; exit 1 }

# ---------- 1. 取令牌 ----------
$source = $null
if (-not [string]::IsNullOrWhiteSpace($Token)) {
    $source = '-Token 参数'
}
elseif (-not [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
    $Token = $env:GITEE_TOKEN
    $source = '环境变量 GITEE_TOKEN'
}
else {
    Write-Step '粘贴 Gitee 令牌（输入不回显）'
    $secure = Read-Host -AsSecureString '令牌'
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $Token = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $source = '交互输入'
}

# 容错：BOM、首尾空白、被一起复制的引号（PS 5.1 的 Set-Content -Encoding utf8 会写 BOM，
# 而 Trim() 不去 U+FEFF，带 BOM 的令牌请求会 401）
$Token = $Token.Trim().TrimStart([char]0xFEFF).Trim().Trim('"', "'").Trim()
if ([string]::IsNullOrWhiteSpace($Token)) { Fail '令牌为空' }
if ($Token.Length -lt 20) { Fail ("令牌长度只有 {0}，疑似没复制完整" -f $Token.Length) }

Write-Step ("令牌来源: {0}，长度: {1}" -f $source, $Token.Length)

# ---------- 2. 落盘 ----------
$dir = Split-Path -Parent $Path
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

if ((Test-Path $Path) -and -not $Force) {
    $answer = Read-Host ("{0} 已存在，覆盖？(y/N)" -f $Path)
    if ($answer -notmatch '^(y|Y)') { Write-Host '已取消，未修改'; exit 1 }
}

[System.IO.File]::WriteAllText($Path, $Token, [System.Text.UTF8Encoding]::new($false))
Write-Step ("已写入 {0}（{1} 字节，无 BOM、无换行）" -f $Path, (Get-Item -LiteralPath $Path).Length)

# ---------- 3. 收紧权限 ----------
try {
    $me = "$env:USERDOMAIN\$env:USERNAME"
    & icacls $Path /inheritance:r /grant:r "${me}:F" /grant:r 'NT AUTHORITY\SYSTEM:F' /grant:r 'BUILTIN\Administrators:F' 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Step '权限已收紧为：当前用户 / SYSTEM / Administrators' }
    else { Write-Host '    权限收紧未成功（文件已写入用户目录，影响不大）' -ForegroundColor Yellow }
}
catch {
    Write-Host ("    权限收紧失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
}

# ---------- 4. 校验 ----------
if ($SkipVerify) {
    Write-Step '已跳过网络校验（-SkipVerify）'
    Write-Host ''
    Write-Host '提示：发版前可用 tools/create-gitee-release.ps1 -Tag <tag> -DryRun 再确认一次。'
    exit 0
}

$cfg = Join-Path $env:TEMP ("gitee-verify-" + [guid]::NewGuid().ToString('N') + ".cfg")
$out = Join-Path $env:TEMP ("gitee-user-" + [guid]::NewGuid().ToString('N') + ".json")
[System.IO.File]::WriteAllText(
    $cfg,
    "url = `"https://gitee.com/api/v5/user?access_token=$([uri]::EscapeDataString($Token))`"`nsilent`nshow-error",
    [System.Text.UTF8Encoding]::new($false))

try {
    Write-Step '调用 Gitee GET /user 校验'
    & curl.exe -sS --config $cfg -o $out
    if (-not (Test-Path $out)) { Fail '校验请求没有任何响应，检查网络后重试（或加 -SkipVerify 先跳过）' }

    $text = [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8)
    if ($text -match '"login"\s*:\s*"([^"]+)"') {
        $login = $Matches[1]
        Write-Step ("令牌有效 ✓  login={0}" -f $login)
        Write-Host ''
        Write-Host '下一步：'
        Write-Host '  发版时用 tools/create-gitee-release.ps1（自动读取该文件）'
        Write-Host '  或先空跑：.\tools\create-gitee-release.ps1 -Tag <tag> -DryRun'
        exit 0
    }

    Fail ("令牌无效或被拒绝。响应：{0}" -f $text)
}
finally {
    Remove-Item $cfg -Force -ErrorAction SilentlyContinue
    Remove-Item $out -Force -ErrorAction SilentlyContinue
}
