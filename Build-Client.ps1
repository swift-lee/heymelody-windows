[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.9.2',
    [ValidateNotNullOrEmpty()][ValidateSet('x64', 'x86', 'arm64')][string[]]$Architectures = @('x64'),
    [switch]$SkipTests,
    [switch]$TestsOnly,
    [switch]$FinalizeOnly,
    [string]$ValidationPath,
    [string]$FilamentSource,
    [string]$FilamentRuntimeRoot,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$releaseRoot = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $root 'release' }
$stage = Join-Path $releaseRoot ('HeyMelody_Windows_' + $Version)
$targetArchitectures = @($Architectures | Select-Object -Unique)
if (-not $TestsOnly -and $targetArchitectures -contains 'x86') { throw 'The Filament native module supports Windows x64 and ARM64; a Windows x86 native module is not available.' }
if (-not $FilamentRuntimeRoot) { $FilamentRuntimeRoot = Join-Path $root 'artifacts/filament-compat' }
$FilamentRuntimeRoot = [IO.Path]::GetFullPath($FilamentRuntimeRoot)
$architectureSuffix = if ($targetArchitectures.Count -eq 3) { 'AllArchitectures' } else { $targetArchitectures -join '-' }
$archive = Join-Path $releaseRoot ('HeyMelody_Windows_' + $Version + '_' + $architectureSuffix + '.zip')
if ($ValidationPath) {
    $validation = Get-Content -LiteralPath $ValidationPath -Raw | ConvertFrom-Json
    if ($validation.schemaVersion -ne 2 -or $validation.version -ne $Version -or -not $validation.success) { throw 'Validation must be a successful schema-2 receipt for this exact release version.' }
}
if ($TestsOnly -and ($SkipTests -or $FinalizeOnly -or $ValidationPath)) { throw 'TestsOnly runs only the default test runners; it cannot be combined with SkipTests, FinalizeOnly or ValidationPath.' }
if (-not $TestsOnly -and ((Test-Path -LiteralPath $archive) -or ((Test-Path -LiteralPath $stage) -and -not $FinalizeOnly))) {
    throw 'This version already has an output directory or archive. Use a new version or review the existing output.'
}
if ($FinalizeOnly -and -not (Test-Path -LiteralPath $stage -PathType Container)) { throw 'There is no published directory to finalize.' }
$repositoryPackageCache = Join-Path $root 'artifacts/nuget-cache'
$restoreProperties = @('-p:RestorePackagesPath=' + $repositoryPackageCache)
$existingPackageCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
if (Test-Path -LiteralPath $existingPackageCache -PathType Container) {
    $restoreProperties += '-p:RestoreFallbackFolders=' + $existingPackageCache
}
if (-not $SkipTests -and -not $FinalizeOnly) {
    & dotnet run --project (Join-Path $root 'src/HeyMelody.Windows/OfflineChecks/HeyMelody.Windows.OfflineChecks.csproj') -c Release '-p:CopyDebugSymbolFilesFromPackages=false' @restoreProperties
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed: HeyMelody.Windows.OfflineChecks' }
    foreach ($project in @('HeyMelody.Diagnostics.Tests', 'HeyMelody.Protocol.Tests', 'HeyMelody.Resources.Tests', 'HeyMelody.Calibration.Tests', 'HeyMelody.Calibration.UI.Tests', 'HeyMelody.Devices.Tests', 'HeyMelody.Control.Tests', 'HeyMelody.Daily.Tests', 'HeyMelody.Probe.Tests', 'HeyMelody.Host.Tests', 'HeyMelody.Client.Tests', 'HeyMelody.App.Tests', 'HeyMelody.DeviceManager.Tests', 'HeyMelody.Rendering.Filament.Tests')) {
        & dotnet run --project (Join-Path $root ('tests/' + $project + '/' + $project + '.csproj')) -c Release '-p:CopyDebugSymbolFilesFromPackages=false' @restoreProperties
        if ($LASTEXITCODE -ne 0) { throw ('Tests failed: ' + $project) }
    }
}
if ($TestsOnly) {
    Write-Host 'All default test runners passed.'
    return
}
if (-not $FinalizeOnly) {
    foreach ($architecture in $targetArchitectures) {
        $rid = 'win-' + $architecture
        $nativeDirectory = Join-Path $FilamentRuntimeRoot ('runtimes/' + $rid + '/native')
        if (-not (Test-Path -LiteralPath (Join-Path $nativeDirectory 'filament-runtime.json'))) {
            $nativeArguments = @{ RuntimeIdentifier = $rid; OutputRoot = $FilamentRuntimeRoot; FetchBuildTools = $true; SkipSmoke = $true }
            if ($FilamentSource) { $nativeArguments.FilamentSource = $FilamentSource }
            else { $nativeArguments.FetchSource = $true }
            & (Join-Path $root 'tools/filament/Build-FilamentNative.ps1') @nativeArguments
            if ($LASTEXITCODE -ne 0) { throw ('Filament native build failed: ' + $rid) }
        }
        $nativeVerification = & (Join-Path $root 'tools/Verify-FilamentRuntime.ps1') -Directory $nativeDirectory -RuntimeIdentifier $rid -RequireCurrent
    }
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$scratch = Join-Path $releaseRoot ('build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$builds = @()
$packageCacheLine = & dotnet nuget locals global-packages --list
if ($LASTEXITCODE -ne 0 -or $packageCacheLine -notmatch '^global-packages:\s*(.+?)\s*$') { throw 'NuGet package-cache path is unavailable.' }
$packageCache = $Matches[1]
# ReadyToRun precompiles the IL-only UI libraries, so the first expander, model drag or wizard page does not pause
# for JIT; tiered compilation still promotes hot methods. Package size is not a constraint for these builds.
$publishProperties = @('-p:CopyDebugSymbolFilesFromPackages=false',
    '-p:PublishReadyToRun=true',
    '-p:CreateHardLinksForCopyLocalIfPossible=false',
    '-p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=false',
    '-p:CreateHardLinksForPublishFilesIfPossible=false',
    '-p:CreateHardLinksForAdditionalFilesIfPossible=false')
$publishProperties += $restoreProperties
$publishProperties += '-p:FilamentRuntimeRoot=' + $FilamentRuntimeRoot

function Resolve-PackageDirectory([object]$Assets, [string]$PackagePath) {
    # A package may have been resolved from either the repository cache or a fallback.
    foreach ($folder in @($Assets.packageFolders.PSObject.Properties.Name) + @($packageCache)) {
        if ([string]::IsNullOrWhiteSpace($folder)) { continue }
        $packageRoot = [IO.Path]::GetFullPath($folder).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $candidate = [IO.Path]::GetFullPath((Join-Path $folder $PackagePath))
        if (-not $candidate.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Resolved package path escaped its package cache.'
        }
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw ('Resolved package is unavailable in its declared caches: ' + $PackagePath)
}

function Export-PackageNotices([string]$AssetsPath, [string]$Destination) {
    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    $notices = @()
    foreach ($entry in $assets.libraries.PSObject.Properties | Sort-Object Name) {
        $library = $entry.Value
        if ($library.type -ne 'package' -or $entry.Name -like 'Microsoft.NETCore.App.Runtime.*') { continue }
        $parts = $entry.Name.Split('/')
        $packageDirectory = Resolve-PackageDirectory $assets $library.path
        $metadataPath = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $metadataPath) { throw ('Missing package metadata: ' + $entry.Name) }
        [xml]$metadataDocument = Get-Content -LiteralPath $metadataPath.FullName -Raw
        $metadata = $metadataDocument.SelectSingleNode('//*[local-name()="metadata"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        $licenseUrl = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
        $noticeDirectory = Join-Path $Destination ('licenses/' + $parts[0] + '/' + $parts[1])
        New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
        Copy-Item -LiteralPath $metadataPath.FullName -Destination $noticeDirectory
        $files = @($library.files | Where-Object { $_ -match '(^|/)(license|licence|copying|copyright|notice|third[-_]party[-_]notices)([._-]|$)' })
        if ($license -and $license.GetAttribute('type') -eq 'file') { $files += $license.InnerText }
        $copied = @()
        foreach ($file in $files | Sort-Object -Unique) {
            $source = [IO.Path]::GetFullPath((Join-Path $packageDirectory $file))
            $packagePrefix = [IO.Path]::GetFullPath($packageDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (-not $source.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package notice path escaped its package directory.' }
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing package notice: ' + $entry.Name + '/' + $file) }
            $target = Join-Path $noticeDirectory $file
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
            $copied += [ordered]@{ path = ('licenses/' + $parts[0] + '/' + $parts[1] + '/' + $file); sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $notices += [ordered]@{
            package = $parts[0]; version = $parts[1]
            licenseType = $(if ($license) { $license.GetAttribute('type') } else { $null })
            license = $(if ($license) { $license.InnerText } else { $null })
            licenseUrl = $(if ($licenseUrl) { $licenseUrl.InnerText } else { $null })
            copyright = $(if ($copyright) { $copyright.InnerText } else { $null })
            metadata = ('licenses/' + $parts[0] + '/' + $parts[1] + '/' + $metadataPath.Name)
            files = @($copied)
        }
    }
    if (-not ($notices | Where-Object { $_.package -eq 'Avalonia' }) -or -not ($notices | Where-Object { $_.package -eq 'FluentAvaloniaUI' })) { throw 'The resolved GUI packages do not include Avalonia and FluentAvaloniaUI.' }
    $supplemental = @()
    foreach ($licenseFile in Get-ChildItem -LiteralPath (Join-Path $root 'packaging/licenses') -File) {
        $licenseTarget = Join-Path $Destination ('licenses/upstream/' + $licenseFile.Name)
        New-Item -ItemType Directory -Path (Split-Path -Parent $licenseTarget) -Force | Out-Null
        Copy-Item -LiteralPath $licenseFile.FullName -Destination $licenseTarget
        $supplemental += [ordered]@{ path = ('licenses/upstream/' + $licenseFile.Name); sha256 = (Get-FileHash -LiteralPath $licenseTarget -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [ordered]@{ schemaVersion = 1; source = 'resolved NuGet project.assets.json'; packages = @($notices); supplementalNotices = @($supplemental) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Destination 'PACKAGE-NOTICES.json') -Encoding UTF8
}

foreach ($architecture in $targetArchitectures) {
    $rid = 'win-' + $architecture
    $filamentNativeDirectory = Join-Path $FilamentRuntimeRoot ('runtimes/' + $rid + '/native')
    # Existing packages can share hard-linked R2R files with the ordinary obj directory.
    # Each publication writes its own intermediate images so a running earlier package
    # cannot lock new outputs, and publishing cannot mutate that package through a link.
    $r2rRoot = Join-Path $scratch ('r2r/' + $rid)
    $app = Join-Path $stage $architecture
    $hostOutput = Join-Path $app 'backend'
    if (-not $FinalizeOnly) {
        Write-Host ('Publishing ' + $rid)
        & dotnet publish (Join-Path $root 'src/HeyMelody.App/HeyMelody.App.csproj') -c Release -r $rid --self-contained true -o $app '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' ('-p:Version=' + $Version) ('-p:_ReadyToRunOutputPath=' + (Join-Path $r2rRoot 'App')) @publishProperties --nologo
        if ($LASTEXITCODE -ne 0) { throw ('GUI publish failed: ' + $rid) }
        & dotnet publish (Join-Path $root 'src/HeyMelody.DeviceManager/HeyMelody.DeviceManager.csproj') -c Release -r $rid --self-contained true -o $app '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' ('-p:Version=' + $Version) ('-p:_ReadyToRunOutputPath=' + (Join-Path $r2rRoot 'DeviceManager')) @publishProperties --nologo
        if ($LASTEXITCODE -ne 0) { throw ('Device manager publish failed: ' + $rid) }
        foreach ($component in @('HeyMelody.FitTest', 'HeyMelody.HearingSetup')) {
            & dotnet publish (Join-Path $root ('src/' + $component + '/' + $component + '.csproj')) -c Release -r $rid --self-contained true -o $app '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' ('-p:Version=' + $Version) ('-p:_ReadyToRunOutputPath=' + (Join-Path $r2rRoot $component)) @publishProperties --nologo
            if ($LASTEXITCODE -ne 0) { throw ('Calibration component publish failed: ' + $component + '/' + $rid) }
        }
        & dotnet publish (Join-Path $root 'src/HeyMelody.Host/HeyMelody.Host.csproj') -c Release -r $rid --self-contained true -o $hostOutput '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' ('-p:Version=' + $Version) ('-p:_ReadyToRunOutputPath=' + (Join-Path $r2rRoot 'Host')) @publishProperties --nologo
        if ($LASTEXITCODE -ne 0) { throw ('Host publish failed: ' + $rid) }
        Copy-Item -Path (Join-Path $filamentNativeDirectory '*') -Destination $app -Recurse -Force
    }
    # The Host falls back to the GUI's ResourceBundles (..\ResourceBundles) and hands the windows paths into it; a second
    # copy in backend/ would only add about 34 MB. Also applies to a staged publication finalized again.
    $hostBundle = Join-Path $hostOutput 'ResourceBundles'
    if (Test-Path -LiteralPath $hostBundle) { Remove-Item -LiteralPath $hostBundle -Recurse -Force }
    $nativeVerification = & (Join-Path $root 'tools/Verify-FilamentRuntime.ps1') -Directory $app -RuntimeIdentifier $rid -RequireCurrent
    $nestedNative = Join-Path $app ('runtimes/' + $rid + '/native')
    if ((Test-Path -LiteralPath (Join-Path $nestedNative 'HeyMelody.Filament.Native.dll')) -or (Test-Path -LiteralPath (Join-Path $nestedNative 'filament-runtime.json')) -or (Test-Path -LiteralPath (Join-Path $nestedNative 'hm_filament_probe.exe'))) {
        $nestedVerification = & (Join-Path $root 'tools/Verify-FilamentRuntime.ps1') -Directory $nestedNative -RuntimeIdentifier $rid -RequireCurrent
        if ($nestedVerification.manifestSha256 -cne $nativeVerification.manifestSha256) { throw ('Published runtime copies differ: ' + $rid) }
    }
    # Native NuGet runtime assets can include PDBs despite the managed-symbol option.
    # They are unnecessary for execution and need not inflate the portable package.
    foreach ($debugSymbol in Get-ChildItem -LiteralPath $app -Filter '*.pdb' -File -Recurse) {
        Remove-Item -LiteralPath $debugSymbol.FullName -Force
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $app 'HeyMelody.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($runtimeEntry in @(
        @{ directory = $app; name = 'HeyMelody'; assets = 'src/HeyMelody.App/obj/project.assets.json' },
        @{ directory = $app; name = 'HeyMelody.DeviceManager'; assets = 'src/HeyMelody.DeviceManager/obj/project.assets.json' },
        @{ directory = $app; name = 'HeyMelody.FitTest'; assets = 'src/HeyMelody.FitTest/obj/project.assets.json' },
        @{ directory = $app; name = 'HeyMelody.HearingSetup'; assets = 'src/HeyMelody.HearingSetup/obj/project.assets.json' },
        @{ directory = $hostOutput; name = 'HeyMelody.Host'; assets = 'src/HeyMelody.Host/obj/project.assets.json' }
    )) {
        $runtimeDirectory = $runtimeEntry.directory
        $publishedRuntime = Get-Content -LiteralPath (Join-Path $runtimeDirectory ($runtimeEntry.name + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
        $runtimeVersion = @($publishedRuntime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })[0].version
        $runtimeAssets = Get-Content -LiteralPath (Join-Path $root $runtimeEntry.assets) -Raw | ConvertFrom-Json
        $corePackage = Resolve-PackageDirectory $runtimeAssets ('microsoft.netcore.app.runtime.' + $rid + '/' + $runtimeVersion)
        Copy-Item -LiteralPath (Join-Path $corePackage 'LICENSE.TXT') -Destination (Join-Path $runtimeDirectory 'LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $corePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $runtimeDirectory 'THIRD-PARTY-NOTICES.txt')
    }
    if (@($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' }).Count -ne 0) { throw 'The Avalonia GUI must not publish the Windows Desktop runtime.' }
    $managerConfig = Get-Content -LiteralPath (Join-Path $app 'HeyMelody.DeviceManager.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (@($managerConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' }).Count -ne 0) { throw 'The device manager must not publish the Windows Desktop runtime.' }
    $guiRuntime = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })[0].version
    $managerRuntime = @($managerConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })[0].version
    if ($guiRuntime -ne $managerRuntime) { throw 'The GUI and device manager must share the exact .NET runtime version.' }
    # Both GUI projects deliberately use identical package versions in the same publish directory.
    $guiAssets = Get-Content -LiteralPath (Join-Path $root 'src/HeyMelody.App/obj/project.assets.json') -Raw | ConvertFrom-Json
    $managerAssets = Get-Content -LiteralPath (Join-Path $root 'src/HeyMelody.DeviceManager/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($dependency in $managerAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        if (-not $guiAssets.libraries.PSObject.Properties[$dependency.Name]) { throw ('The manager has a dependency not covered by the shared GUI notices: ' + $dependency.Name) }
    }
    foreach ($component in @('HeyMelody.FitTest', 'HeyMelody.HearingSetup')) {
        $componentConfig = Get-Content -LiteralPath (Join-Path $app ($component + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
        $componentFrameworks = @($componentConfig.runtimeOptions.includedFrameworks)
        if ($componentFrameworks.Count -ne 1 -or $componentFrameworks[0].name -ne 'Microsoft.NETCore.App' -or $componentFrameworks[0].version -ne $guiRuntime) { throw ('Calibration component does not share the GUI runtime: ' + $component) }
        $componentAssets = Get-Content -LiteralPath (Join-Path $root ('src/' + $component + '/obj/project.assets.json')) -Raw | ConvertFrom-Json
        foreach ($dependency in $componentAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
            if (-not $guiAssets.libraries.PSObject.Properties[$dependency.Name]) { throw ('Calibration dependency is not covered by shared GUI notices: ' + $dependency.Name) }
        }
    }
    foreach ($resourceFile in @('HeyMelody.Resources.dll', 'ResourceBundles/artwork.json', 'ResourceBundles/provenance.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $app $resourceFile) -PathType Leaf)) { throw ('Offline resources missing from publication: ' + $resourceFile) }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $hostOutput 'HeyMelody.Resources.dll') -PathType Leaf)) { throw 'Offline resources missing from backend publication: HeyMelody.Resources.dll' }
    if (Test-Path -LiteralPath (Join-Path $hostOutput 'ResourceBundles')) { throw ('The backend must share the GUI ResourceBundles: ' + $rid) }
    Export-PackageNotices (Join-Path $root 'src/HeyMelody.App/obj/project.assets.json') $app
    Export-PackageNotices (Join-Path $root 'src/HeyMelody.Host/obj/project.assets.json') $hostOutput
    $builds += [ordered]@{
        architecture = $architecture; rid = $rid; selfContained = $true
        gui = ($architecture + '/HeyMelody.exe'); deviceManager = ($architecture + '/HeyMelody.DeviceManager.exe'); host = ($architecture + '/backend/HeyMelody.Host.exe')
        fitTest = ($architecture + '/HeyMelody.FitTest.exe'); hearingSetup = ($architecture + '/HeyMelody.HearingSetup.exe')
        guiFramework = 'net10.0'; deviceManagerFramework = 'net10.0'; fitTestFramework = 'net10.0'; hearingSetupFramework = 'net10.0'; hostFramework = 'net10.0-windows10.0.19041.0'; uiToolkit = 'Avalonia + FluentAvalonia'
        guiSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'HeyMelody.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        deviceManagerSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'HeyMelody.DeviceManager.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        fitTestSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'HeyMelody.FitTest.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        hearingSetupSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'HeyMelody.HearingSetup.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        resourceIndexSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'ResourceBundles/artwork.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        hostResourceBundles = 'gui'
        guiDiagnosticsSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'HeyMelody.Diagnostics.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        hostDiagnosticsSha256 = (Get-FileHash -LiteralPath (Join-Path $hostOutput 'HeyMelody.Diagnostics.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        hostSha256 = (Get-FileHash -LiteralPath (Join-Path $hostOutput 'HeyMelody.Host.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        hardwareExecutionVerified = $false; validationScope = 'build-only; see VALIDATION.json for actual execution evidence'
        modelRenderer = $nativeVerification
    }
}
Copy-Item -LiteralPath (Join-Path $root 'packaging/Start-HeyMelody.ps1') -Destination (Join-Path $stage 'Start-HeyMelody.ps1')
Copy-Item -LiteralPath (Join-Path $root 'packaging/Start-HeyMelody.cmd') -Destination (Join-Path $stage 'Start-HeyMelody.cmd')
Copy-Item -LiteralPath (Join-Path $root 'packaging/Start-3D-Demo.cmd') -Destination (Join-Path $stage 'Start-3D-Demo.cmd')
Copy-Item -LiteralPath (Join-Path $root 'packaging/README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'packaging/README.md') -Destination (Join-Path $stage 'README-使用说明.md')
Copy-Item -LiteralPath (Join-Path $root 'packaging/SOURCE-NOTES.md') -Destination (Join-Path $stage 'SOURCE-NOTES.md')
if ($ValidationPath) {
    foreach ($build in $builds) {
        $receipt = @($validation.builds | Where-Object { $_.architecture -eq $build.architecture })
        if ($receipt.Count -ne 1 -or $receipt[0].guiSha256 -ne $build.guiSha256 -or $receipt[0].deviceManagerSha256 -ne $build.deviceManagerSha256 -or $receipt[0].fitTestSha256 -ne $build.fitTestSha256 -or $receipt[0].hearingSetupSha256 -ne $build.hearingSetupSha256 -or $receipt[0].hostSha256 -ne $build.hostSha256) { throw ('Validation executable hashes do not match: ' + $build.architecture) }
    }
    Copy-Item -LiteralPath $ValidationPath -Destination (Join-Path $stage 'VALIDATION.json')
}
$manifest = [ordered]@{ schemaVersion = 2; version = $Version; builtAtUtc = [DateTime]::UtcNow.ToString('o'); builds = $builds; modelRenderer = 'Filament'; diagnosticLogging = 'one-host-session-debug-jsonl-v1'; nativeArm64ExecutionVerified = $false; validationScope = 'build-only unless an exact-version execution receipt is supplied' }
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
# This is the exact owned GUID directory created above, verified inside release before recursive cleanup.
$resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
$resolvedRelease = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
if (-not $resolvedScratch.StartsWith($resolvedRelease, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe build scratch path.' }
Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
[ordered]@{ package = $archive; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); version = $Version } | ConvertTo-Json -Compress
