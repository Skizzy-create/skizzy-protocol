<#
.SYNOPSIS
Removes the skizzy-protocol package from a Kerbal Space Program install.

.DESCRIPTION
Default behaviour removes <KspDir>/GameData/SkizzyProtocol/Plugins, Bin, the version file, LICENSE and NOTICE, and
KEEPS everything the plugin created at runtime:

  * GameData/SkizzyProtocol/PluginData/  (auth.token, permissions.cfg, settings.cfg, audit/, host.log, endpoint.json)
  * <KspDir>/Skizzy/                     (logs and screenshots written by the plugin)
  * %LOCALAPPDATA%\skizzy-protocol\endpoint.json (per-user pointer)

-RemoveData  also removes the whole GameData/SkizzyProtocol folder (including PluginData) and the per-user
             pointer %LOCALAPPDATA%\skizzy-protocol\endpoint.json.
-RemoveLogs  also removes <KspDir>/Skizzy.
-DryRun      prints every action and performs no writes; all deletions go through a single helper that checks it.

Nothing else under the KSP folder is ever touched. Refuses to run while KSP is running unless -Force.

.PARAMETER KspDir
KSP install folder. Defaults to the KSPDIR environment variable.

.PARAMETER DryRun
Show what would be removed without changing anything.

.PARAMETER RemoveData
Also delete PluginData (token, permissions, settings, audit, host.log) and the per-user endpoint pointer.

.PARAMETER RemoveLogs
Also delete <KspDir>/Skizzy (logs and screenshots).

.PARAMETER Force
Proceed even if KSP appears to be running.

.EXAMPLE
.\scripts\uninstall.ps1 -KspDir "C:\Games\KSP" -DryRun

.EXAMPLE
.\scripts\uninstall.ps1 -KspDir "C:\Games\KSP"

.EXAMPLE
.\scripts\uninstall.ps1 -KspDir "C:\Games\KSP" -RemoveData -RemoveLogs
#>
[CmdletBinding()]
param(
    [string]$KspDir = $env:KSPDIR,
    [switch]$DryRun,
    [switch]$RemoveData,
    [switch]$RemoveLogs,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }
function Log([string]$msg) { Write-Host $msg }

