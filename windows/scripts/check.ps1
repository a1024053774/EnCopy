# EnCopy Windows 本地检查脚本：每次改完必跑。
# 六道门禁：上游名称检查、本机用户名路径检查、唯一热键入口检查、
# 唯一数据目录入口检查、中英文词条对齐检查、双版本编译与隔离运行检查。
$ErrorActionPreference = 'Stop'

if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") {
    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$windowsRoot = Join-Path $repoRoot 'windows'
$srcDir = Join-Path $windowsRoot 'src\EnCopy'
$checksProj = Join-Path $windowsRoot 'tests\EnCopy.Checks\EnCopy.Checks.csproj'
$appProj = Join-Path $srcDir 'EnCopy.csproj'

function Get-SourceFiles([string]$rootDir, [string[]]$excludeNames = @()) {
    Get-ChildItem -Path $rootDir -Recurse -File | Where-Object {
        $full = $_.FullName
        $name = $_.Name
        $ext = $_.Extension.ToLowerInvariant()
        ($full -notmatch '[\\/](bin|obj|build|\.vs)[\\/]') -and
        ($excludeNames -notcontains $name) -and
        ($ext -in @('.cs', '.xaml', '.csproj', '.sln', '.ps1', '.json', '.md'))
    }
}

Write-Output '[check] 1/6 Checking upstream brand references...'
$upstreamHits = Get-SourceFiles $windowsRoot @('check.ps1') | Select-String -Pattern 'tendedero' -CaseSensitive:$false
if ($upstreamHits) {
    $upstreamHits | ForEach-Object { Write-Error "$($_.Path):$($_.LineNumber): $($_.Line)" }
    exit 1
}

Write-Output '[check] 2/6 Checking local username path leaks...'
$leakTargets = @()
$leakTargets += Get-SourceFiles $windowsRoot
$leakTargets += Get-Item (Join-Path $repoRoot 'AGENTS.md')
$leakTargets += Get-ChildItem -Path (Join-Path $repoRoot '.project-map') -Recurse -File -Filter '*.md'
$leakHits = $leakTargets | Select-String -Pattern 'Users[\\/]10240'
if ($leakHits) {
    $leakHits | ForEach-Object { Write-Error "$($_.Path):$($_.LineNumber): $($_.Line)" }
    exit 1
}

Write-Output '[check] 3/6 Checking single global hotkey registration...'
$csFiles = Get-SourceFiles (Join-Path $windowsRoot 'src') | Where-Object { $_.Extension -eq '.cs' }
$regHits = $csFiles | Where-Object { $_.Name -ne 'HotKey.cs' } | Select-String -Pattern '\bRegisterHotKey\b'
$hookHits = $csFiles | Select-String -Pattern '\b(SetWindowsHookEx|WH_KEYBOARD)\b'
$newHotKeyHits = $csFiles | Where-Object { $_.Name -ne 'HotKey.cs' } | Select-String -Pattern '\bnew\s+HotKey\s*\('
if ($regHits -or $hookHits -or ($newHotKeyHits | Measure-Object).Count -ne 1) {
    Write-Error "EnCopy must register global hotkey in exactly one place (HotKey.cs + 1 instantiation in App). Found reg=$($regHits.Count), hook=$($hookHits.Count), instantiations=$(($newHotKeyHits | Measure-Object).Count)."
    exit 1
}

Write-Output '[check] 4/6 Checking single DataFolder entry point...'
$dataHits = $csFiles | Where-Object { $_.Name -ne 'DataFolder.cs' } | Select-String -Pattern 'LocalApplicationData|LOCALAPPDATA'
if ($dataHits) {
    $dataHits | ForEach-Object { Write-Error "Only DataFolder.cs may reference LocalApplicationData: $($_.Path):$($_.LineNumber)" }
    exit 1
}

Write-Output '[check] 5/6 Checking en / zh-Hans string alignment...'
$codeFiles = Get-ChildItem -Path $srcDir -Recurse -File | Where-Object {
    ($_.FullName -notmatch '[\\/](bin|obj)[\\/]') -and ($_.Extension -in @('.cs', '.xaml'))
}
$usedKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($file in $codeFiles) {
    $text = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    $matches = [regex]::Matches($text, '(?<![A-Za-z0-9_])(?:L|Localization\.Format)\("((?:[^"\\]|\\.)+)"')
    foreach ($m in $matches) {
        $key = [regex]::Unescape($m.Groups[1].Value)
        [void]$usedKeys.Add($key)
    }
}
$enTable = Get-Content -Path (Join-Path $srcDir 'Resources\Strings.en.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$zhTable = Get-Content -Path (Join-Path $srcDir 'Resources\Strings.zh-Hans.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$enKeys = @($enTable.PSObject.Properties.Name)
$zhKeys = @($zhTable.PSObject.Properties.Name)
$missingEn = @($usedKeys | Where-Object { $enKeys -notcontains $_ })
$missingZh = @($usedKeys | Where-Object { $zhKeys -notcontains $_ })
$extraEn = @($enKeys | Where-Object { -not $usedKeys.Contains($_) })
$extraZh = @($zhKeys | Where-Object { -not $usedKeys.Contains($_) })
$emptyEn = @($enKeys | Where-Object { [string]::IsNullOrWhiteSpace($enTable.$_) })
$emptyZh = @($zhKeys | Where-Object { [string]::IsNullOrWhiteSpace($zhTable.$_) })
if ($missingEn -or $missingZh -or $extraEn -or $extraZh -or $emptyEn -or $emptyZh) {
    Write-Error "Localization mismatch: missingEn=[$($missingEn -join ', ')] missingZh=[$($missingZh -join ', ')] extraEn=[$($extraEn -join ', ')] extraZh=[$($extraZh -join ', ')] emptyEn=[$($emptyEn -join ', ')] emptyZh=[$($emptyZh -join ', ')]"
    exit 1
}

Write-Output '[check] 6/6 Building daily + dev editions and running isolated checks...'
dotnet build $appProj -c Debug -p:EnCopyEdition=daily --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $checksProj -c Debug -p:EnCopyEdition=dev --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$checksExe = Join-Path $windowsRoot 'tests\EnCopy.Checks\bin\Debug\net10.0-windows10.0.22621.0\EnCopy.Checks.exe'
if (-not (Test-Path $checksExe)) {
    Write-Error "Check executable not found at $checksExe"
    exit 1
}

# Verify bypass protection refuses direct launch without ENCOPY_CHECKS_RUNNER=1
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $checksExe
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.EnvironmentVariables.Remove('ENCOPY_CHECKS_RUNNER')
$psi.EnvironmentVariables.Remove('ENCOPY_CHECKS_DATA_DIR')
$proc = [System.Diagnostics.Process]::Start($psi)
$null = $proc.StandardOutput.ReadToEnd()
$bypassErr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()
if ($proc.ExitCode -eq 0 -or ($bypassErr -notmatch 'Refusing to run')) {
    Write-Error "FAIL: EnCopy.Checks must refuse to run directly when ENCOPY_CHECKS_RUNNER is unset (exit=$($proc.ExitCode))."
    exit 1
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("encopy-checks-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
try {
    $env:ENCOPY_CHECKS_RUNNER = '1'
    $env:ENCOPY_CHECKS_DATA_DIR = Join-Path $tempDir 'data'
    & $checksExe
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} finally {
    $env:ENCOPY_CHECKS_RUNNER = $null
    $env:ENCOPY_CHECKS_DATA_DIR = $null
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output '[check] PASS'
