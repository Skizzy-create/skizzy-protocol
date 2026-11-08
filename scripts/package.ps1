<#
.SYNOPSIS
Builds skizzy-protocol and assembles artifacts/GameData/SkizzyProtocol plus a release zip.

.DESCRIPTION
Produces:
  artifacts/GameData/SkizzyProtocol/Plugins/Skizzy.Host.dll
  artifacts/GameData/SkizzyProtocol/Plugins/Skizzy.Protocol.dll
  artifacts/GameData/SkizzyProtocol/Bin/skizzy.exe          (self-contained, single file, no .NET runtime needed)
  artifacts/GameData/SkizzyProtocol/LICENSE, NOTICE         (Apache-2.0 licence and credit notice)
  artifacts/GameData/SkizzyProtocol/skizzy-protocol.version (KSP-AVC style JSON)
  artifacts/skizzy-protocol-<version>.zip                   (contains GameData/SkizzyProtocol/...)

PluginData/ is intentionally NOT shipped: the plugin creates auth.token and permissions.cfg at runtime.
Only the two plugin DLLs are copied; KSP/Unity DLLs and pdb files are never packaged.

The Host build needs KSP's Managed folder. Provide it with -KspDir, the KSPDIR environment variable,
or the untracked Directory.Build.props.user (see Directory.Build.props.user.example).

.PARAMETER Configuration
Build configuration. Default: Release.

.PARAMETER SkipCli
Do not publish skizzy.exe (partial package; a previously packaged Bin/ is left untouched).

.PARAMETER SkipHost
Do not build the plugin DLLs (partial package; previously packaged Plugins/ is left untouched).

.PARAMETER KspDir
KSP install folder, passed to the Host build as /p:KSPDIR=... Read-only use (reference DLLs).

.EXAMPLE
.\scripts\package.ps1

.EXAMPLE
.\scripts\package.ps1 -KspDir "C:\Games\KSP"

.EXAMPLE
.\scripts\package.ps1 -SkipHost   # CLI only, no KSP install needed
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipCli,
    [switch]$SkipHost,
    [string]$KspDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Artifacts = Join-Path $RepoRoot 'artifacts'
$PkgRoot = Join-Path $Artifacts 'GameData\SkizzyProtocol'

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

# Version comes from Directory.Build.props (<SkizzyVersion>), falling back to 0.1.0.
$Version = '0.1.0'
$propsPath = Join-Path $RepoRoot 'Directory.Build.props'
if (Test-Path -LiteralPath $propsPath) {
    $m = [regex]::Match((Get-Content -LiteralPath $propsPath -Raw), '<SkizzyVersion>\s*([^<\s]+)\s*</SkizzyVersion>')
    if ($m.Success) { $Version = $m.Groups[1].Value }
}

# Runs dotnet, echoes output, retries once after 15 s when another process holds a file.
function Invoke-DotNet([string[]]$DotnetArgs) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Write-Host ("dotnet " + ($DotnetArgs -join ' '))
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { $out = & dotnet @DotnetArgs 2>&1 | ForEach-Object { "$_" } }
        finally { $ErrorActionPreference = $prev }
        $code = $LASTEXITCODE
        $out | ForEach-Object { Write-Host $_ }
        if ($code -eq 0) { return }
        $busy = ($out | Out-String) -match 'being used by another process'
        if ($busy -and $attempt -eq 1) {
            Write-Warning 'A file is in use by another process (probably a concurrent build). Retrying in 15 s...'
            Start-Sleep -Seconds 15
            continue
        }
        throw "dotnet failed with exit code $code."
    }
}

function Copy-Into([string]$From, [string]$ToDir) {
    if (-not (Test-Path -LiteralPath $From)) { throw "Expected build output not found: $From" }
    New-Item -ItemType Directory -Force -Path $ToDir | Out-Null
    Copy-Item -LiteralPath $From -Destination $ToDir -Force
    $name = Split-Path -Leaf $From
    $size = (Get-Item -LiteralPath (Join-Path $ToDir $name)).Length
    Write-Host ("  {0}  ({1} bytes)" -f $name, $size.ToString("N0", [cultureinfo]::InvariantCulture))
}

if ($KspDir -and -not $SkipHost) {
    if (-not (Test-Path -LiteralPath (Join-Path $KspDir 'KSP_x64_Data\Managed'))) {
        throw "-KspDir '$KspDir' has no KSP_x64_Data\Managed folder."
    }
}

