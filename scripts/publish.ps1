. "$PSScriptRoot\sdk.ps1"
Push-Location $projectRoot
try {
    $publishDirectory = Join-Path $projectRoot 'artifacts\ScreenshotDock-win-x64'
    Invoke-ProjectDotnet publish src/ScreenshotDock.App -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o $publishDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\使用说明.md') -Destination (Join-Path $publishDirectory '使用说明.md') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\验收记录.md') -Destination (Join-Path $publishDirectory '验收记录.md') -Force
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath (Join-Path $projectRoot 'artifacts\ScreenshotDock-1.0.0-win-x64.zip') -Force
    Write-Output "Ready: $publishDirectory\ScreenshotDock.exe"
} finally { Pop-Location }
