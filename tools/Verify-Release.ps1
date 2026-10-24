<#
.SYNOPSIS
Verifies an existing three-architecture release and writes its static receipt.
.DESCRIPTION
Version is a stable semantic version (major.minor.patch), matching the managed
entry assembly version major.minor.patch.0. The staging directory and ZIP must
already exist under release/. This script does not build or execute the apps.
.EXAMPLE
pwsh -File tools/Verify-Release.ps1 -Version 0.8.0 -VerifyDeviceArtwork
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [string]$Version,
    [switch]$VerifyDeviceArtwork,
    [ValidateNotNullOrEmpty()][ValidateSet('x64', 'x86', 'arm64')][string[]]$Architectures = @('x64', 'x86', 'arm64'),
    [string]$OutputPath,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutputRoot = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $taskRoot 'release' }
$taskStage = Join-Path $taskOutputRoot ('HeyMelody_Windows_' + $Version)
$targetArchitectures = @($Architectures | Select-Object -Unique)
$architectureSuffix = if ($targetArchitectures.Count -eq 3) { 'AllArchitectures' } else { $targetArchitectures -join '-' }
$taskArchive = Join-Path $taskOutputRoot ('HeyMelody_Windows_' + $Version + '_' + $architectureSuffix + '.zip')
$taskReceipt = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $taskOutputRoot ('HeyMelody_Windows_' + $Version + '_STATIC-VALIDATION.json') }
if (Test-Path -LiteralPath $taskReceipt) { throw 'The static receipt already exists; use a new OutputPath.' }

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Pe([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        Require ($reader.ReadUInt16() -eq 0x5A4D) ('Missing MZ header: ' + $Path)
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        Require ($reader.ReadUInt32() -eq 0x4550) ('Missing PE header: ' + $Path)
        $machine = $reader.ReadUInt16()
        $optionalOffset = $peOffset + 24
        $stream.Position = $optionalOffset
        $magic = $reader.ReadUInt16()
        Require ($magic -in @(0x10B, 0x20B)) ('Unsupported PE optional header: ' + $Path)
        $stream.Position = $optionalOffset + 68
        $subsystem = $reader.ReadUInt16()
        $directoryOffset = if ($magic -eq 0x10B) { 96 } else { 112 }
        $stream.Position = $optionalOffset + $directoryOffset + 14 * 8
        $cliRva = $reader.ReadUInt32()
        [ordered]@{ machine = ('0x{0:x4}' -f $machine); machineValue = $machine; subsystem = $subsystem; managed = ($cliRva -ne 0) }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}
function Assembly-Version([string]$Path) { [Reflection.AssemblyName]::GetAssemblyName($Path).Version.ToString() }
function Checked-File([string]$Directory, [string]$RelativePath) {
    Require (-not [IO.Path]::IsPathRooted($RelativePath)) ('Unexpected rooted package path: ' + $RelativePath)
    $basePath = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Directory $RelativePath))
    Require ($path.StartsWith($basePath, [StringComparison]::OrdinalIgnoreCase)) ('Package path escaped its directory: ' + $RelativePath)
    Require ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -gt 0) ('Missing or empty package file: ' + $path)
    $path
}
function Add-CriticalFile([string]$Path) {
    [void]$criticalPaths.Add([IO.Path]::GetRelativePath($taskStage, $Path).Replace('\', '/'))
}
function Check-ResourceBundle([string]$Bundle) {
    $reference = Join-Path $taskRoot 'src/HeyMelody.Resources/Bundles'
    $manifests = @('artwork.json', 'provenance.json', 'supplemental.json')
    $actualManifests = @(Get-ChildItem -LiteralPath $Bundle -File | ForEach-Object Name)
    Require (@(Compare-Object $manifests $actualManifests).Count -eq 0) 'Unexpected offline resource manifest.'
    foreach ($name in $manifests) {
        Require ((Hash-File (Checked-File $Bundle $name)) -eq (Hash-File (Checked-File $reference $name))) ('Offline manifest differs from source: ' + $name)
    }
    $provenance = @(Read-Json (Join-Path $Bundle 'provenance.json'))
    Require ($provenance.Count -eq 1 -and $provenance[0].package -eq 'com.heytap.headset' -and $provenance[0].PSObject.Properties.Name -notcontains 'source') 'Invalid HeyMelody provenance.'
    $entries = @(Read-Json (Join-Path $Bundle 'artwork.json'))
    $supplemental = @(Read-Json (Join-Path $Bundle 'supplemental.json'))
    Require ($entries.Count -gt 0 -and $supplemental.Count -gt 0) 'Offline resource manifests are empty.'
    $reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $supplemental) { Require ($entry.package -eq 'com.heytap.headset') 'Invalid supplemental asset provenance.' }
    foreach ($entry in @($entries) + @($supplemental)) {
        Require ($entry.PSObject.Properties.Name -notcontains 'source') 'Artwork source selection is no longer supported.'
        $file = Checked-File $Bundle $entry.path
        Require ((Get-Item -LiteralPath $file).Length -eq $entry.length -and (Hash-File $file) -eq $entry.sha256) ('Offline artwork bytes do not match index: ' + $entry.path)
        [void]$reachable.Add($entry.path)
        if ($entry.originalPath) {
            Require ((Hash-File (Checked-File $Bundle $entry.originalPath)) -eq $entry.originalSha256) ('Offline original artwork hash mismatch: ' + $entry.originalPath)
            [void]$reachable.Add($entry.originalPath)
        }
        if ($entry.kind -eq 'lottie') {
            foreach ($asset in @((Read-Json $file).assets)) {
                $relative = [string]$asset.u + [string]$asset.p
                if (-not $relative -or $relative.StartsWith('data:')) { continue }
                Require ($relative -notmatch ':' -and $relative -notmatch '(^|[\\/])\.\.([\\/]|$)') 'Unsafe animation dependency.'
                $dependency = Checked-File (Split-Path -Parent $file) $relative
                $name = [IO.Path]::GetRelativePath($Bundle, $dependency).Replace('\', '/')
                Require ((Hash-File $dependency) -eq (Hash-File (Checked-File $reference $name))) ('Animation dependency differs from source: ' + $name)
                [void]$reachable.Add($name)
            }
        }
    }
    $actual = @(Get-ChildItem -LiteralPath (Join-Path $Bundle 'data') -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($Bundle, $_.FullName).Replace('\', '/') })
    Require (@(Compare-Object @($reachable) $actual).Count -eq 0) 'Offline artwork contains missing or unreferenced files.'
    [ordered]@{ indexSha256 = (Hash-File (Join-Path $Bundle 'artwork.json')); artworkEntries = $entries.Count; catalogCount = 1; dataFiles = $reachable.Count; allIndexedBytesVerified = $true }
}
function Check-Notice([string]$Directory, [object]$Notice) {
    Require (@($Notice.packages).Count -gt 0) ('Package notices are empty: ' + $Directory)
    $packageNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Notice.packages)) {
        Require ($packageNames.Add($entry.package + '/' + $entry.version)) ('Duplicate package notice: ' + $entry.package + '/' + $entry.version)
        $metadata = Checked-File $Directory $entry.metadata
        Add-CriticalFile $metadata
        [xml]$metadataXml = Get-Content -LiteralPath $metadata -Raw
        $packageMetadata = $metadataXml.SelectSingleNode('//*[local-name()="metadata"]')
        Require ($null -ne $packageMetadata) ('Invalid NuGet notice metadata: ' + $metadata)
        Require ($packageMetadata.SelectSingleNode('*[local-name()="id"]').InnerText -eq $entry.package) ('Package notice ID mismatch: ' + $metadata)
        Require ($packageMetadata.SelectSingleNode('*[local-name()="version"]').InnerText -eq $entry.version) ('Package notice version mismatch: ' + $metadata)
        foreach ($file in @($entry.files)) {
            $path = Checked-File $Directory $file.path
            Require ((Hash-File $path) -eq $file.sha256) ('Invalid package notice: ' + $path)
            Add-CriticalFile $path
        }
        $metadataLicense = $packageMetadata.SelectSingleNode('*[local-name()="license"]')
        if ($metadataLicense -and $metadataLicense.GetAttribute('type') -eq 'file') {
            Require ($entry.licenseType -eq 'file' -and $entry.license -eq $metadataLicense.InnerText) ('Package license metadata mismatch: ' + $metadata)
            $licenseRelative = ('licenses/' + $entry.package + '/' + $entry.version + '/' + $metadataLicense.InnerText).Replace('\', '/')
            Require (@($entry.files | Where-Object { $_.path.Replace('\', '/') -eq $licenseRelative }).Count -eq 1) ('Declared package license file is absent: ' + $licenseRelative)
        }
    }
    Require (@($Notice.supplementalNotices).Count -gt 0) ('Supplemental notices are empty: ' + $Directory)
    foreach ($file in @($Notice.supplementalNotices)) {
        $path = Checked-File $Directory $file.path
        Require ((Hash-File $path) -eq $file.sha256) ('Invalid supplemental notice: ' + $path)
        Add-CriticalFile $path
    }
}

Require (Test-Path -LiteralPath $taskStage -PathType Container) ('The release staging directory has not been built yet: ' + $taskStage)
Require (Test-Path -LiteralPath $taskArchive -PathType Leaf) ('The release ZIP has not been built yet: ' + $taskArchive)
$manifest = Read-Json (Join-Path $taskStage 'manifest.json')
Require ($manifest.schemaVersion -eq 2 -and $manifest.version -eq $Version) 'The release manifest has the wrong schema/version.'
Require (@($manifest.builds).Count -eq $targetArchitectures.Count -and @(Compare-Object @($manifest.builds.architecture | Sort-Object) @($targetArchitectures | Sort-Object)).Count -eq 0) 'The release manifest does not match the requested architectures.'
Require (-not $manifest.nativeArm64ExecutionVerified) 'A static receipt cannot assert ARM64 hardware execution.'
$expectedMachines = @{ x86 = 0x14C; x64 = 0x8664; arm64 = 0xAA64 }
$criticalPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
[void]$criticalPaths.Add('manifest.json')
$buildChecks = @()
$artworkChecks = @()
$resourceChecks = @()
$resourceRelease = [version]$Version -ge [version]'0.8.0'
$sourceArtwork = @()

if ($VerifyDeviceArtwork) {
    if (-not ('HeyMelody.ReleaseVerification.DeviceArtwork' -as [type])) {
        Add-Type -TypeDefinition @'
using System;

namespace HeyMelody.ReleaseVerification
{
    public static class DeviceArtwork
    {
        public static int Find(byte[] assembly, byte[] artwork)
        {
            return assembly.AsSpan().IndexOf(artwork.AsSpan());
        }
    }
}
'@
    }
    foreach ($assetName in @('x4-pods-with-earphones.png', 'x4-both-earphones.png')) {
        $sourceRelative = 'src/HeyMelody.App/Assets/Devices/EncoX4/' + $assetName
        $sourcePath = Checked-File $taskRoot $sourceRelative
        $sourceArtwork += [ordered]@{
            path = $sourceRelative
            bytes = [IO.File]::ReadAllBytes($sourcePath)
            sha256 = (Hash-File $sourcePath)
        }
    }
    if ([version]$Version -ge [version]'0.8.2') {
        $sourceRelative = 'src/HeyMelody.App/Assets/Devices/EncoFree4Dynaudio/model.glb'
        $sourcePath = Checked-File $taskRoot $sourceRelative
        $sourceHash = Hash-File $sourcePath
        Require ($sourceHash -eq '7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6') 'The demo model differs from the independently verified Dynaudio GLB.'
        $sourceArtwork += [ordered]@{
            path = $sourceRelative
            bytes = [IO.File]::ReadAllBytes($sourcePath)
            sha256 = $sourceHash
        }
    }
}

foreach ($architecture in $targetArchitectures) {
    $appDirectory = Join-Path $taskStage $architecture
    $hostDirectory = Join-Path $appDirectory 'backend'
    $build = @($manifest.builds | Where-Object { $_.architecture -eq $architecture })
    Require ($build.Count -eq 1 -and $build[0].rid -eq ('win-' + $architecture) -and $build[0].selfContained) ('Invalid architecture manifest: ' + $architecture)
    $build = $build[0]
    Require (-not $build.hardwareExecutionVerified) ('Unexpected hardware execution claim: ' + $architecture)
    Require ($build.gui -eq ($architecture + '/HeyMelody.exe') -and $build.deviceManager -eq ($architecture + '/HeyMelody.DeviceManager.exe') -and $build.host -eq ($architecture + '/backend/HeyMelody.Host.exe')) ('Unexpected entrypoint manifest paths: ' + $architecture)
    Require ($build.guiFramework -eq 'net10.0' -and $build.deviceManagerFramework -eq 'net10.0' -and $build.hostFramework -eq 'net10.0-windows10.0.19041.0') ('Unexpected target frameworks: ' + $architecture)
    $executables = @(
        @{ role = 'gui'; relativePath = $build.gui; expectedHash = $build.guiSha256; subsystem = 2; assembly = 'HeyMelody' },
        @{ role = 'deviceManager'; relativePath = $build.deviceManager; expectedHash = $build.deviceManagerSha256; subsystem = 2; assembly = 'HeyMelody.DeviceManager' },
        @{ role = 'host'; relativePath = $build.host; expectedHash = $build.hostSha256; subsystem = 2; assembly = 'HeyMelody.Host' }
    )
    if ($resourceRelease) {
        Require ($build.fitTest -eq ($architecture + '/HeyMelody.FitTest.exe') -and $build.hearingSetup -eq ($architecture + '/HeyMelody.HearingSetup.exe')) ('Unexpected calibration entrypoint paths: ' + $architecture)
        Require ($build.fitTestFramework -eq 'net10.0' -and $build.hearingSetupFramework -eq 'net10.0') ('Unexpected calibration frameworks: ' + $architecture)
        $executables += @(
            @{ role = 'fitTest'; relativePath = $build.fitTest; expectedHash = $build.fitTestSha256; subsystem = 2; assembly = 'HeyMelody.FitTest' },
            @{ role = 'hearingSetup'; relativePath = $build.hearingSetup; expectedHash = $build.hearingSetupSha256; subsystem = 2; assembly = 'HeyMelody.HearingSetup' }
        )
    }
    $executableChecks = @()
    $managedChecks = @()
    $runtimeVersions = @()
    foreach ($executable in $executables) {
        $path = Checked-File $taskStage $executable.relativePath
        $pe = Read-Pe $path
        $hash = Hash-File $path
        Require ($pe.machineValue -eq $expectedMachines[$architecture] -and -not $pe.managed) ('Wrong executable architecture: ' + $path)
        Require ($pe.subsystem -eq $executable.subsystem) ('Wrong executable subsystem: ' + $path)
        Require ($hash -eq $executable.expectedHash) ('Executable does not match manifest: ' + $path)
        [void]$criticalPaths.Add($executable.relativePath)
        $executableChecks += [ordered]@{ role = $executable.role; path = $executable.relativePath; machine = $pe.machine; subsystem = $pe.subsystem; sha256 = $hash }

        $assemblyPath = [IO.Path]::ChangeExtension($path, '.dll')
        $assemblyRelative = [IO.Path]::GetRelativePath($taskStage, $assemblyPath).Replace('\', '/')
        $assemblyVersion = Assembly-Version $assemblyPath
        Require ($assemblyVersion -eq ($Version + '.0')) ('Wrong managed assembly version: ' + $assemblyPath + ' = ' + $assemblyVersion)
        [void]$criticalPaths.Add($assemblyRelative)
        $managedChecks += [ordered]@{ role = $executable.role; path = $assemblyRelative; assemblyVersion = $assemblyVersion; sha256 = (Hash-File $assemblyPath) }

        $runtimePath = [IO.Path]::ChangeExtension($path, '.runtimeconfig.json')
        $runtime = Read-Json $runtimePath
        $frameworks = @($runtime.runtimeOptions.includedFrameworks)
        Require ($frameworks.Count -eq 1 -and $frameworks[0].name -eq 'Microsoft.NETCore.App') ('Unexpected runtime dependency: ' + $runtimePath)
        $runtimeVersions += $frameworks[0].version
        [void]$criticalPaths.Add([IO.Path]::GetRelativePath($taskStage, $runtimePath).Replace('\', '/'))
        [void]$criticalPaths.Add([IO.Path]::GetRelativePath($taskStage, [IO.Path]::ChangeExtension($path, '.deps.json')).Replace('\', '/'))
    }
    if ($VerifyDeviceArtwork) {
        $guiAssemblyPath = Checked-File $appDirectory 'HeyMelody.dll'
        $guiAssemblyRelative = [IO.Path]::GetRelativePath($taskStage, $guiAssemblyPath).Replace('\', '/')
        $guiAssemblyBytes = [IO.File]::ReadAllBytes($guiAssemblyPath)
        $guiAssemblyHash = Hash-File $guiAssemblyPath
        foreach ($asset in $sourceArtwork) {
            $offset = [HeyMelody.ReleaseVerification.DeviceArtwork]::Find($guiAssemblyBytes, $asset.bytes)
            Require ($offset -ge 0) ('GUI assembly is missing the complete source artwork bytes: ' + $guiAssemblyRelative + ' / ' + $asset.path)
            $artworkChecks += [ordered]@{
                architecture = $architecture; sourcePath = $asset.path; sourceSha256 = $asset.sha256
                sourceSize = $asset.bytes.Length; guiAssembly = $guiAssemblyRelative; guiSha256 = $guiAssemblyHash
                embeddedOffset = $offset; completeSourceBytesVerified = $true
            }
        }
    }
    Require (@($runtimeVersions | Sort-Object -Unique).Count -eq 1) ('GUI/manager/Host runtime versions differ: ' + $architecture)

    $guiDeps = Read-Json (Join-Path $appDirectory 'HeyMelody.deps.json')
    if ($manifest.diagnosticLogging -eq 'one-host-session-debug-jsonl-v1') {
        $guiDiagnostics = Checked-File $appDirectory 'HeyMelody.Diagnostics.dll'
        $hostDiagnostics = Checked-File $hostDirectory 'HeyMelody.Diagnostics.dll'
        Add-CriticalFile $guiDiagnostics
        Add-CriticalFile $hostDiagnostics
        Require ((Hash-File $guiDiagnostics) -eq $build.guiDiagnosticsSha256) ('GUI diagnostics library does not match manifest: ' + $architecture)
        Require ((Hash-File $hostDiagnostics) -eq $build.hostDiagnosticsSha256) ('Host diagnostics library does not match manifest: ' + $architecture)
        foreach ($entry in @('HeyMelody', 'HeyMelody.DeviceManager', 'HeyMelody.FitTest', 'HeyMelody.HearingSetup', 'backend/HeyMelody.Host')) {
            $entryDeps = Read-Json (Join-Path $appDirectory ($entry + '.deps.json'))
            Require (@($entryDeps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'HeyMelody.Diagnostics/*' }).Count -eq 1) ('Diagnostic dependency missing: ' + $architecture + '/' + $entry)
        }
    }
    $managerDeps = Read-Json (Join-Path $appDirectory 'HeyMelody.DeviceManager.deps.json')
    Require ($guiDeps.runtimeTarget.name -eq $managerDeps.runtimeTarget.name) ('GUI/manager runtime target differs: ' + $architecture)
    $notice = Read-Json (Join-Path $appDirectory 'PACKAGE-NOTICES.json')
    Require ($notice.schemaVersion -eq 1) ('Unexpected notice schema: ' + $architecture)
    Check-Notice $appDirectory $notice
    $hostNotice = Read-Json (Join-Path $hostDirectory 'PACKAGE-NOTICES.json')
    Require ($hostNotice.schemaVersion -eq 1) ('Unexpected backend notice schema: ' + $architecture)
    Check-Notice $hostDirectory $hostNotice
    if ($resourceRelease) {
        Require (@($notice.packages | Where-Object { $_.package -eq 'SkiaSharp.Skottie' }).Count -eq 1) ('Lottie dependency notice missing: ' + $architecture)
        # Builds that declare it ship one ResourceBundles beside the GUI; the backend reads that copy. Older builds carry two.
        $sharedBundles = $build.PSObject.Properties.Name -contains 'hostResourceBundles' -and $build.hostResourceBundles -eq 'gui'
        if ($sharedBundles) { Require (-not (Test-Path -LiteralPath (Join-Path $hostDirectory 'ResourceBundles'))) ('Backend carries a second ResourceBundles: ' + $architecture) }
        foreach ($directory in @($appDirectory, $hostDirectory)) {
            Add-CriticalFile (Checked-File $directory 'HeyMelody.Resources.dll')
            Add-CriticalFile (Checked-File $directory 'licenses/upstream/Extracted-Earphone-Resources-NOTICE.txt')
            if ($sharedBundles -and $directory -eq $hostDirectory) { continue }
            $bundle = Join-Path $directory 'ResourceBundles'
            $indexPath = Checked-File $bundle 'artwork.json'
            $provenancePath = Checked-File $bundle 'provenance.json'
            $bundleCheck = Check-ResourceBundle $bundle
            if ($directory -eq $appDirectory) { Require ((Hash-File $indexPath) -eq $build.resourceIndexSha256) ('Resource index does not match manifest: ' + $architecture) }
            foreach ($file in [IO.Directory]::EnumerateFiles($bundle, '*', [IO.SearchOption]::AllDirectories)) { Add-CriticalFile $file }
            $resourceChecks += [ordered]@{ architecture = $architecture; directory = [IO.Path]::GetRelativePath($taskStage, $bundle).Replace('\', '/'); catalogCount = 1; dataFiles = $bundleCheck.dataFiles; artworkEntries = $bundleCheck.artworkEntries; indexSha256 = (Hash-File $indexPath); allIndexedBytesVerified = $true }
        }
        foreach ($component in @('HeyMelody.FitTest', 'HeyMelody.HearingSetup')) {
            $deps = Read-Json (Checked-File $appDirectory ($component + '.deps.json'))
            Require ($deps.runtimeTarget.name -eq $guiDeps.runtimeTarget.name) ('Calibration runtime target differs: ' + $component)
            foreach ($library in $deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
                Require ($null -ne $guiDeps.libraries.PSObject.Properties[$library.Name]) ('Calibration dependency is not shared by the GUI: ' + $library.Name)
                if ($library.Name -notlike 'Microsoft.NETCore.App.Runtime.*') {
                    $parts = $library.Name.Split('/')
                    Require (@($notice.packages | Where-Object { $_.package -eq $parts[0] -and $_.version -eq $parts[1] }).Count -eq 1) ('Calibration package notice missing: ' + $library.Name)
                }
            }
        }
    }
    Add-CriticalFile (Checked-File $hostDirectory 'PACKAGE-NOTICES.json')
    $hostDeps = Read-Json (Join-Path $hostDirectory 'HeyMelody.Host.deps.json')
    foreach ($library in $hostDeps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name -notlike 'Microsoft.NETCore.App.Runtime.*' }) {
        $parts = $library.Name.Split('/')
        Require (@($hostNotice.packages | Where-Object { $_.package -eq $parts[0] -and $_.version -eq $parts[1] }).Count -eq 1) ('Backend package notice missing: ' + $library.Name)
    }
    [void]$criticalPaths.Add($architecture + '/PACKAGE-NOTICES.json')
    $managerPackages = @()
    foreach ($library in $managerDeps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        Require ($null -ne $guiDeps.libraries.PSObject.Properties[$library.Name]) ('Manager dependency does not match shared GUI dependencies: ' + $library.Name)
        $sharedLibrary = $guiDeps.libraries.PSObject.Properties[$library.Name].Value
        Require ($library.Value.sha512 -eq $sharedLibrary.sha512 -and $library.Value.path -eq $sharedLibrary.path) ('Manager/GUI package identity differs: ' + $library.Name)
        $managerPackages += $library.Name
        if ($library.Name -notlike 'Microsoft.NETCore.App.Runtime.*') {
            $parts = $library.Name.Split('/')
            Require (@($notice.packages | Where-Object { $_.package -eq $parts[0] -and $_.version -eq $parts[1] }).Count -eq 1) ('Manager package notice missing: ' + $library.Name)
        }
    }
    foreach ($library in $guiDeps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name -notlike 'Microsoft.NETCore.App.Runtime.*' }) {
        $parts = $library.Name.Split('/')
        Require (@($notice.packages | Where-Object { $_.package -eq $parts[0] -and $_.version -eq $parts[1] }).Count -eq 1) ('GUI package notice missing: ' + $library.Name)
    }
    foreach ($desktopAssembly in @('PresentationCore.dll', 'PresentationFramework.dll', 'System.Windows.Forms.dll', 'wpfgfx_cor3.dll')) {
        Require (-not (Test-Path -LiteralPath (Join-Path $appDirectory $desktopAssembly))) ('Unexpected WindowsDesktop assembly: ' + $desktopAssembly)
        Require (-not (Test-Path -LiteralPath (Join-Path $hostDirectory $desktopAssembly))) ('Unexpected backend WindowsDesktop assembly: ' + $desktopAssembly)
    }
    foreach ($requiredPackage in @('Avalonia', 'FluentAvaloniaUI', 'SkiaSharp.NativeAssets.Win32')) {
        Require (@($notice.packages | Where-Object { $_.package -eq $requiredPackage }).Count -eq 1) ('Required GUI package missing: ' + $requiredPackage)
        Require (@($hostNotice.packages | Where-Object { $_.package -eq $requiredPackage }).Count -eq 1) ('Required backend package missing: ' + $requiredPackage)
    }
    foreach ($directory in @($appDirectory, $hostDirectory)) {
        foreach ($licenseName in @('LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')) {
            $licensePath = Join-Path $directory $licenseName
            Require ((Test-Path -LiteralPath $licensePath -PathType Leaf) -and (Get-Item -LiteralPath $licensePath).Length -gt 0) ('Runtime license missing: ' + $licensePath)
            [void]$criticalPaths.Add([IO.Path]::GetRelativePath($taskStage, $licensePath).Replace('\', '/'))
        }
    }

    $nativeChecks = @()
    foreach ($path in [IO.Directory]::EnumerateFiles($appDirectory, '*.dll', [IO.SearchOption]::AllDirectories)) {
        $pe = Read-Pe $path
        if ($pe.managed) { continue }
        Require ($pe.machineValue -eq $expectedMachines[$architecture]) ('Wrong native DLL architecture: ' + $path)
        $relative = [IO.Path]::GetRelativePath($taskStage, $path).Replace('\', '/')
        [void]$criticalPaths.Add($relative)
        $nativeChecks += [ordered]@{ path = $relative; machine = $pe.machine; sha256 = (Hash-File $path) }
    }
    foreach ($relative in @(($architecture + '/coreclr.dll'), ($architecture + '/libSkiaSharp.dll'), ($architecture + '/libHarfBuzzSharp.dll'), ($architecture + '/av_libglesv2.dll'), ($architecture + '/backend/coreclr.dll'), ($architecture + '/backend/libSkiaSharp.dll'), ($architecture + '/backend/libHarfBuzzSharp.dll'), ($architecture + '/backend/av_libglesv2.dll'))) {
        Require (@($nativeChecks | Where-Object { $_.path -eq $relative }).Count -eq 1) ('Required native runtime/graphics DLL missing: ' + $relative)
    }
    foreach ($packageFile in @('PACKAGE-NOTICES.json', 'HeyMelody.runtimeconfig.json', 'HeyMelody.DeviceManager.runtimeconfig.json', 'HeyMelody.deps.json', 'HeyMelody.DeviceManager.deps.json')) {
        [void](Checked-File $appDirectory $packageFile)
    }
    $modelRenderer = $null
    if ($manifest.modelRenderer -eq 'Filament' -or (Test-Path -LiteralPath (Join-Path $appDirectory 'HeyMelody.Rendering.Filament.dll'))) {
        Require ($manifest.modelRenderer -eq 'Filament') 'Filament renderer declaration is absent from the release manifest.'
        Add-CriticalFile (Checked-File $appDirectory 'HeyMelody.Rendering.Filament.dll')
        $modelRenderer = & (Join-Path $PSScriptRoot 'Verify-FilamentRuntime.ps1') -Directory $appDirectory -RuntimeIdentifier ('win-' + $architecture) -RequireCurrent:($manifest.diagnosticLogging -eq 'one-host-session-debug-jsonl-v1')
        Require ($build[0].modelRenderer.success -and $build[0].modelRenderer.sha256 -eq $modelRenderer.sha256 -and $build[0].modelRenderer.manifestSha256 -eq $modelRenderer.manifestSha256) 'Filament runtime does not match the release manifest.'
        Require ($build[0].modelRenderer.abiVersion -eq $modelRenderer.abiVersion -and $build[0].modelRenderer.filamentCommit -eq $modelRenderer.filamentCommit -and $build[0].modelRenderer.library -eq $modelRenderer.library) 'Filament release metadata does not match its native runtime.'
        if ($modelRenderer.abiVersion -eq 2) {
            $declaredRenderer = $build[0].modelRenderer
            Require ((@($declaredRenderer.supportedBackends) -join ',') -ceq (@($modelRenderer.supportedBackends) -join ',') -and $declaredRenderer.defaultBackend -ceq $modelRenderer.defaultBackend) 'Filament release backend capabilities do not match its runtime.'
            Require ($declaredRenderer.probe -ceq $modelRenderer.probe -and $declaredRenderer.probeSha256 -ceq $modelRenderer.probeSha256) 'Filament release probe does not match its runtime.'
            Require ($declaredRenderer.materials.path -ceq $modelRenderer.materials.path -and $declaredRenderer.materials.sha256 -ceq $modelRenderer.materials.sha256 -and (@($declaredRenderer.materials.supportedBackends) -join ',') -ceq (@($modelRenderer.materials.supportedBackends) -join ',')) 'Filament release material archive does not match its runtime.'
        }
        Add-CriticalFile (Checked-File $appDirectory 'filament-runtime.json')
        $nativeManifest = Read-Json (Join-Path $appDirectory 'filament-runtime.json')
        foreach ($nativeFile in $nativeManifest.files) { Add-CriticalFile (Checked-File $appDirectory $nativeFile.path) }
        if ($modelRenderer.abiVersion -eq 2) {
            $nestedNative = Join-Path $appDirectory ('runtimes/win-' + $architecture + '/native')
            if ((Test-Path -LiteralPath (Join-Path $nestedNative 'HeyMelody.Filament.Native.dll')) -or (Test-Path -LiteralPath (Join-Path $nestedNative 'filament-runtime.json')) -or (Test-Path -LiteralPath (Join-Path $nestedNative 'hm_filament_probe.exe'))) {
                $nestedVerification = & (Join-Path $PSScriptRoot 'Verify-FilamentRuntime.ps1') -Directory $nestedNative -RuntimeIdentifier ('win-' + $architecture) -RequireCurrent
                Require ($nestedVerification.manifestSha256 -ceq $modelRenderer.manifestSha256) 'Nested Filament runtime does not match the flat runtime.'
                Add-CriticalFile (Checked-File $nestedNative 'filament-runtime.json')
                foreach ($nativeFile in $nativeManifest.files) { Add-CriticalFile (Checked-File $nestedNative $nativeFile.path) }
            }
        }
        Require (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'Ava3D.dll'))) 'Ava3D must not be distributed with the Filament experiment.'
    }
    $buildChecks += [ordered]@{
        architecture = $architecture; runtimeVersion = $runtimeVersions[0]
        executables = $executableChecks; managedAssemblies = $managedChecks
        managerSharedPackages = @($managerPackages | Sort-Object); nativeLibraries = $nativeChecks
        noticesVerified = $true; runtimeLicensesVerified = $true; hardwareExecutionVerified = $false
        modelRenderer = $modelRenderer
    }
    Write-Output ('PASS ' + $architecture + ': executable/managed/native architecture, versions, shared runtime/dependencies and licenses')
}
if ($VerifyDeviceArtwork) {
    Require ($artworkChecks.Count -eq ($sourceArtwork.Count * $targetArchitectures.Count)) 'Expected all source artwork bytes in every published GUI architecture.'
    Write-Output ('PASS device artwork: ' + $artworkChecks.Count + ' complete source artwork matches in published GUI assemblies')
}

foreach ($rootFile in @('README.md', 'README-使用说明.md', 'SOURCE-NOTES.md', 'Start-HeyMelody.cmd', 'Start-HeyMelody.ps1')) {
    Add-CriticalFile (Checked-File $taskStage $rootFile)
}
if ([version]$Version -ge [version]'0.8.2') {
    Add-CriticalFile (Checked-File $taskStage 'Start-3D-Demo.cmd')
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($taskArchive)
$archivePathChecks = 0
$archiveHashChecks = 0
try {
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
    foreach ($entry in $zip.Entries) {
        Require (-not $entries.ContainsKey($entry.FullName)) ('Duplicate zip entry: ' + $entry.FullName)
        $entries.Add($entry.FullName, $entry)
    }
    $stageFiles = @([IO.Directory]::EnumerateFiles($taskStage, '*', [IO.SearchOption]::AllDirectories))
    Require (@($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') }).Count -eq $stageFiles.Count) 'Archive/staging file counts differ.'
    foreach ($path in $stageFiles) {
        $relative = [IO.Path]::GetRelativePath($taskStage, $path).Replace('\', '/')
        Require ($entries.ContainsKey($relative)) ('Zip is missing file: ' + $relative)
        Require ($entries[$relative].Length -eq (Get-Item -LiteralPath $path).Length) ('Zip file size mismatch: ' + $relative)
        $archivePathChecks++
        if ($criticalPaths.Contains($relative)) {
            $stream = $entries[$relative].Open()
            $hashProvider = $null
            try {
                $hashProvider = [Security.Cryptography.SHA256]::Create()
                $archiveHash = [BitConverter]::ToString($hashProvider.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
            }
            finally {
                if ($hashProvider) { $hashProvider.Dispose() }
                $stream.Dispose()
            }
            Require ($archiveHash -eq (Hash-File $path)) ('Zip hash mismatch: ' + $relative)
            $archiveHashChecks++
        }
    }
    Require ($archiveHashChecks -eq $criticalPaths.Count) 'Some critical files were not verified in the archive.'
}
finally { $zip.Dispose() }

$receipt = [ordered]@{
    schemaVersion = 1; version = $Version; success = $true; observedAtUtc = [DateTime]::UtcNow.ToString('o')
    scope = 'static/build-only'; hardwareExecutionVerified = $false; nativeArm64ExecutionVerified = $false
    buildDirectory = [IO.Path]::GetRelativePath($taskRoot, $taskStage).Replace('\', '/')
    manifestSha256 = (Hash-File (Join-Path $taskStage 'manifest.json')); builds = $buildChecks
    deviceArtwork = [ordered]@{
        requested = [bool]$VerifyDeviceArtwork; source = 'checked-in application assets'
        completeSourceMatchesVerified = $artworkChecks.Count; checks = $artworkChecks
    }
    resourceBundles = @($resourceChecks)
    archive = [ordered]@{
        path = [IO.Path]::GetRelativePath($taskRoot, $taskArchive).Replace('\', '/'); sha256 = (Hash-File $taskArchive)
        allFilePathsAndSizesVerified = $archivePathChecks; criticalContentHashesVerified = $archiveHashChecks
        verificationScope = 'all file paths/sizes plus hashes of manifest, apphosts, managed entry assemblies, dependency/runtime configs, native DLLs, package notices/metadata/license files, runtime notices and root package instructions/launchers'
    }
    limitations = @('Static checks do not execute released programs or validate Bluetooth hardware.',
        'Managed assembly hashes supplement apphost hashes; apphost hashes alone do not bind application behavior.',
        'No exact-release hardware execution receipt is claimed.')
}
$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $taskReceipt -Encoding UTF8
Write-Output ('PASS archive: ' + $archivePathChecks + ' file paths/sizes and ' + $archiveHashChecks + ' critical hashes')
Write-Output ('ZIP_SHA256 ' + $receipt.archive.sha256)
Write-Output ('STATIC_RECEIPT ' + $taskReceipt)
