[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$AvatarPath
)

$ErrorActionPreference = 'Stop'

# Mirror the package contents into <AvatarPath>\Packages\<package-id>\ using
# the same exclusion list as .github/workflows/release.yml, so what the
# avatar project sees is byte-equivalent to what VCC users will get after a
# release.
#
# Caller picks the target avatar each invocation; nothing about the host
# filesystem is baked in.
#
# Compatible with Windows PowerShell 5.1 (no &&, no ??, no ternary).

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageJsonPath = Join-Path $repoRoot 'package.json'
if (-not (Test-Path $packageJsonPath)) {
    Write-Error "package.json not found at $packageJsonPath"
    exit 1
}

$packageName = (Get-Content $packageJsonPath -Raw | ConvertFrom-Json).name
if ([string]::IsNullOrEmpty($packageName)) {
    Write-Error "package.json missing 'name' field"
    exit 1
}

if (-not (Test-Path $AvatarPath)) {
    Write-Error "Avatar project path not found: $AvatarPath"
    exit 1
}

$avatarFull = (Resolve-Path $AvatarPath).Path
$packagesDir = Join-Path $avatarFull 'Packages'
if (-not (Test-Path $packagesDir)) {
    Write-Error "No Packages/ directory under $avatarFull -- not a Unity project root?"
    exit 1
}

$destination = Join-Path $packagesDir $packageName

# Foot-gun guard: only write into Packages\dev.whyknot.* so a malformed
# package.json or argument can't wipe an unrelated directory.
if ($destination -notmatch '\\Packages\\dev\.whyknot\.[\w\-]+\\?$') {
    Write-Error "Destination '$destination' is not under Packages\dev.whyknot.*. Refusing to deploy."
    exit 1
}

Write-Host "Deploying $packageName"
Write-Host "  from: $repoRoot"
Write-Host "  to:   $destination"

$excludeDirs = @('.git', '.github', '.claude', '.vscode', '.vs', '.idea', 'wiki', 'staging', 'scripts')
$excludeFiles = @('.gitignore', '.gitattributes', 'CONTRIBUTING.md', '*.zip', '*.bak', '*.log', '*.tmp', '*.out', '*.exit')

$robocopyArgs = @($repoRoot, $destination, '/MIR', '/NFL', '/NDL', '/NJH', '/R:1', '/W:1')
$robocopyArgs += '/XD'
$robocopyArgs += $excludeDirs
$robocopyArgs += '/XF'
$robocopyArgs += $excludeFiles

& robocopy @robocopyArgs | Out-Null
$rc = $LASTEXITCODE

# robocopy exit semantics: 0-7 = success (various combinations of copied /
# skipped / mismatched), 8+ = real failure.
if ($rc -ge 8) {
    Write-Error "robocopy failed with exit code $rc"
    exit $rc
}

$copiedFiles = Get-ChildItem -Path $destination -Recurse -File -ErrorAction SilentlyContinue
if ($copiedFiles) {
    $count = @($copiedFiles).Count
} else {
    $count = 0
}
Write-Host ("Deployed {0} file(s) (robocopy exit {1})." -f $count, $rc)
