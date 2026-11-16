#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [ValidateNotNullOrEmpty()][ValidateSet('x64', 'arm64')][string[]]$Architectures = @('x64'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'release/installers'),
    [string]$InnoCompilerPath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Build this Windows installer on Windows with PowerShell 7.4 or later.' }
$repositoryRoot = $PSScriptRoot
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($OutputRoot.Equals($PackageRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputRoot.StartsWith($PackageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the input client package.'
}
$architecturesToBuild = @($Architectures | Select-Object -Unique)

function Read-SmallJson([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 1MB) { throw "Unexpectedly large metadata: $Path" }
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json
}

function ConvertTo-InnoString([string]$Value) {
    if ($Value.Contains("`n") -or $Value.Contains("`r") -or $Value.Contains('"')) {
        throw 'Inno Setup build paths cannot contain quotes or line breaks.'
    }
    return '"' + $Value + '"'
}

function Assert-NoReparsePoints([string]$Directory) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    while ($pending.Count) {
        $entry = Get-Item -LiteralPath $pending.Pop() -Force
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package links are not supported: $($entry.FullName)" }
        if ($entry.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) { $pending.Push($child.FullName) }
        }
    }
}

$manifestPath = Join-Path $PackageRoot 'manifest.json'
$manifest = Read-SmallJson $manifestPath
$compilationMode = if ($manifest.compilationMode) { [string]$manifest.compilationMode } else { 'ReadyToRun' }
if ($compilationMode -notin @('ReadyToRun', 'AotExperimental')) { throw "Unknown compilation mode: $compilationMode" }
if ($compilationMode -eq 'AotExperimental' -and @($architecturesToBuild | Where-Object { $_ -ne 'x64' }).Count) {
    throw 'AotExperimental installers support only x64.'
}
$runtimeConfigName = if ($compilationMode -eq 'AotExperimental') { 'HeyMelody.DeviceManager.runtimeconfig.json' } else { 'HeyMelody.runtimeconfig.json' }
$modeSuffix = if ($compilationMode -eq 'AotExperimental') { '_AOT' } else { '' }
if (-not $InnoCompilerPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $InnoCompilerPath = $compiler.Source }
    else {
        foreach ($programFilesRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
            if (-not $programFilesRoot) { continue }
            foreach ($directory in @('Inno Setup 6', 'Inno Setup 7')) {
                $candidate = Join-Path $programFilesRoot "$directory/ISCC.exe"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $InnoCompilerPath = $candidate; break }
            }
            if ($InnoCompilerPath) { break }
        }
    }
}
if (-not $InnoCompilerPath) { throw 'Install Inno Setup 6.6 or later (https://jrsoftware.org/isinfo.php), or pass -InnoCompilerPath.' }
$InnoCompilerPath = (Resolve-Path -LiteralPath $InnoCompilerPath).Path
$version = [string]$manifest.version
if ($manifest.schemaVersion -ne 2 -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a Build-Client schema-2 package manifest and three-part version.' }
$numericVersion = [version]$version
if ($numericVersion.Major -gt 65535 -or $numericVersion.Minor -gt 65535 -or $numericVersion.Build -gt 65535) { throw 'Version components exceed Windows file-version limits.' }
foreach ($architecture in $architecturesToBuild) {
    $build = @($manifest.builds | Where-Object architecture -eq $architecture)
    if ($build.Count -ne 1 -or -not $build[0].selfContained -or $build[0].rid -ne "win-$architecture") { throw "Package does not contain a self-contained win-$architecture build." }
    $components = [ordered]@{
        gui = "$architecture/HeyMelody.exe"; host = "$architecture/backend/HeyMelody.Host.exe"
        deviceManager = "$architecture/HeyMelody.DeviceManager.exe"; fitTest = "$architecture/HeyMelody.FitTest.exe"
        hearingSetup = "$architecture/HeyMelody.HearingSetup.exe"
    }
    foreach ($component in $components.Keys) {
        $expected = if ($compilationMode -eq 'AotExperimental' -and $component -in @('gui', 'host')) { 'native-aot' } else { 'ready-to-run' }
        $actual = $build[0].componentCompilation.$component
        if (($compilationMode -eq 'AotExperimental' -or $build[0].componentCompilation) -and $actual -ne $expected) {
            throw "Unexpected $component compilation mode for $architecture : expected $expected."
        }
        $executable = Join-Path $PackageRoot $components[$component]
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing package file: $($components[$component])" }
        $expectedSha256 = $build[0].("${component}Sha256")
        if ($expectedSha256 -and (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ine $expectedSha256) {
            throw "Package executable hash mismatch: $($components[$component])"
        }
    }
    foreach ($relative in @("$architecture/$runtimeConfigName", "$architecture/coreclr.dll", 'SOURCE-NOTES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $relative) -PathType Leaf)) { throw "Missing package file: $relative" }
    }
    if ($compilationMode -eq 'AotExperimental') {
        foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'HeyMelody.Host.dll', 'HeyMelody.Host.deps.json', 'HeyMelody.Host.runtimeconfig.json')) {
            if (Test-Path -LiteralPath (Join-Path $PackageRoot "$architecture/backend/$name")) { throw "AOT backend contains a managed runtime file: $name" }
        }
    }
    $installerPath = Join-Path $OutputRoot "HeyMelody_Windows_${version}_${architecture}${modeSuffix}_Setup.exe"
    if ((Test-Path -LiteralPath $installerPath) -or (Test-Path -LiteralPath "$installerPath.json") -or (Test-Path -LiteralPath "$installerPath.payload.json")) { throw "Installer or receipt already exists; select a new output directory: $installerPath" }
}
if (@(Get-ChildItem -LiteralPath $PackageRoot -File | Where-Object Name -Match '^(Uninstall\.exe|unins\d+\.(exe|dat|msg))$').Count) {
    throw 'Use a Build-Client package, not an installed application directory.'
}
Assert-NoReparsePoints $PackageRoot
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
# Keep native compiler input paths below MAX_PATH even in a long worktree path.
$workRoot = Join-Path $repositoryRoot ('artifacts/ib-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $workRoot | Out-Null
$packageCache = Join-Path $repositoryRoot 'artifacts/nuget-cache'
$restoreArguments = @("-p:RestorePackagesPath=$packageCache")
$fallbackCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
if (Test-Path -LiteralPath $fallbackCache -PathType Container) { $restoreArguments += "-p:RestoreFallbackFolders=$fallbackCache" }
$utf8Bom = [Text.UTF8Encoding]::new($true)

foreach ($architecture in $architecturesToBuild) {
    $build = @($manifest.builds | Where-Object architecture -eq $architecture)
    $work = Join-Path $workRoot $architecture
    $payload = Join-Path $work 'p'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageRoot $architecture) -Destination $payload -Recurse
    foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -File -Force) { Copy-Item -LiteralPath $file.FullName -Destination $payload }
    foreach ($name in @('README.md', 'README-使用说明.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/README.md') -Destination (Join-Path $payload $name) -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $payload 'installer-licenses') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/inno/LICENSE.txt') -Destination (Join-Path $payload 'installer-licenses/InnoSetup-LICENSE.txt')
    $payloadManifest = Read-SmallJson $manifestPath
    $payloadManifest.builds = @($payloadManifest.builds | Where-Object architecture -eq $architecture)
    $payloadManifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8
    $runtimeConfig = Read-SmallJson (Join-Path $payload "$architecture/$runtimeConfigName")
    $framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')
    if ($framework.Count -ne 1 -or $framework[0].version -notmatch '^10\.\d+\.\d+$') { throw 'The managed GUI components must include the .NET 10 runtime.' }
    $helperOutput = Join-Path $work 'helper'
    & dotnet publish (Join-Path $repositoryRoot 'tools/HeyMelody.InstallerSupport/HeyMelody.InstallerSupport.csproj') -c Release -r "win-$architecture" --self-contained true -o $helperOutput "-p:RuntimeFrameworkVersion=$($framework[0].version)" '-p:PublishSingleFile=true' '-p:PublishTrimmed=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=false' '-p:CopyDebugSymbolFilesFromPackages=false' @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw "Installer helper publish failed: $architecture" }
    $helperPath = Join-Path $helperOutput 'HeyMelody.InstallerSupport.exe'
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force | Sort-Object FullName)
    foreach ($file in $files) {
        if ($file.FullName.Length -ge 260) { throw "Inno Setup input exceeds MAX_PATH; build from a shorter checkout path: $($file.FullName)" }
    }
    $relativeFiles = @($files | ForEach-Object { [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('/', '\') })
    $fileListPath = Join-Path $work 'payload-files.txt'
    [IO.File]::WriteAllLines($fileListPath, $relativeFiles, $utf8Bom)
    $outputBaseName = "HeyMelody_Windows_${version}_${architecture}${modeSuffix}_Setup"
    $installerPath = Join-Path $OutputRoot "$outputBaseName.exe"
    $definitions = [ordered]@{
        AppVersion = $version; AppArch = $architecture; CompilationMode = $compilationMode; PayloadDir = $payload; HelperExe = $helperPath
        FilesManifest = $fileListPath; OutputDir = $OutputRoot; OutputBaseName = $outputBaseName
        AppIcon = Join-Path $repositoryRoot 'src/HeyMelody.App/Assets/HeyMelody.ico'
        LanguageFile = Join-Path $repositoryRoot 'packaging/inno/Languages/ChineseSimplified.isl'
    }
    $entryLines = [Collections.Generic.List[string]]::new()
    foreach ($pair in $definitions.GetEnumerator()) { $entryLines.Add('#define ' + $pair.Key + ' ' + (ConvertTo-InnoString ([string]$pair.Value))) }
    $entryLines.Add('#include ' + (ConvertTo-InnoString (Join-Path $repositoryRoot 'packaging/inno/HeyMelody.iss')))
    $entryPath = Join-Path $work 'Build.iss'
    [IO.File]::WriteAllLines($entryPath, $entryLines, $utf8Bom)
    $compilerLog = Join-Path $work 'inno-compile.log'
    & $InnoCompilerPath $entryPath *> $compilerLog
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        Get-Content -LiteralPath $compilerLog -Tail 12 | ForEach-Object { if ($_.Length -le 500) { Write-Host $_ } }
        throw "Inno Setup compilation failed: $architecture (work files retained at $work)"
    }
    $compilerVersion = 'Inno Setup (minimum 6.6 validated by preprocessor)'
    foreach ($line in [IO.File]::ReadLines($compilerLog)) {
        if ($line -match 'Compiler engine version:\s*(.+)$') { $compilerVersion = $Matches[1].Trim(); break }
    }
    $payloadInventoryPath = "$installerPath.payload.json"
    [ordered]@{
        schemaVersion = 1; compilationMode = $compilationMode
        files = @($files | ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/')
                bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadInventoryPath -Encoding utf8
    [ordered]@{
        schemaVersion = 2; installerFormat = 'inno-setup'; version = $version; architecture = $architecture
        compilationMode = $compilationMode; componentCompilation = $build[0].componentCompilation
        createdAtUtc = [DateTime]::UtcNow.ToString('o'); compiler = $compilerVersion
        compilerSha256 = (Get-FileHash -LiteralPath $InnoCompilerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        sdkVersion = (& dotnet --version)
        packageManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        payloadManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        payloadInventorySha256 = (Get-FileHash -LiteralPath $payloadInventoryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        installerSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        helperPath = [IO.Path]::GetRelativePath($repositoryRoot, $helperPath).Replace('\', '/')
        helperSha256 = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()
        helperRuntimeVersion = [string]$framework[0].version; helperRuntimeConfig = $runtimeConfigName
        scope = 'current-user'; defaultDirectory = '%LOCALAPPDATA%\Programs\HeyMelody'
        desktopPreference = '%LOCALAPPDATA%\HeyMelody\installer-preferences.json:desktopShortcut'
        modeSwitch = 'same application identity; uninstall previous payload before changing compilation mode'
        compatibilityHardwareId = 'ACPI\VEN_QCOM&DEV_043A'; payloadFiles = $relativeFiles
        validationScope = 'packaged only; no hardware, UI or installer execution implied'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$installerPath.json" -Encoding utf8
    Write-Host "Created $installerPath ($((Get-FileHash -LiteralPath $installerPath).Hash.ToLowerInvariant())); work files: $work"
}
