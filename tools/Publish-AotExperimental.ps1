# Invoked in Build-Client.ps1's scope after the common tests and native-runtime gates.
# Keep the production ReadyToRun path separate from this deliberately mixed experiment.
function Merge-ExperimentalPublication([string]$Source, [string]$Destination, [switch]$RequireIdentical) {
    $sourcePrefix = [IO.Path]::GetFullPath($Source).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        if ($file.Extension -eq '.pdb') { continue }
        $relative = [IO.Path]::GetRelativePath($sourcePrefix, $file.FullName)
        $target = Join-Path $Destination $relative
        if ($RequireIdentical -and (Test-Path -LiteralPath $target -PathType Leaf)) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                throw ('AOT/shared publication file conflict: ' + $relative)
            }
            continue
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Get-ExperimentalPackageLicense([string]$Directory) {
    $metadataFiles = @(Get-ChildItem -LiteralPath $Directory -Filter '*.nuspec' -File)
    if ($metadataFiles.Count -ne 1) { throw ('Runtime package metadata is missing or ambiguous: ' + $Directory) }
    [xml]$document = [IO.File]::ReadAllText($metadataFiles[0].FullName)
    $metadata = $document.SelectSingleNode('//*[local-name()="metadata"]')
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    [pscustomobject]@{
        path = $metadataFiles[0].FullName
        licenseType = $(if ($license) { $license.GetAttribute('type') } else { '' })
        license = $(if ($license) { $license.InnerText.Trim() } else { '' })
        repository = $(if ($repository) { $repository.GetAttribute('url') } else { '' })
        commit = $(if ($repository) { $repository.GetAttribute('commit') } else { '' })
    }
}

function Get-ExperimentalLicenseSource([string]$RuntimeDirectory, [string]$RuntimePackage,
    [string]$SharedRuntimeDirectory, [string]$SharedRuntimePackage, [string]$FileName) {
    $metadata = Get-ExperimentalPackageLicense $RuntimeDirectory
    $source = Join-Path $RuntimeDirectory $FileName
    $basis = 'own-package'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        if ($FileName -ne 'LICENSE.TXT') { throw ('The actual runtime package is missing its notice: ' + $RuntimePackage + '/' + $FileName) }
        $shared = Get-ExperimentalPackageLicense $SharedRuntimeDirectory
        # ILCompiler packages declare MIT through SPDX and intentionally omit a
        # license file. Reuse the unmodified .NET MIT text only from the exact
        # same source revision; retain both original nuspecs and compiler notices.
        if ($metadata.licenseType -ne 'expression' -or $metadata.license -ne 'MIT' -or
            $shared.licenseType -ne 'expression' -or $shared.license -ne 'MIT' -or
            -not $metadata.commit -or $metadata.commit -cne $shared.commit -or
            -not $metadata.repository -or $metadata.repository -cne $shared.repository) {
            throw 'A missing Native AOT license file requires matching MIT declarations and an identical source repository/revision.'
        }
        $source = Join-Path $SharedRuntimeDirectory $FileName
        $RuntimePackage = $SharedRuntimePackage
        $metadata = $shared
        $basis = 'matching-MIT-and-source-revision'
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('The proven runtime license source is missing: ' + $source) }
    [pscustomobject]@{ path = $source; package = $RuntimePackage; metadata = $metadata; basis = $basis }
}

