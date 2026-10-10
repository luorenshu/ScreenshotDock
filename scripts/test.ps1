param([switch]$Integration, [switch]$Editor, [switch]$Pin, [switch]$Edge, [switch]$Settings, [switch]$Annotations, [switch]$SmartSelection)
. "$PSScriptRoot\sdk.ps1"
Push-Location $projectRoot
try {
    Invoke-ProjectDotnet test tests/ScreenshotDock.Tests -c Release
    if ($Editor) { Invoke-ProjectDotnet run --project tests/ScreenshotDock.IntegrationTests -c Release -- --editor-tests (Join-Path $projectRoot 'artifacts\editor-verification') }
    if ($Pin) { Invoke-ProjectDotnet run --project tests/ScreenshotDock.IntegrationTests -c Release -- --pin-tests (Join-Path $projectRoot 'artifacts\pin-verification') }
    if ($Edge) { Invoke-ProjectDotnet run --project tests/ScreenshotDock.IntegrationTests -c Release -- --edge-tests (Join-Path $projectRoot 'artifacts\edge-verification') }
    if ($Settings) { Invoke-ProjectDotnet run --project tests/ScreenshotDock.IntegrationTests -c Release -- --settings-tests (Join-Path $projectRoot 'artifacts\settings-verification') }
    if ($Annotations) { Invoke-ProjectDotnet run --project tests/ScreenshotDock.IntegrationTests -c Release -- --annotation-tests (Join-Path $projectRoot 'artifacts\annotation-verification') }
    if ($SmartSelection) { Invoke-ProjectDotnet run --project tests/ScreenshotDock.IntegrationTests -c Release -- --smart-selection-tests (Join-Path $projectRoot 'artifacts\smart-selection-verification') }
    if ($Integration) { Invoke-ProjectDotnet run --project tests/ScreenshotDock.IntegrationTests -c Release -- (Join-Path $projectRoot 'artifacts\verification') }
} finally { Pop-Location }
