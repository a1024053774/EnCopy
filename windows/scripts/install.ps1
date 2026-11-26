# 构建日常版 EnCopy 并安装到 %LOCALAPPDATA%\Programs\EnCopy\，安装前先退出运行中的日常版（不碰开发版）。
param(
    [string]$TargetDir = (Join-Path $env:LOCALAPPDATA 'Programs\EnCopy'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$windowsRoot = Join-Path $repoRoot 'windows'
$buildScript = Join-Path $PSScriptRoot 'build-app.ps1'
$dailyBuildDir = Join-Path $windowsRoot 'build\EnCopy'

Write-Output '[install] Building daily EnCopy (Release)...'
& powershell -ExecutionPolicy Bypass -File $buildScript -Edition daily -Configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path (Join-Path $dailyBuildDir 'EnCopy.exe'))) {
    Write-Error "Built EnCopy.exe not found at $dailyBuildDir"
    exit 1
}

# 只退出安装目录对应日常版进程 EnCopy，不动开发版 EnCopy Dev 或其他目录测试进程
$normalizedTarget = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\') + '\'
$procs = @(Get-Process -Name 'EnCopy' -ErrorAction SilentlyContinue | Where-Object {
    try {
        -not [string]::IsNullOrEmpty($_.Path) -and $_.Path.StartsWith($normalizedTarget, [System.StringComparison]::OrdinalIgnoreCase)
    } catch { $false }
})
if ($procs.Count -gt 0) {
    Write-Output '[install] Stopping running EnCopy...'
    foreach ($p in $procs) {
        try {
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
                $null = $p.CloseMainWindow()
            }
        } catch {}
    }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $alive = @(Get-Process -Id ($procs | Select-Object -ExpandProperty Id) -ErrorAction SilentlyContinue)
        if ($alive.Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    }
    $remaining = @(Get-Process -Id ($procs | Select-Object -ExpandProperty Id) -ErrorAction SilentlyContinue)
    foreach ($p in $remaining) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 300
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
Get-ChildItem -Path $TargetDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $dailyBuildDir '*') -Destination $TargetDir -Recurse -Force

# 删掉构建目录里多余的一份日常版，只保留安装目标（对齐 Mac install.sh）
Remove-Item -Path $dailyBuildDir -Recurse -Force -ErrorAction SilentlyContinue

$installedExe = Join-Path $TargetDir 'EnCopy.exe'
Write-Output "Installed $installedExe"

if (-not $NoLaunch) {
    $desktopName = $null
    try {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class EnCopyDesktopProbe {
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint dwThreadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);
    public static string CurrentDesktopName() {
        var h = GetThreadDesktop(GetCurrentThreadId());
        var sb = new StringBuilder(128);
        return GetUserObjectInformation(h, 2, sb, sb.Capacity * 2, out _) ? sb.ToString() : "";
    }
}
"@ -ErrorAction SilentlyContinue
        $desktopName = [EnCopyDesktopProbe]::CurrentDesktopName()
    } catch {}

    if ($desktopName -eq 'Default') {
        Start-Process -FilePath $installedExe
        Write-Output "Launched $installedExe"
    } else {
        Write-Output "Skipped launching GUI on non-interactive desktop '$desktopName'. Start manually: $installedExe"
    }
}
