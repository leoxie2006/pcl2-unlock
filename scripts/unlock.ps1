<#
.SYNOPSIS
    PCL2 下载功能解锁补丁 —— Windows 一键脚本

.DESCRIPTION
    构建并运行补丁工具，把 PCL2 的「MC 下载」与「整合包安装」入口恢复。
    需要 .NET SDK 8 或更高版本（dnlib 由 NuGet 自动还原）。

.PARAMETER InputExe
    PCL2 的 exe 路径。省略时会在当前目录和项目根目录查找
    「Plain Craft Launcher 2.exe」/「PCL2.exe」。

.PARAMETER Output
    输出路径。省略时写到 dist\<输入名>.unlocked.exe。

.PARAMETER Only
    只跑指定补丁，例如 1 或 1,2。

.PARAMETER Check
    只检查输入文件是否已解锁，不写文件。

.EXAMPLE
    .\scripts\unlock.ps1

.EXAMPLE
    .\scripts\unlock.ps1 -InputExe "D:\PCL2\Plain Craft Launcher 2.exe" -Only 1
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$InputExe,

    [Parameter()]
    [string]$Output,

    [Parameter()]
    [string]$Only,

    [Parameter()]
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root 'src\PCL2Unlock'

function Write-Step([string]$message) {
    Write-Host "> $message" -ForegroundColor Cyan
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 8 或更高版本，请先从 https://dotnet.microsoft.com/download 安装'
}

# 1. 定位输入文件
if (-not $InputExe) {
    $candidates = @(
        (Join-Path (Get-Location) 'Plain Craft Launcher 2.exe'),
        (Join-Path (Get-Location) 'PCL2.exe'),
        (Join-Path $root 'Plain Craft Launcher 2.exe'),
        (Join-Path $root 'PCL2.exe')
    )
    $InputExe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $InputExe -or -not (Test-Path -LiteralPath $InputExe)) {
    throw '找不到 PCL2 的 exe，请用 -InputExe 指定，例如：.\scripts\unlock.ps1 -InputExe "D:\PCL2\Plain Craft Launcher 2.exe"'
}

$InputExe = (Resolve-Path -LiteralPath $InputExe).Path
Write-Step "输入：$InputExe"

# 2. 输出路径
if (-not $Output -and -not $Check) {
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $Output = Join-Path $dist (((Split-Path -Leaf $InputExe) -replace '\.exe$', '') + '.unlocked.exe')
}

$toolArgs = @($InputExe)
if ($Only) { $toolArgs += @('--only', $Only) }

if ($Check) {
    $toolArgs += '--check'
}
else {
    $toolArgs += @('-o', $Output)
}

Write-Step '构建补丁工具'
dotnet build $project -v q --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

Write-Step '运行补丁'
dotnet run --project $project --no-build -- @toolArgs
$exit = $LASTEXITCODE

if ($Check) {
    if ($exit -eq 0) {
        Write-Host '检查结果：已解锁（退出码 0）' -ForegroundColor Green
    }
    else {
        Write-Host "检查结果：未解锁或版本不匹配（退出码 $exit）" -ForegroundColor Yellow
    }
    exit $exit
}

if ($exit -ne 0) { throw "补丁失败，退出码 $exit" }

# 3. 复核输出
if (Test-Path -LiteralPath $Output) {
    Write-Step '校验输出文件'
    dotnet run --project $project --no-build -- $Output --check

    $hash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLower()
    Write-Host ''
    Write-Host "SHA-256：$hash"
    Write-Host "输出文件：$Output"
    Write-Host ''
    Write-Host '完成。用解锁后的 exe 替换原文件即可（建议先备份原 exe）。'
}
