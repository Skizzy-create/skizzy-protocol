<#
.SYNOPSIS
Installs the skizzy-protocol package into a Kerbal Space Program GameData folder.

.DESCRIPTION
Copies artifacts/GameData/SkizzyProtocol (produced by scripts/package.ps1) into
<KspDir>/GameData/SkizzyProtocol/.

  * Installs Plugins/, Bin/, the version file and LICENSE.
  * On upgrade, Plugins/ and Bin/ are replaced (stale files in them are removed).
  * PluginData/ (auth.token, permissions.cfg, settings.cfg, audit/, host.log, endpoint.json) is NEVER touched.
  * Nothing outside <KspDir>/GameData/SkizzyProtocol/ is ever written or deleted.
  * Warns about other Skizzy*.dll copies elsewhere under GameData (does not delete them).
  * Refuses to run while KSP is running unless -Force.
  * Output goes to the console only.

-DryRun prints every action (create dir / copy file with size / replace file / remove file) and performs
no writes at all; every write goes through a single helper that checks -DryRun.

.PARAMETER KspDir
KSP install folder (contains KSP_x64.exe or KSP.exe, and GameData). Defaults to the KSPDIR environment variable.

.PARAMETER Source
Package folder to install. Defaults to <repo>/artifacts/GameData/SkizzyProtocol (relative to this script).

.PARAMETER DryRun
Show what would be done without changing anything.

.PARAMETER Force
Install even if KSP appears to be running.

.EXAMPLE
.\scripts\install.ps1 -KspDir "C:\Games\Kerbal Space Program" -DryRun

.EXAMPLE
$env:KSPDIR = "C:\Games\Kerbal Space Program"; .\scripts\install.ps1

.EXAMPLE
.\scripts\install.ps1 -KspDir "C:\Games\KSP" -Source "D:\unzipped\GameData\SkizzyProtocol"
#>
[CmdletBinding()]
param(
    [string]$KspDir = $env:KSPDIR,
    [string]$Source,
    [switch]$DryRun,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Inv = [cultureinfo]::InvariantCulture
function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }
function Log([string]$msg) { Write-Host $msg }
function Fmt-Size([long]$n) { return $n.ToString('N0', $Inv) + ' bytes' }

# --- Validate inputs -------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($KspDir)) { Fail 'No KSP folder. Pass -KspDir <path> or set the KSPDIR environment variable.' }
if (-not (Test-Path -LiteralPath $KspDir -PathType Container)) { Fail "KSP folder not found: $KspDir" }
$KspDir = (Resolve-Path -LiteralPath $KspDir).ProviderPath.TrimEnd('\')
if (-not ((Test-Path -LiteralPath (Join-Path $KspDir 'KSP_x64.exe')) -or (Test-Path -LiteralPath (Join-Path $KspDir 'KSP.exe')))) {
    Fail "'$KspDir' does not look like a KSP install (no KSP_x64.exe or KSP.exe). Nothing was changed."
}
$GameData = Join-Path $KspDir 'GameData'
if (-not (Test-Path -LiteralPath $GameData -PathType Container)) {
    Fail "'$KspDir' has no GameData folder. Nothing was changed."
}

if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path (Join-Path $PSScriptRoot '..') 'artifacts\GameData\SkizzyProtocol'
}
if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    Fail "Package folder not found: $Source`nRun scripts\package.ps1 first (or pass -Source <folder>). Nothing was changed."
}
$Source = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\')
$srcFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -File | Where-Object {
        $rel = $_.FullName.Substring($Source.Length + 1)
        -not ($rel -like 'PluginData\*')
    })
if ($srcFiles.Count -eq 0) { Fail "Package folder '$Source' contains no files. Run scripts\package.ps1. Nothing was changed." }
$hasPluginData = @(Get-ChildItem -LiteralPath $Source -Recurse -File | Where-Object { $_.FullName.Substring($Source.Length + 1) -like 'PluginData\*' })
if ($hasPluginData.Count -gt 0) { Write-Warning "Package contains PluginData files; they are ignored (PluginData is never installed)." }

$Dest = Join-Path $GameData 'SkizzyProtocol'
$DestPrefix = $Dest + '\'

# --- KSP running? ----------------------------------------------------------------------------------
$running = @(Get-Process -Name 'KSP_x64', 'KSP' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if ($Force) { Write-Warning "KSP is running (PID $(($running | ForEach-Object { $_.Id }) -join ', ')); continuing because of -Force." }
    elseif ($DryRun) { Write-Warning 'KSP is running. A real install would be refused without -Force.' }
    else { Fail 'KSP is running. Close it first (or use -Force). Nothing was changed.' }
}

