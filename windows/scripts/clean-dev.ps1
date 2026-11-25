# 退出开发版 EnCopy Dev 并清掉它的设置、数据文件夹与测试启动项，不碰日常版 EnCopy。
$ErrorActionPreference = 'Stop'

$procs = @(Get-Process -Name 'EnCopy Dev' -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    foreach ($p in $procs) {
        try {
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
                $null = $p.CloseMainWindow()
            }
        } catch {}
    }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $alive = @(Get-Process -Name 'EnCopy Dev' -ErrorAction SilentlyContinue)
        if ($alive.Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    }
    $remaining = @(Get-Process -Name 'EnCopy Dev' -ErrorAction SilentlyContinue)
    foreach ($p in $remaining) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 300
}

$devDataDir = Join-Path $env:LOCALAPPDATA 'EnCopy Dev'
if (Test-Path $devDataDir) {
    Remove-Item -Path $devDataDir -Recurse -Force
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path $runKey) {
    $existing = Get-ItemProperty -Path $runKey -Name 'EnCopyDev' -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Remove-ItemProperty -Path $runKey -Name 'EnCopyDev' -ErrorAction SilentlyContinue
    }
}

Write-Output 'Removed the settings and data folder of EnCopy Dev'
