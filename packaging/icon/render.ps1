# Makes the Hyperceps icon's PNG files from its SVG source, with headless Microsoft Edge
# (installed with Windows). Output, next to this script, all on a transparent background:
#   hyperceps-24.png    the component's icon (from hyperceps-icon-24.svg, drawn for 24 px);
#                       the plugin embeds it as src/Cordyceps/Resources/HypercepsIcon.png
#   hyperceps-64.png    the Package Manager icon
#   hyperceps-512.png   large: the Food4Rhino page and the README
# Run:  powershell -ExecutionPolicy Bypass -File packaging\icon\render.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found.' }
$work = Join-Path $env:TEMP 'hyperceps-icon-render'
New-Item -ItemType Directory -Force $work | Out-Null

function Export-Png([string]$svg, [int]$size, [string]$png) {
    # Edge draws the SVG at its final size on a 512 px page; the picture is then cut out of the
    # page's top-left corner (a headless window can't be made as small as 24 px).
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $here $svg)))
    $page = Join-Path $work 'page.html'
    $html = "<!DOCTYPE html><html><body style=""margin:0;background:transparent"">" +
            "<img width=""$size"" height=""$size"" style=""display:block"" src=""data:image/svg+xml;base64,$b64""></body></html>"
    [IO.File]::WriteAllText($page, $html, (New-Object Text.UTF8Encoding $false))
    $shot = Join-Path $work 'shot.png'
    if (Test-Path $shot) { Remove-Item $shot }
    $edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
        '--window-size=512,512', '--virtual-time-budget=4000', "--user-data-dir=$work\profile",
        '--default-background-color=00000000', "--screenshot=$shot", ('file:///' + ($page -replace '\\', '/')))
    # Wait for the main Edge process only, then stop anything still using this script's profile.
    $p = Start-Process -FilePath $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
    [void]$p.WaitForExit(60000)
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
        Where-Object { $_.CommandLine -like '*hyperceps-icon-render*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path $shot)) { throw "Edge did not draw $svg." }
    $full = New-Object System.Drawing.Bitmap $shot
    try {
        $cut = $full.Clone((New-Object System.Drawing.Rectangle 0, 0, $size, $size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try { $cut.Save((Join-Path $here $png), [System.Drawing.Imaging.ImageFormat]::Png) } finally { $cut.Dispose() }
    } finally { $full.Dispose() }
    Write-Host "$png ($size x $size, from $svg)"
}

Export-Png 'hyperceps-icon-24.svg' 24 'hyperceps-24.png'
Export-Png 'hyperceps-icon.svg' 64 'hyperceps-64.png'
Export-Png 'hyperceps-icon.svg' 512 'hyperceps-512.png'