# --- The one place where anything is written -------------------------------------------------------
$script:Actions = 0
function Assert-InsideDest([string]$path) {
    $full = [System.IO.Path]::GetFullPath($path)
    if (-not ($full.Equals($Dest, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($DestPrefix, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to touch '$full': outside $Dest"
    }
    if ($full.StartsWith($DestPrefix + 'PluginData', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to touch '$full': PluginData is never modified by the installer"
    }
}
function Invoke-Write([string]$Description, [string]$Path, [scriptblock]$Action) {
    Assert-InsideDest $Path
    $script:Actions++
    if ($DryRun) { Log "[dry-run] $Description"; return }
    Log $Description
    & $Action
}

# --- Plan + execute --------------------------------------------------------------------------------
Log ''
Log ("skizzy-protocol install{0}" -f $(if ($DryRun) { ' (DRY RUN - no changes will be made)' } else { '' }))
Log "  KSP     : $KspDir"
Log "  Source  : $Source"
Log "  Target  : $Dest"
Log ''

$upgrade = Test-Path -LiteralPath $Dest -PathType Container
if ($upgrade) { Log 'Existing install found: upgrading (Plugins/ and Bin/ are replaced, PluginData/ is left alone).' }

if (-not $upgrade) {
    Invoke-Write "create dir  $Dest" $Dest { New-Item -ItemType Directory -Path $Dest | Out-Null }
}

# Remove stale files inside Plugins/ and Bin/ that the new package no longer contains.
$srcRel = @{}
foreach ($f in $srcFiles) { $srcRel[$f.FullName.Substring($Source.Length + 1).ToLowerInvariant()] = $true }
foreach ($sub in 'Plugins', 'Bin') {
    $d = Join-Path $Dest $sub
    if (Test-Path -LiteralPath $d -PathType Container) {
        foreach ($old in (Get-ChildItem -LiteralPath $d -Recurse -File)) {
            $rel = $old.FullName.Substring($Dest.Length + 1).ToLowerInvariant()
            if (-not $srcRel.ContainsKey($rel)) {
                $p = $old.FullName
                Invoke-Write "remove file $p (stale)" $p { Remove-Item -LiteralPath $p -Force }
            }
        }
    }
}

$createdDirs = @{ $Dest.ToLowerInvariant() = $true }
foreach ($f in ($srcFiles | Sort-Object FullName)) {
    $rel = $f.FullName.Substring($Source.Length + 1)
    $target = Join-Path $Dest $rel
    $tdir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $tdir -PathType Container) -and -not $createdDirs.ContainsKey($tdir.ToLowerInvariant())) {
        $createdDirs[$tdir.ToLowerInvariant()] = $true
        Invoke-Write "create dir  $tdir" $tdir { New-Item -ItemType Directory -Force -Path $tdir | Out-Null }
    }
    $verb = if (Test-Path -LiteralPath $target -PathType Leaf) { 'replace file' } else { 'copy file   ' }
    $from = $f.FullName
    $desc = "$verb $target  ($(Fmt-Size $f.Length))"
    try {
        Invoke-Write $desc $target { Copy-Item -LiteralPath $from -Destination $target -Force }
    }
    catch {
        Fail "Could not write '$target': $($_.Exception.Message)`nIf skizzy.exe is in use (for example by a running MCP bridge), close it and retry."
    }
}

# --- Other copies of Skizzy*.dll -------------------------------------------------------------------
# Only assemblies with the same file name as one this package ships can conflict (e.g. an unrelated
# mod's SkizzyRig.dll is not reported).
$pkgDllNames = @($srcFiles | Where-Object { $_.Name -like 'Skizzy*.dll' } | ForEach-Object { $_.Name.ToLowerInvariant() })
$others = @(Get-ChildItem -LiteralPath $GameData -Recurse -File -Filter 'Skizzy*.dll' -ErrorAction SilentlyContinue |
        Where-Object { $pkgDllNames -contains $_.Name.ToLowerInvariant() -and -not $_.FullName.StartsWith($DestPrefix, [StringComparison]::OrdinalIgnoreCase) })
if ($others.Count -gt 0) {
    Log ''
    Write-Warning 'Other Skizzy*.dll copies exist under GameData (not touched). Two copies will conflict in KSP:'
    foreach ($o in $others) { Log "  $($o.FullName)" }
}

# --- Summary + next steps --------------------------------------------------------------------------
$exe = Join-Path $Dest 'Bin\skizzy.exe'
Log ''
if ($DryRun) { Log ("Dry run complete: {0} action(s) planned, nothing written." -f $script:Actions) }
else { Log ("Install complete: {0} action(s)." -f $script:Actions) }
Log ''
Log 'Next steps'
Log '  1. Start KSP.'
Log '  2. In a terminal, check the connection:'
Log "       `"$exe`" status"
Log '  3. Register with Claude Code:'
Log "       claude mcp add skizzy -- `"$exe`" mcp"
Log '  4. Or Codex CLI: add to ~/.codex/config.toml'
Log '       [mcp_servers.skizzy]'
Log ("       command = `"{0}`"" -f $exe.Replace('\', '\\'))
Log '       args = ["mcp"]'
Log ''
