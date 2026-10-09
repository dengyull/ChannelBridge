param([string]$Tag = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$project = Get-Content ChannelBridge.csproj
    $version = [string]$project.Project.PropertyGroup.Version
    if ($Tag -and $Tag -ne "v$version") { throw "Tag $Tag does not match project version v$version" }
    $artifactRoot = Join-Path $root 'artifacts'
    $publish = Join-Path $artifactRoot 'publish'
    $reports = Join-Path $artifactRoot 'reports'
    New-Item -ItemType Directory -Path $publish,$reports -Force | Out-Null
    dotnet restore ChannelBridge.csproj -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet publish ChannelBridge.csproj --no-restore -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $exe = Join-Path $publish 'ChannelBridge.exe'
    foreach ($test in @('self-test','latency-check','audio-test-check','ui-test','wireless-check','airplay-check')) {
        $report = Join-Path $reports "$test.txt"
        $process = Start-Process -FilePath $exe -ArgumentList @("--$test", ('"' + $report + '"')) -PassThru -WindowStyle Hidden
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw "$test timed out" }
        if ($process.ExitCode -ne 0) { if (Test-Path $report) { Get-Content $report }; throw "$test failed ($($process.ExitCode))" }
        if (!(Test-Path $report) -or !(Select-String -Path $report -Pattern '^PASS:' -Quiet)) { throw "$test produced no passing report" }
    }
    foreach ($language in @('english','chinese')) {
        $report = Join-Path $reports "ui-$language.txt"
        $process = Start-Process -FilePath $exe -ArgumentList @('--ui-test',('"' + $report + '"'),"--$language") -PassThru -WindowStyle Hidden
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw "UI $language timed out" }
        if ($process.ExitCode -ne 0) { if (Test-Path $report) { Get-Content $report }; throw "UI $language failed" }
    }
    Copy-Item LICENSE,THIRD_PARTY_NOTICES.md,README.md,README.en.md $publish
    Copy-Item docs,licenses $publish -Recurse -Force
    Copy-Item 'docs/使用说明.md','docs/User guide.md' $publish
    $nugetLine = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw 'Unable to locate package cache' }
    $nugetRoot = ($nugetLine -replace '^[^:]+:\s*','').Trim()
    $assets = Get-Content 'obj/project.assets.json' -Raw | ConvertFrom-Json
    $runtimeDependencies = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies
    foreach ($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packagePath = Join-Path $nugetRoot $pack
        if (!(Test-Path $packagePath)) { throw "Runtime pack not found: $pack" }
        $dependency = $runtimeDependencies | Where-Object name -EQ $pack | Select-Object -First 1
        if (!$dependency) { throw "Resolved runtime version not found: $pack" }
        $resolvedVersion = ($dependency.version.Trim('[',']') -split ',')[0].Trim()
        $versionFolder = Get-Item (Join-Path $packagePath $resolvedVersion)
        foreach ($notice in Get-ChildItem $versionFolder.FullName -File | Where-Object Name -Match 'LICENSE|THIRD.PARTY') {
            Copy-Item $notice.FullName (Join-Path $publish "licenses/$pack-$($versionFolder.Name)-$($notice.Name)")
        }
    }
    $zipName = "ChannelBridge-v$version-win-x64.zip"
    $zip = Join-Path $artifactRoot $zipName
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal -Force
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha  $zipName" | Set-Content (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Built $zipName"
}
finally { Pop-Location }
