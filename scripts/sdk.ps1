$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $projectDotnet)) {
    $installedDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installedDotnet -and ((& $installedDotnet.Source --list-sdks) -match '^10\.')) {
        $projectDotnet = $installedDotnet.Source
    } else {
        $toolDirectory = Join-Path $projectRoot '.tools'
        New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
        $installer = Join-Path $toolDirectory 'dotnet-install.ps1'
        if (-not (Test-Path -LiteralPath $installer)) { Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer }
        & $installer -Version '10.0.401' -InstallDir (Join-Path $toolDirectory 'dotnet') -NoPath
        if (-not (Test-Path -LiteralPath $projectDotnet)) { throw '.NET 10 SDK installation failed.' }
    }
}
function Invoke-ProjectDotnet {
    & $projectDotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
