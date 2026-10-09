. "$PSScriptRoot\sdk.ps1"
Push-Location $projectRoot
try { Invoke-ProjectDotnet build ScreenshotDock.sln -c Release } finally { Pop-Location }
