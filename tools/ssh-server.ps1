<#
.SYNOPSIS
    通过 SSH 访问 NightWatch 生产机（连接信息来自本地不入库的配置文件）。

.DESCRIPTION
    主机地址 / 用户名 / 端口 / 私钥路径统一放在 %USERPROFILE%\.nightwatch\server.json，
    该文件**不入库**；仓库与文档里不允许出现 IP 与密钥内容，docs 中保持脱敏。

    默认只读：脚本会拒绝停止/启动服务、删除或覆盖文件、修改权限等破坏性命令。
    确需执行时必须显式加 -AllowDestructive，且应先得到用户同意。

.PARAMETER Command
    在服务器上执行的命令（cmd.exe 语义）。

.PARAMETER Interactive
    打开交互式 SSH 会话。

.PARAMETER Upload
    上传本地文件到服务器（配合 -RemoteDir）。

.PARAMETER RemoteDir
    上传目标目录（默认 C:\Windows\Temp）。

.PARAMETER AllowDestructive
    允许执行可能改变服务器状态的命令（停止/启动服务、删改文件、改 ACL 等）。

.PARAMETER DryRun
    只打印将要执行的命令（主机名打码），不实际连接。

.EXAMPLE
    .\tools\ssh-server.ps1 -Command "sc query NightWatch"

.EXAMPLE
    .\tools\ssh-server.ps1 -Command "icacls C:\AppData\NightWatch"

.EXAMPLE
    .\tools\ssh-server.ps1 -Upload .\NightWatch-v1.4.3-win-x64.zip -RemoteDir C:\Windows\Temp

.EXAMPLE
    .\tools\ssh-server.ps1 -Interactive
#>
[CmdletBinding(DefaultParameterSetName = 'Command')]
param(
    [Parameter(ParameterSetName = 'Command', Mandatory = $true, Position = 0)]
    [string]$Command,

    [Parameter(ParameterSetName = 'Interactive', Mandatory = $true)]
    [switch]$Interactive,

    [Parameter(ParameterSetName = 'Upload', Mandatory = $true)]
    [string]$Upload,

    [Parameter(ParameterSetName = 'Upload')]
    [string]$RemoteDir = 'C:\Windows\Temp',

    [switch]$AllowDestructive,
    [switch]$DryRun,

    [string]$ConfigFile = (Join-Path $env:USERPROFILE '.nightwatch\server.json')
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Text) { Write-Host "错误: $Text" -ForegroundColor Red; exit 1 }
function Mask-Host([string]$HostName) {
    $p = $HostName.Split('.')
    if ($p.Count -eq 4) { return "$($p[0]).$($p[1]).x.x" }
    return '***'
}

if (-not (Test-Path $ConfigFile)) {
    Fail @"
找不到服务器连接配置: $ConfigFile
请创建（内容不入库）：
  @{ host = '<服务器地址>'; user = 'Administrator'; port = 22; key = '<私钥路径>' } | ConvertTo-Json | Set-Content '$ConfigFile' -Encoding ascii
"@
}

$cfg = Get-Content $ConfigFile -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($cfg.host)) { Fail "配置缺少 host: $ConfigFile" }
$sshUser = if ($cfg.user) { $cfg.user } else { 'Administrator' }
$sshPort = if ($cfg.port) { [int]$cfg.port } else { 22 }
$sshKey = $cfg.key
if (-not $sshKey -or -not (Test-Path $sshKey)) { Fail "私钥不存在: $sshKey" }

$target = "$sshUser@$($cfg.host)"
$masked = "$sshUser@$(Mask-Host $cfg.host):$sshPort"
$targetMasked = "$sshUser@$(Mask-Host $cfg.host)"

# 破坏性命令守卫（默认只读）
$destructive = '(?i)(\bsc(\.exe)?\s+(stop|start|config|delete|create)\b|\bnet\s+(stop|start)\b|\bStop-Service\b|\bStart-Service\b|\bRestart-Service\b|\bRemove-Item\b|\bdel\b|\berase\b|\brmdir\b|\brd\b|\bRemove-Item\b|\bcopy\b|\bxcopy\b|\brobocopy\b|\bmove\b|\bren\b|\bicacls\b.*(/grant|/remove|/reset|/setowner)|\btakeown\b|\btar\b\s+-[xf]|\bExpand-Archive\b|\bcertutil\b.*-addstore|\breg\s+(add|delete)\b)'
if ($Command -and $Command -match $destructive -and -not $AllowDestructive) {
    Write-Host "拒绝执行：命令看起来会改变服务器状态。" -ForegroundColor Yellow
    Write-Host "  命令: $Command"
    Write-Host "  如确需执行，加 -AllowDestructive，并先取得用户明确同意。"
    exit 2
}

$sshOpts = @('-i', $sshKey, '-p', "$sshPort", '-o', 'ConnectTimeout=15', '-o', 'StrictHostKeyChecking=accept-new')

if ($DryRun) {
    Write-Host "==> [DryRun] 目标: $masked" -ForegroundColor Cyan
    switch ($PSCmdlet.ParameterSetName) {
        'Command'   { Write-Host "    ssh $($sshOpts -join ' ') $targetMasked `"$Command`"" }
        'Upload'    { Write-Host "    scp $($sshOpts -join ' ') $Upload $targetMasked`:$RemoteDir" }
        'Interactive' { Write-Host "    ssh $($sshOpts -join ' ') $targetMasked" }
    }
    exit 0
}

switch ($PSCmdlet.ParameterSetName) {
    'Command' {
        Write-Host "==> $masked" -ForegroundColor Cyan
        & ssh @sshOpts -o BatchMode=$true $target $Command
        exit $LASTEXITCODE
    }
    'Upload' {
        if (-not (Test-Path $Upload)) { Fail "本地文件不存在: $Upload" }
        Write-Host "==> 上传 $(Split-Path $Upload -Leaf) -> $masked`:$RemoteDir" -ForegroundColor Cyan
        & scp @sshOpts $Upload "$target`:$RemoteDir"
        exit $LASTEXITCODE
    }
    'Interactive' {
        Write-Host "==> 交互登录 $masked（exit 退出）" -ForegroundColor Cyan
        & ssh @sshOpts $target
        exit $LASTEXITCODE
    }
}