# --- Plugin DLLs -----------------------------------------------------------------------------
if (-not $SkipHost) {
    Write-Step 'Building plugin (Skizzy.Host)'
    $hostArgs = @('build', (Join-Path $RepoRoot 'src\Skizzy.Host\Skizzy.Host.csproj'), '-c', $Configuration, '--nologo')
    if ($KspDir) { $hostArgs += "/p:KSPDIR=$KspDir" }
    elseif (-not $env:KSPDIR -and -not (Test-Path -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props.user'))) {
        throw 'KSPDIR is not set. Pass -KspDir, set the KSPDIR environment variable, or create Directory.Build.props.user (see Directory.Build.props.user.example). Use -SkipHost to skip the plugin.'
    }
    Invoke-DotNet $hostArgs

    Write-Step 'Staging Plugins/'
    $plugins = Join-Path $PkgRoot 'Plugins'
    if (Test-Path -LiteralPath $plugins) { Remove-Item -LiteralPath $plugins -Recurse -Force }
    Copy-Into (Join-Path $RepoRoot "src\Skizzy.Host\bin\$Configuration\net48\Skizzy.Host.dll") $plugins
    Copy-Into (Join-Path $RepoRoot "src\Skizzy.Protocol.Ksp\bin\$Configuration\net48\Skizzy.Protocol.dll") $plugins
}

# --- CLI ---------------------------------------------------------------------------------------
if (-not $SkipCli) {
    Write-Step 'Publishing CLI (self-contained single file)'
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('skizzy-publish-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-DotNet @('publish', (Join-Path $RepoRoot 'src\Skizzy.Cli\Skizzy.Cli.csproj'),
            '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-p:DebugSymbols=false',
            '--nologo', '-o', $tmp)
        Write-Step 'Staging Bin/'
        $bin = Join-Path $PkgRoot 'Bin'
        if (Test-Path -LiteralPath $bin) { Remove-Item -LiteralPath $bin -Recurse -Force }
        Copy-Into (Join-Path $tmp 'skizzy.exe') $bin
    }
    finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# --- Metadata ----------------------------------------------------------------------------------
Write-Step 'Writing LICENSE, NOTICE and version file'
New-Item -ItemType Directory -Force -Path $PkgRoot | Out-Null
foreach ($legal in 'LICENSE', 'NOTICE') {
    $legalSrc = Join-Path $RepoRoot $legal
    if (Test-Path -LiteralPath $legalSrc) { Copy-Into $legalSrc $PkgRoot } else { Write-Host "  (no $legal at repo root; skipped)" }
}

$versionJson = @"
{
  "NAME": "skizzy-protocol",
  "VERSION": "$Version",
  "KSP_VERSION": "1.12.5",
  "KSP_VERSION_MIN": "1.12.0"
}
"@
[System.IO.File]::WriteAllText((Join-Path $PkgRoot 'skizzy-protocol.version'), $versionJson.Replace("`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

# PluginData must never be shipped.
$pd = Join-Path $PkgRoot 'PluginData'
if (Test-Path -LiteralPath $pd) { Remove-Item -LiteralPath $pd -Recurse -Force }

# --- Zip ---------------------------------------------------------------------------------------
Write-Step "Creating skizzy-protocol-$Version.zip"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $Artifacts "skizzy-protocol-$Version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$gameData = Join-Path $Artifacts 'GameData'
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Resolve-Path $Artifacts).Path.TrimEnd('\') + '\'
    foreach ($f in (Get-ChildItem -LiteralPath $gameData -Recurse -File)) {
        $entry = $f.FullName.Substring($base.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

Write-Step 'Package contents'
$root = (Resolve-Path $PkgRoot).Path.TrimEnd('\') + '\'
Get-ChildItem -LiteralPath $PkgRoot -Recurse -File | ForEach-Object {
    Write-Host ("  {0,12}  {1}" -f $_.Length.ToString("N0", [cultureinfo]::InvariantCulture), $_.FullName.Substring($root.Length))
}
Write-Host ("  zip: {0}  ({1} bytes)" -f $zipPath, (Get-Item -LiteralPath $zipPath).Length.ToString("N0", [cultureinfo]::InvariantCulture))
if ($SkipHost -or $SkipCli) { Write-Warning 'Partial package (-SkipHost/-SkipCli): not suitable for release.' }
