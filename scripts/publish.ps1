. "$PSScriptRoot\sdk.ps1"
Push-Location $projectRoot
try {
    $publishDirectory = Join-Path $projectRoot 'artifacts\ScreenshotDock-win-x64'
    Invoke-ProjectDotnet publish src/ScreenshotDock.App -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o $publishDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\使用说明.md') -Destination (Join-Path $publishDirectory '使用说明.md') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\验收记录.md') -Destination (Join-Path $publishDirectory '验收记录.md') -Force
    foreach ($document in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination (Join-Path $publishDirectory $document) -Force
    }
    $licenseDirectory = Join-Path $publishDirectory 'licenses'
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\Apache-2.0.txt') -Destination (Join-Path $licenseDirectory 'Apache-2.0.txt') -Force
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src\ScreenshotDock.App\obj\project.assets.json') -Raw | ConvertFrom-Json
    $runtimeNames = @{
        'Microsoft.NETCore.App.Runtime.win-x64' = 'dotnet-runtime'
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' = 'windowsdesktop-runtime'
    }
    $runtimePackages = @($assets.project.frameworks.PSObject.Properties | ForEach-Object { $_.Value.downloadDependencies } |
        Where-Object { $runtimeNames.ContainsKey($_.name) } | Sort-Object name -Unique)
    if ($runtimePackages.Count -ne $runtimeNames.Count) { throw 'The restored Windows runtime packages could not be located.' }
    $runtimeVersions = [ordered]@{}
    foreach ($runtime in $runtimePackages) {
        if ($runtime.version -notmatch '^\[([^,]+),\s*\1\]$') { throw "Unexpected runtime package version: $($runtime.version)" }
        $version = $Matches[1]
        $relativePath = Join-Path ($runtime.name.ToLowerInvariant()) $version
        $packageDirectory = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $relativePath } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $packageDirectory) { throw "Runtime package not found: $($runtime.name)/$version" }
        $documents = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^LICENSE(\.TXT)?$|^THIRD-PARTY-NOTICES\.TXT$' })
        if (-not ($documents | Where-Object { $_.Name -match '^LICENSE(\.TXT)?$' })) { throw "Runtime license not found: $($runtime.name)" }
        $destination = Join-Path $licenseDirectory $runtimeNames[$runtime.name]
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($document in $documents) { Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $destination $document.Name) -Force }
        $runtimeVersions[$runtime.name] = $version
    }
    $runtimeVersions | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $licenseDirectory 'runtime-packages.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath (Join-Path $projectRoot 'artifacts\ScreenshotDock-1.0.0-win-x64.zip') -Force
    Write-Output "Ready: $publishDirectory\ScreenshotDock.exe"
} finally { Pop-Location }
