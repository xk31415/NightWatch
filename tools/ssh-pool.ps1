<#
.SYNOPSIS
    通过本地 SSH 连接池访问生产机（一条长连接，避免频繁新建连接触发服务器限流）。

.DESCRIPTION
    `tools/ssh-server.ps1` 每次调用都会新建一条 SSH 连接；短时间内连续调用几次就可能被
    服务器/防火墙限流（表现为 `Connection reset by ... port 22`，exit 255）。

    本脚本改用 `%USERPROFILE%\.nightwatch\ssh_daemon.py` 这个常驻连接池：
    * 只有一条 SSH 连接，带 keepalive，断了自动退避重连；
    * 命令经本地 127.0.0.1:19555 转发，不再新建 SSH；
    * 命令内容写入临时文件再发送，避免多层引号被解析坏。

    局部依赖（不入库，需一次性准备）：
    * python + `pip install --user paramiko`
    * `%USERPROFILE%\.nightwatch\ssh_daemon.py`（serve / ask / put）
    * `%USERPROFILE%\.nightwatch\ask_file.py`（命令走文件的小客户端）

.PARAMETER Command
    在服务器上执行的命令（cmd.exe 语义）。

.PARAMETER TimeoutSeconds
    远程命令超时，默认 600。

.PARAMETER AllowDestructive
    允许执行可能改变服务器状态的命令（停止/启动服务、删改文件、改 ACL 等）。
    默认只读：命中破坏性命令会拒绝执行并返回 exit 2。

.PARAMETER NoAutoStart
    连接池未运行时不要自动启动，直接报错。

.EXAMPLE
    .\tools\ssh-pool.ps1 -Command "sc query NightWatch"

.EXAMPLE
    .\tools\ssh-pool.ps1 -Command 'del /f /q "C:\Windows\Temp\nw-v142.zip"' -AllowDestructive
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Command,

    [int]$TimeoutSeconds = 600,
    [switch]$AllowDestructive,
    [switch]$NoAutoStart
)

$ErrorActionPreference = 'Stop'
$localDir = Join-Path $env:USERPROFILE '.nightwatch'
$daemon = Join-Path $localDir 'ssh_daemon.py'
$helper = Join-Path $localDir 'ask_file.py'
$port = 19555

function Fail([string]$Text) { Write-Host "错误: $Text" -ForegroundColor Red; exit 1 }

foreach ($f in @($daemon, $helper)) {
    if (-not (Test-Path $f)) { Fail "缺少局部依赖: $f（连接池需要本地准备，不入库）" }
}
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Fail "找不到 python" }

# 破坏性命令守卫（与 ssh-server.ps1 保持一致，连接池同样要拦）
$destructive = '(?i)(\bsc(\.exe)?\s+(stop|start|config|delete|create)\b|\bnet\s+(stop|start)\b|\bStop-Service\b|\bStart-Service\b|\bRestart-Service\b|\bRemove-Item\b|\bdel\b|\berase\b|\brmdir\b|\brd\b|\bcopy\b|\bxcopy\b|\brobocopy\b|\bmove\b|\bren\b|\bicacls\b.*(/grant|/remove|/reset|/setowner)|\btakeown\b|\btar\b\s+-[xf]|\bExpand-Archive\b|\bcertutil\b.*-addstore|\breg\s+(add|delete)\b)'
if ($Command -match $destructive -and -not $AllowDestructive) {
    Write-Host "拒绝执行：命令看起来会改变服务器状态。" -ForegroundColor Yellow
    Write-Host "  命令: $Command"
    Write-Host "  确认要执行时加 -AllowDestructive，并先取得用户同意。"
    exit 2
}

# 连接池没起就拉起来
$listening = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if (-not $listening) {
    if ($NoAutoStart) { Fail "连接池未运行（127.0.0.1:$port 未监听）" }
    Write-Host "==> 连接池未运行，启动中..." -ForegroundColor Cyan
    Start-Process -FilePath python -ArgumentList $daemon, 'serve' -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $env:TEMP 'ssh-daemon.out') `
        -RedirectStandardError (Join-Path $env:TEMP 'ssh-daemon.err') | Out-Null

    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { break }
    }
    if (-not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)) {
        $err = Get-Content (Join-Path $env:TEMP 'ssh-daemon.err') -ErrorAction SilentlyContinue | Select-Object -Last 5
        Fail "连接池启动失败：$($err -join ' | ')"
    }
    Write-Host "==> 连接池已启动" -ForegroundColor Cyan
}

# 命令走文件，避免引号在多层传递中被解析坏
$cmdFile = Join-Path $env:TEMP ('ssh-cmd-' + [guid]::NewGuid().ToString('N') + '.txt')
$outFile = Join-Path $env:TEMP ('ssh-out-' + [guid]::NewGuid().ToString('N') + '.txt')
$errFile = Join-Path $env:TEMP ('ssh-err-' + [guid]::NewGuid().ToString('N') + '.txt')
[System.IO.File]::WriteAllText($cmdFile, $Command, [System.Text.UTF8Encoding]::new($false))

try {
    $p = Start-Process -FilePath python -ArgumentList @($helper, $cmdFile, "$TimeoutSeconds") -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    $out = if (Test-Path $outFile) { [System.IO.File]::ReadAllText($outFile, [System.Text.Encoding]::UTF8) } else { '' }
    $err = if (Test-Path $errFile) { [System.IO.File]::ReadAllText($errFile, [System.Text.Encoding]::UTF8) } else { '' }
    if ($out.Trim()) { Write-Host $out.TrimEnd() }
    if ($err.Trim()) { Write-Host $err.TrimEnd() -ForegroundColor Yellow }
    exit $p.ExitCode
}
finally {
    Remove-Item $cmdFile, $outFile, $errFile -Force -ErrorAction SilentlyContinue
}