$architecture = 'x64'
$rid = 'win-x64'
$app = Join-Path $stage $architecture
$hostOutput = Join-Path $app 'backend'
$filamentNativeDirectory = Join-Path $FilamentRuntimeRoot ('runtimes/' + $rid + '/native')
$roles = @(
    @{ role = 'deviceManager'; project = 'HeyMelody.DeviceManager'; name = 'HeyMelody.DeviceManager'; compilation = 'ready-to-run' },
    @{ role = 'fitTest'; project = 'HeyMelody.FitTest'; name = 'HeyMelody.FitTest'; compilation = 'ready-to-run' },
    @{ role = 'hearingSetup'; project = 'HeyMelody.HearingSetup'; name = 'HeyMelody.HearingSetup'; compilation = 'ready-to-run' },
    @{ role = 'gui'; project = 'HeyMelody.App'; name = 'HeyMelody'; compilation = 'native-aot' },
    @{ role = 'host'; project = 'HeyMelody.Host'; name = 'HeyMelody.Host'; compilation = 'native-aot' }
)
$experimentalPublications = @{}
$sdkVersion = (& dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the .NET SDK version.' }
foreach ($component in $roles) {
    $componentRoot = Join-Path $scratch ('experimental/' + $component.role)
    $componentOutput = Join-Path $componentRoot 'publish'
    $artifactsPath = Join-Path $componentRoot 'artifacts'
    $arguments = @(
        'publish', (Join-Path $root ('src/' + $component.project + '/' + $component.project + '.csproj')),
        '-c', 'Release', '-r', $rid, '--self-contained', 'true', '-o', $componentOutput,
        '-p:PublishSingleFile=false', '-p:CopyDebugSymbolFilesFromPackages=false',
        '-p:CreateHardLinksForCopyLocalIfPossible=false', '-p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=false',
        '-p:CreateHardLinksForPublishFilesIfPossible=false', '-p:CreateHardLinksForAdditionalFilesIfPossible=false',
        '-p:UseArtifactsOutput=true', ('-p:ArtifactsPath=' + $artifactsPath),
        ('-p:FilamentRuntimeRoot=' + $FilamentRuntimeRoot), ('-p:Version=' + $Version), '--nologo'
    ) + $restoreProperties
    if ($component.compilation -eq 'native-aot') {
        $arguments += @('-p:PublishAot=true', '-p:PublishReadyToRun=false', '-p:PublishTrimmed=true')
    } else {
        $arguments += @('-p:PublishAot=false', '-p:PublishReadyToRun=true', '-p:PublishTrimmed=false')
    }
    Write-Host ('Publishing ' + $component.project + ' (' + $component.compilation + ')')
    $previousPublishPath = $env:PATH
    try {
        if ($component.compilation -eq 'native-aot') {
            # The SDK's findvcvarsall.bat invokes vswhere by name. A developer
            # shell alone may set VC tools without adding the installer directory.
            $vswherePath = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
            if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
                $vswhereCommand = Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue
                if (-not $vswhereCommand) { throw 'Native AOT needs Visual Studio C++ tools and a resolvable vswhere.exe.' }
                $vswherePath = $vswhereCommand.Source
            }
            if ((Get-Item -LiteralPath $vswherePath).Length -eq 0) { throw 'The resolved vswhere.exe is empty.' }
            $env:PATH = (Split-Path -Parent $vswherePath) + [IO.Path]::PathSeparator + $previousPublishPath
        }
        & dotnet @arguments
        $publishExitCode = $LASTEXITCODE
    } finally {
        $env:PATH = $previousPublishPath
    }
    if ($publishExitCode -ne 0) { throw ('Experimental publish failed: ' + $component.role) }
    $assetsPath = Join-Path $artifactsPath ('obj/' + $component.project + '/project.assets.json')
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) { throw ('Isolated assets are missing: ' + $assetsPath) }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | Sort-Object Name | ForEach-Object {
        $parts = $_.Name.Split('/')
        [ordered]@{ package = $parts[0]; version = $parts[1]; sha512 = $_.Value.sha512 }
    })
    foreach ($download in @(Get-DownloadedCompilerPackages $assets)) {
        if (@($packages | Where-Object { $_.package -eq $download.package -and $_.version -eq $download.version }).Count) { continue }
        $packages += [ordered]@{ package = $download.package; version = $download.version; sha512 = $download.sha512 }
    }
    $projects = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' } | ForEach-Object { $_.Name } | Sort-Object)
    $executable = Join-Path $componentOutput ($component.name + '.exe')
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw ('Missing published executable: ' + $component.role) }
    if ($component.compilation -eq 'native-aot') {
        foreach ($suffix in @('.dll', '.deps.json', '.runtimeconfig.json')) {
            if (Test-Path -LiteralPath (Join-Path $componentOutput ($component.name + $suffix))) { throw ('Unexpected managed AOT entry artifact: ' + $component.role + $suffix) }
        }
        if (Test-Path -LiteralPath (Join-Path $componentOutput 'coreclr.dll')) { throw ('AOT publication contains CoreCLR: ' + $component.role) }
    }
    $destination = if ($component.role -eq 'host') { $hostOutput } else { $app }
    Merge-ExperimentalPublication $componentOutput $destination -RequireIdentical:($component.compilation -eq 'native-aot')
    $experimentalPublications[$component.role] = @{
        assetsPath = $assetsPath; assets = $assets
        evidence = [ordered]@{
            role = $component.role; compilation = $component.compilation
            sourceAssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash.ToLowerInvariant()
            executable = $(if ($component.role -eq 'host') { 'backend/' + $component.name + '.exe' } else { $component.name + '.exe' })
            executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
            packages = $packages; projects = $projects
        }
    }
}
Merge-ExperimentalPublication $filamentNativeDirectory $app -RequireIdentical
$hostBundle = Join-Path $hostOutput 'ResourceBundles'
if (Test-Path -LiteralPath $hostBundle) {
    $resolvedHostBundle = [IO.Path]::GetFullPath($hostBundle)
    if (-not $resolvedHostBundle.StartsWith([IO.Path]::GetFullPath($stage).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe experimental resource cleanup path.' }
    Remove-Item -LiteralPath $resolvedHostBundle -Recurse -Force
}
foreach ($debugSymbol in Get-ChildItem -LiteralPath $app -Filter '*.pdb' -File -Recurse) { Remove-Item -LiteralPath $debugSymbol.FullName -Force }

$managerRuntime = Get-Content -LiteralPath (Join-Path $app 'HeyMelody.DeviceManager.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = @($managerRuntime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
if (-not $runtimeVersion) { throw 'The R2R manager runtime version is unavailable.' }
foreach ($component in $roles | Where-Object compilation -eq 'ready-to-run') {
    $configuration = Get-Content -LiteralPath (Join-Path $app ($component.name + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if (@($configuration.runtimeOptions.includedFrameworks).Count -ne 1 -or $configuration.runtimeOptions.includedFrameworks[0].version -ne $runtimeVersion) { throw 'Experimental R2R components must share the exact .NET runtime.' }
}
$compilerPackages = @($experimentalPublications.gui.evidence.packages | Where-Object package -Match '(?i)ilcompiler')
if (-not $compilerPackages -or @($compilerPackages | Where-Object version -ne $runtimeVersion).Count) { throw 'Native AOT compiler and R2R runtime versions must match.' }
$runtimePackage = Resolve-PackageDirectory $experimentalPublications.deviceManager.assets ('microsoft.netcore.app.runtime.' + $rid + '/' + $runtimeVersion)
$aotLicensePackage = @($experimentalPublications.host.evidence.packages | Where-Object { $_.package -eq 'runtime.win-x64.Microsoft.DotNet.ILCompiler' })
if ($aotLicensePackage.Count -ne 1) { throw 'The resolved Windows x64 Native AOT runtime/compiler package is missing or ambiguous.' }
$aotRuntimePackage = Resolve-PackageDirectory $experimentalPublications.host.assets ($aotLicensePackage[0].package.ToLowerInvariant() + '/' + $aotLicensePackage[0].version)
$runtimeLicenseSources = [ordered]@{}
foreach ($runtimeLicense in @(
    @{ role = 'gui'; directory = $app; source = $runtimePackage; package = ('Microsoft.NETCore.App.Runtime.' + $rid) },
    @{ role = 'host'; directory = $hostOutput; source = $aotRuntimePackage; package = 'runtime.win-x64.Microsoft.DotNet.ILCompiler' }
)) {
    $licenseFiles = @()
    foreach ($file in @(@{ source = 'LICENSE.TXT'; destination = 'LICENSE.txt' }, @{ source = 'THIRD-PARTY-NOTICES.TXT'; destination = 'THIRD-PARTY-NOTICES.txt' })) {
        $licenseSource = Get-ExperimentalLicenseSource $runtimeLicense.source $runtimeLicense.package $runtimePackage ('Microsoft.NETCore.App.Runtime.' + $rid) $file.source
        $destination = Join-Path $runtimeLicense.directory $file.destination
        Copy-Item -LiteralPath $licenseSource.path -Destination $destination
        $metadataRelative = 'licenses/' + $licenseSource.package + '/' + $runtimeVersion + '/' + [IO.Path]::GetFileName($licenseSource.metadata.path)
        $metadataDestination = Join-Path $runtimeLicense.directory $metadataRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $metadataDestination) -Force | Out-Null
        Copy-Item -LiteralPath $licenseSource.metadata.path -Destination $metadataDestination
        $licenseFiles += [ordered]@{
            path = $file.destination; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            sourcePackage = $licenseSource.package; sourceVersion = $runtimeVersion; sourcePath = $file.source
            sourceMetadata = $metadataRelative; sourceMetadataSha256 = (Get-FileHash -LiteralPath $metadataDestination -Algorithm SHA256).Hash.ToLowerInvariant()
            basis = $licenseSource.basis
        }
    }
    $runtimeLicenseSources[$runtimeLicense.role] = [ordered]@{ package = $runtimeLicense.package; version = $runtimeVersion; files = $licenseFiles }
}
$guiAssets = @($roles | Where-Object role -ne 'host' | ForEach-Object { $experimentalPublications[$_.role].assetsPath })
Export-PackageNotices $guiAssets $app
Export-PackageNotices $experimentalPublications.host.assetsPath $hostOutput
$dependencyEvidence = [ordered]@{
    schemaVersion = 1; source = 'isolated resolved NuGet project.assets.json'
    sdkVersion = $sdkVersion; runtimeVersion = $runtimeVersion
    runtimeLicenseSources = $runtimeLicenseSources
    components = @($roles | ForEach-Object { $experimentalPublications[$_.role].evidence })
}
$evidencePath = Join-Path $app 'BUILD-DEPENDENCIES.json'
$dependencyEvidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
$nativeVerification = & (Join-Path $root 'tools/Verify-FilamentRuntime.ps1') -Directory $app -RuntimeIdentifier $rid -RequireCurrent
$abi = & (Join-Path $root 'tools/ci/Invoke-NativeAbiCheck.ps1') -NativeDirectory $app -RuntimeIdentifier $rid -OutputRoot (Join-Path $NativeAbiOutputRoot "$rid-package")
$nativeAbiChecks.Add([ordered]@{ phase = 'package'; receiptPath = $abi.receiptPath; validation = $abi.validation })
$componentCompilation = [ordered]@{}
foreach ($component in $roles) { $componentCompilation[$component.role] = $component.compilation }
$build = [ordered]@{
    architecture = $architecture; rid = $rid; selfContained = $true
    componentCompilation = $componentCompilation
    toolchain = [ordered]@{ sdkVersion = $sdkVersion; runtimeVersion = $runtimeVersion; ilCompilerPackages = $compilerPackages }
    dependencyEvidence = 'x64/BUILD-DEPENDENCIES.json'
    dependencyEvidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    hostResourceBundles = 'gui'; resourceIndexSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'ResourceBundles/artwork.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    hardwareExecutionVerified = $false; validationScope = 'experimental mixed Native AOT/R2R build-only; see exact-hash execution evidence'
    modelRenderer = $nativeVerification
}
foreach ($component in $roles) {
    $role = $component.role
    $relative = 'x64/' + $experimentalPublications[$role].evidence.executable
    $build[$role] = $relative
    $build[$role + 'Sha256'] = $experimentalPublications[$role].evidence.executableSha256
    $build[$role + 'Framework'] = 'net10.0-windows10.0.19041.0'
}
$builds += $build
