. "$PSScriptRoot\sdk.ps1"
Push-Location $projectRoot
try { Invoke-ProjectDotnet run --project src/ScreenshotDock.App -c Release } finally { Pop-Location }
