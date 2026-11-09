<#
.SYNOPSIS
Copies BDArmory.dll into the git-ignored refs/ folder so src/Skizzy.BDArmory can compile against it.

.DESCRIPTION
The BDArmory binding (src/Skizzy.BDArmory) references refs/BDArmory.dll. BDArmory is GPL-3 and is never committed
or shipped by this repo: refs/ is git-ignored and the package step never copies it.

The binding's typed hook classes need an Overdrive-hooks build of BDArmory (branch overdrive-hooks). At run time
the binding probes for each hook, so the same Skizzy.BDArmory.dll also loads on a stock BDArmory and refuses the
orders that need a missing hook (HOOK_MISSING).

The source is only read. Nothing is written outside refs/.

.PARAMETER Source
Path to BDArmory.dll. Default: $env:BDARMORY_DLL, else <KSPDIR>\GameData\BDArmory\Plugins\BDArmory.dll
(KSPDIR from -KspDir, the KSPDIR environment variable or Directory.Build.props.user).

.PARAMETER KspDir
KSP install folder, used only to find the default source.

.EXAMPLE
.\scripts\copy-refs.ps1 -Source D:\KerbalSpaceProgram\BDArmory\BDArmory\bin\Release\BDArmory.dll
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$KspDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Refs = Join-Path $RepoRoot 'refs'

if (-not $Source) { $Source = $env:BDARMORY_DLL }
if (-not $Source) {
    if (-not $KspDir) { $KspDir = $env:KSPDIR }
    if (-not $KspDir) {
        $user = Join-Path $RepoRoot 'Directory.Build.props.user'
        if (Test-Path -LiteralPath $user) {
            $m = [regex]::Match((Get-Content -LiteralPath $user -Raw), '<KSPDIR>\s*([^<]+?)\s*</KSPDIR>')
            if ($m.Success) { $KspDir = $m.Groups[1].Value }
        }
    }
    if (-not $KspDir) { throw 'No -Source given and KSPDIR is not set (pass -Source, -KspDir, or set KSPDIR).' }
    $Source = Join-Path $KspDir 'GameData\BDArmory\Plugins\BDArmory.dll'
}
if (-not (Test-Path -LiteralPath $Source)) { throw "BDArmory.dll not found: $Source" }

# The typed hook classes need the Overdrive members; a stock DLL compiles nothing useful.
$text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Source))
$missing = @('ExternalTargetFilter', 'ExternalSecondaryTargets', 'OnExternalOverrideLost', 'fireAuthority', 'DisplayedContacts', 'waypointsYieldToCombat', 'OnMissileFired') |
    Where-Object { -not $text.Contains($_) }
if ($missing) {
    throw ("$Source is not an Overdrive-hooks build of BDArmory (missing: " + ($missing -join ', ') + "). Build BDArmory branch overdrive-hooks and pass -Source <its BDArmory.dll>.")
}

New-Item -ItemType Directory -Force -Path $Refs | Out-Null
Copy-Item -LiteralPath $Source -Destination (Join-Path $Refs 'BDArmory.dll') -Force
$size = (Get-Item -LiteralPath (Join-Path $Refs 'BDArmory.dll')).Length
Write-Host ("refs\BDArmory.dll  ({0} bytes)  from {1}" -f $size.ToString('N0', [cultureinfo]::InvariantCulture), $Source)
