<#
.SYNOPSIS
  Build the Hyperceps release files: the Yak package, the plugin and the skill zip.

.DESCRIPTION
  Builds the plugin (Release), runs the unit tests, checks that the plugin, the skill and the package
  share one version number, stages the Yak package (plugin, LICENSE, README, icon, manifest and the
  skill), runs `yak build`, and writes everything to artifacts\ (git-ignored):

    artifacts\hyperceps-<version>-<distribution>.yak   for `yak push`
    artifacts\Hyperceps.gha                            for the GitHub release
    artifacts\hyperceps-skill.zip                      for the GitHub release (the hyperceps skill folder)

  It never publishes: `yak push` and the GitHub release are done by the owner; the script prints the
  commands.

.PARAMETER Platform
  The Yak platform: 'any' (Windows and macOS, like Cordyceps; the default) or 'win'.

.PARAMETER Dotnet
  The dotnet executable. Defaults to a Scoop-installed .NET 8 SDK when there is one, else 'dotnet'.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\package.ps1
#>
param(
    [ValidateSet('any', 'win')][string]$Platform = 'any',
    [string]$Dotnet = '',
    [string]$Yak = 'C:\Program Files\Rhino 8\System\Yak.exe'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts'
$stage = Join-Path $artifacts 'yak'

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed (exit code $LASTEXITCODE)" }
}

if (-not $Dotnet) {
    $scoop = Join-Path $env:USERPROFILE 'scoop\apps\dotnet8-sdk\current'
    if (Test-Path (Join-Path $scoop 'dotnet.exe')) {
        $Dotnet = Join-Path $scoop 'dotnet.exe'
        $env:DOTNET_ROOT = $scoop
    } else {
        $Dotnet = 'dotnet'
    }
}
if (-not (Test-Path $Yak)) { throw "Yak not found at $Yak (it comes with Rhino 8)" }

# 1. One version number for plugin, skill and package.
$csproj = Get-Content (Join-Path $repo 'src\Cordyceps\Cordyceps.csproj') -Raw
$version = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
$skillFile = Join-Path $repo 'skills\hyperceps\SKILL.md'
$skillLine = Select-String -LiteralPath $skillFile -Pattern '^This skill is for Hyperceps (\S+?)\.?(\s|$)' | Select-Object -First 1
$skillVersion = if ($skillLine) { $skillLine.Matches[0].Groups[1].Value } else { '' }
if (-not $version -or $version -ne $skillVersion) {
    throw "Version mismatch: plugin '$version', skill '$skillVersion'. Make them equal before packaging."
}
Step "Packaging Hyperceps $version for platform '$Platform'"

# 2. Build and test.
Step 'Building (Release)'
Run $Dotnet @('build', (Join-Path $repo 'src\Cordyceps\Cordyceps.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
Step 'Running unit tests'
Run $Dotnet @('test', (Join-Path $repo 'src\Cordyceps.Tests\Cordyceps.Tests.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$gha = Join-Path $repo 'releases\Hyperceps.gha'
if (-not (Test-Path $gha)) { throw "The build didn't produce $gha" }

# 3. Stage the Yak package.
Step 'Staging the Yak package'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath $gha -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repo 'packaging\icon\hyperceps-64.png') -Destination (Join-Path $stage 'icon.png')
foreach ($name in 'manifest.template.yml', 'README.md') {
    $text = (Get-Content -LiteralPath (Join-Path $repo "packaging\yak\$name") -Raw).Replace('{{VERSION}}', $version)
    $out = if ($name -eq 'manifest.template.yml') { 'manifest.yml' } else { $name }
    [IO.File]::WriteAllText((Join-Path $stage $out), $text, (New-Object Text.UTF8Encoding $false))
}
$skillStage = Join-Path $stage 'skill\hyperceps'
New-Item -ItemType Directory -Path $skillStage -Force | Out-Null
Copy-Item -LiteralPath $skillFile -Destination $skillStage

# 4. Build the .yak.
Step 'Running yak build'
Get-ChildItem -LiteralPath $artifacts -Filter '*.yak' -ErrorAction SilentlyContinue | Remove-Item -Force
Push-Location $stage
try {
    Run $Yak @('build', '--platform', $Platform)
} finally { Pop-Location }
$built = Get-ChildItem -LiteralPath $stage -Filter '*.yak' | Select-Object -First 1
if (-not $built) { throw 'yak build produced no .yak file' }
Move-Item -LiteralPath $built.FullName -Destination $artifacts -Force
$yakFile = Join-Path $artifacts $built.Name

# 5. The GitHub release files.
Step 'Writing the GitHub release files'
Copy-Item -LiteralPath $gha -Destination $artifacts -Force
$zip = Join-Path $artifacts 'hyperceps-skill.zip'
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
# Built entry by entry: Windows PowerShell's Compress-Archive writes '\' into entry names, which other
# tools (and Claude's skill upload) may not read as folders. Entries are 'hyperceps/<file>'.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$skillDir = Join-Path $repo 'skills\hyperceps'
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
$archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $skillDir -Recurse -File) {
        $entry = 'hyperceps/' + $file.FullName.Substring($skillDir.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
    }
} finally {
    $archive.Dispose()
    $stream.Dispose()
}

Step 'Release files'
foreach ($f in $yakFile, (Join-Path $artifacts 'Hyperceps.gha'), $zip) {
    $hash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0,-45} {1,9:N0} bytes  sha256 {2}' -f (Split-Path $f -Leaf), (Get-Item $f).Length, $hash
}

Write-Host @"

Nothing has been published. To publish (owner only):
  Yak:     & "$Yak" login
           & "$Yak" push "$yakFile"
  GitHub:  create release v$version in github.com/yang-yibinn/hyperceps and attach
           Hyperceps.gha and hyperceps-skill.zip from $artifacts
"@
