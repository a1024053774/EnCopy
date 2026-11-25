# 构建 Windows 版 EnCopy 到 windows/build/，默认构建开发版（EnCopy Dev）。
param(
    [ValidateSet('dev', 'daily')]
    [string]$Edition = 'dev',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") {
    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$windowsRoot = Join-Path $repoRoot 'windows'
$appProj = Join-Path $windowsRoot 'src\EnCopy\EnCopy.csproj'
$name = if ($Edition -eq 'daily') { 'EnCopy' } else { 'EnCopy Dev' }
$outDir = Join-Path $windowsRoot (Join-Path 'build' $name)

if (Test-Path $outDir) {
    Remove-Item -Path $outDir -Recurse -Force
}

dotnet publish $appProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:EnCopyEdition=$Edition `
    -o $outDir `
    --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -Path (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $outDir 'LICENSE') -Force
Write-Output "Built $name -> $outDir"