# --- Validate --------------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($KspDir)) { Fail 'No KSP folder. Pass -KspDir <path> or set the KSPDIR environment variable.' }
if (-not (Test-Path -LiteralPath $KspDir -PathType Container)) { Fail "KSP folder not found: $KspDir" }
$KspDir = (Resolve-Path -LiteralPath $KspDir).ProviderPath.TrimEnd('\')
if (-not ((Test-Path -LiteralPath (Join-Path $KspDir 'KSP_x64.exe')) -or (Test-Path -LiteralPath (Join-Path $KspDir 'KSP.exe')))) {
    Fail "'$KspDir' does not look like a KSP install (no KSP_x64.exe or KSP.exe). Nothing was changed."
}
$GameData = Join-Path $KspDir 'GameData'
if (-not (Test-Path -LiteralPath $GameData -PathType Container)) { Fail "'$KspDir' has no GameData folder. Nothing was changed." }

$Dest = Join-Path $GameData 'SkizzyProtocol'
$DestPrefix = $Dest + '\'
$LogsDir = Join-Path $KspDir 'Skizzy'
$localApp = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { [Environment]::GetFolderPath('LocalApplicationData') }
$PointerDir = if ($localApp) { Join-Path $localApp 'skizzy-protocol' } else { $null }
$Pointer = if ($PointerDir) { Join-Path $PointerDir 'endpoint.json' } else { $null }

$running = @(Get-Process -Name 'KSP_x64', 'KSP' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if ($Force) { Write-Warning "KSP is running (PID $(($running | ForEach-Object { $_.Id }) -join ', ')); continuing because of -Force." }
    elseif ($DryRun) { Write-Warning 'KSP is running. A real uninstall would be refused without -Force.' }
    else { Fail 'KSP is running. Close it first (or use -Force). Nothing was changed.' }
}

# --- The one place where anything is deleted -------------------------------------------------------
$script:Actions = 0
function Test-Allowed([string]$full) {
    # GameData/SkizzyProtocol (and below), and the exact extra targets.
    if ($full.Equals($Dest, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($DestPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        # PluginData only deletable together with -RemoveData.
        if ($full.StartsWith($DestPrefix + 'PluginData', [StringComparison]::OrdinalIgnoreCase)) { return [bool]$RemoveData }
        return $true

    }
    if ($RemoveLogs -and ($full.Equals($LogsDir, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($LogsDir + '\', [StringComparison]::OrdinalIgnoreCase))) { return $true }
    if ($RemoveData -and $Pointer -and $full.Equals([System.IO.Path]::GetFullPath($Pointer), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($RemoveData -and $PointerDir -and $full.Equals([System.IO.Path]::GetFullPath($PointerDir), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $false
}
function Invoke-Remove([string]$Description, [string]$Path, [switch]$Recurse) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Allowed $full)) { throw "Refusing to delete '$full'." }
    $script:Actions++
    if ($DryRun) { Log "[dry-run] $Description"; return }
    Log $Description
    if ($Recurse) { Remove-Item -LiteralPath $full -Recurse -Force } else { Remove-Item -LiteralPath $full -Force }
}

Log ''
Log ("skizzy-protocol uninstall{0}" -f $(if ($DryRun) { ' (DRY RUN - no changes will be made)' } else { '' }))
Log "  KSP     : $KspDir"
Log "  Target  : $Dest"
Log ''

$kept = New-Object System.Collections.Generic.List[string]
$pluginDataPath = Join-Path $Dest 'PluginData'

try {
    if (-not (Test-Path -LiteralPath $Dest -PathType Container)) {
        Log "Not installed: $Dest does not exist."
    }
    elseif ($RemoveData) {
        Invoke-Remove "remove dir  $Dest (including PluginData)" $Dest -Recurse
    }
    else {
        foreach ($name in 'Plugins', 'Bin') {
            $p = Join-Path $Dest $name
            if (Test-Path -LiteralPath $p -PathType Container) { Invoke-Remove "remove dir  $p" $p -Recurse }
        }
        foreach ($name in 'skizzy-protocol.version', 'LICENSE', 'NOTICE') {
            $p = Join-Path $Dest $name
            if (Test-Path -LiteralPath $p -PathType Leaf) { Invoke-Remove "remove file $p" $p }
        }
        $removedNames = @('Plugins', 'Bin', 'skizzy-protocol.version', 'LICENSE', 'NOTICE')
        $remaining = @(Get-ChildItem -LiteralPath $Dest -Force | Where-Object { $removedNames -notcontains $_.Name })
        if ($remaining.Count -eq 0) {
            Invoke-Remove "remove dir  $Dest (now empty)" $Dest -Recurse
        }
        else {
            foreach ($r in $remaining) {
                if ($r.Name -eq 'PluginData') { $kept.Add("$pluginDataPath  (token, permissions, settings, audit, host.log; use -RemoveData to delete)") }
                else { $kept.Add("$($r.FullName)  (not part of the package)") }
            }
        }
    }

    if (Test-Path -LiteralPath $LogsDir -PathType Container) {
        if ($RemoveLogs) { Invoke-Remove "remove dir  $LogsDir (logs and screenshots)" $LogsDir -Recurse }
        else { $kept.Add("$LogsDir  (logs and screenshots; use -RemoveLogs to delete)") }
    }

    if ($Pointer -and (Test-Path -LiteralPath $Pointer -PathType Leaf)) {
        if ($RemoveData) {
            Invoke-Remove "remove file $Pointer (per-user endpoint pointer)" $Pointer
            $others = @(Get-ChildItem -LiteralPath $PointerDir -Force | Where-Object { $_.Name -ne 'endpoint.json' })
            if ($others.Count -eq 0) { Invoke-Remove "remove dir  $PointerDir (now empty)" $PointerDir -Recurse }
        }
        else { $kept.Add("$Pointer  (per-user pointer; use -RemoveData to delete)") }
    }
}
catch {
    Fail "Uninstall failed: $($_.Exception.Message)`nIf a file is in use (for example a running skizzy.exe mcp), close it and retry."
}

Log ''
if ($DryRun) { Log ("Dry run complete: {0} action(s) planned, nothing deleted." -f $script:Actions) }
else { Log ("Uninstall complete: {0} action(s)." -f $script:Actions) }
if ($kept.Count -gt 0) {
    Log ''
    Log 'Kept:'
    foreach ($k in $kept) { Log "  $k" }
}
Log ''
