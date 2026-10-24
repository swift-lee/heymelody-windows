[CmdletBinding()]
param(
    [string]$FilamentSource = $env:HEYMELODY_FILAMENT_SOURCE,
    [ValidateSet('win-x64','win-arm64','osx-x64','osx-arm64','linux-x64','linux-arm64')]
    [string]$RuntimeIdentifier,
    [string]$OutputRoot,
    [string]$PythonExecutable,
    [string]$ExpectedToolchainIdentityFile,
    [switch]$FetchSource,
    [switch]$FetchBuildTools,
    [string]$Proxy,
    [ValidateRange(1,64)][int]$Parallel = 8,
    [switch]$SkipSmoke
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$environmentBefore = [Environment]::GetEnvironmentVariables()
$isWindowsHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$isMacHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)
$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if (-not $RuntimeIdentifier) {
    $runtimeOs = if ($isWindowsHost) { 'win' } elseif ($isMacHost) { 'osx' } else { 'linux' }
    $RuntimeIdentifier = $runtimeOs + '-' + $hostArchitecture
    if ($RuntimeIdentifier -notin @('win-x64','win-arm64','osx-x64','osx-arm64','linux-x64','linux-arm64')) { throw 'This host has no supported Filament build preset.' }
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'filament.lock.json') -Raw | ConvertFrom-Json
# Filament vendors paths beyond MAX_PATH; keep this scoped to our Git commands.
$gitPathArguments = @('-c', 'core.longpaths=true')
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts/filament' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$managedSource = -not $FilamentSource
if ($managedSource) {
    if (-not $FetchSource) { throw 'Provide -FilamentSource or -FetchSource. The local research directory is never an implicit build dependency.' }
    $FilamentSource = Join-Path $OutputRoot 'source'
    if (-not (Test-Path -LiteralPath (Join-Path $FilamentSource '.git'))) {
        New-Item -ItemType Directory -Path $FilamentSource -Force | Out-Null
        & git @gitPathArguments -C $FilamentSource init --quiet
        if ($LASTEXITCODE) { throw 'Could not initialize the source cache.' }
        & git @gitPathArguments -C $FilamentSource remote add origin $lock.repository
        if ($LASTEXITCODE) { throw 'Could not configure the source-cache remote.' }
        $networkArguments = if ($Proxy) { @('-c', ('http.proxy=' + $Proxy)) } else { @() }
        & git @gitPathArguments @networkArguments -C $FilamentSource fetch --depth 1 origin $lock.commit
        if ($LASTEXITCODE) { throw 'Could not fetch the pinned Filament commit.' }
        & git @gitPathArguments -C $FilamentSource checkout --detach FETCH_HEAD
        if ($LASTEXITCODE) { throw 'Could not check out the pinned Filament commit.' }
    }
}
$FilamentSource = (Resolve-Path -LiteralPath $FilamentSource).Path
$actual = & git @gitPathArguments -c "safe.directory=$($FilamentSource.Replace('\','/'))" -C $FilamentSource rev-parse HEAD
if ($LASTEXITCODE) { throw 'Could not resolve the Filament checkout commit.' }
if (([string]$actual).Trim() -ne $lock.commit) { throw "Filament checkout must be pinned to $($lock.commit)." }
$sourceBefore = & git @gitPathArguments -c "safe.directory=$($FilamentSource.Replace('\','/'))" -C $FilamentSource status --porcelain --untracked-files=normal
if ($LASTEXITCODE) { throw 'Could not verify the Filament source working tree.' }
if ($sourceBefore) {
    if ($managedSource) { throw 'The managed Filament source cache is not clean, possibly after an incomplete checkout. Use a fresh -OutputRoot; the cache was not modified.' }
    throw 'The provided Filament checkout must be clean; it is treated as a read-only input.'
}

if (($RuntimeIdentifier.StartsWith('win-') -and -not $isWindowsHost) -or
    ($RuntimeIdentifier.StartsWith('osx-') -and -not $isMacHost) -or
    ($RuntimeIdentifier.StartsWith('linux-') -and ($isWindowsHost -or $isMacHost))) {
    throw 'Build each runtime on its own operating system; no unverified cross-OS build is implied.'
}
if ($RuntimeIdentifier.StartsWith('linux-') -and -not $RuntimeIdentifier.EndsWith($hostArchitecture)) {
    throw 'The Linux preset requires a matching native host architecture.'
}

$mapping = $null
$mappingCreated = $false
$buildRoot = $OutputRoot
$toolchain = $null
try {
    if ($isWindowsHost) {
        . (Join-Path $PSScriptRoot 'FilamentToolchain.ps1')
        $toolchain = Get-FilamentWindowsToolchain -RuntimeIdentifier $RuntimeIdentifier -OutputRoot $OutputRoot -PythonExecutable $PythonExecutable
        if ($ExpectedToolchainIdentityFile) {
            $expectedIdentity = Get-Content -LiteralPath $ExpectedToolchainIdentityFile -Raw | ConvertFrom-Json
            if (-not (Test-FilamentToolchainIdentity -Actual $toolchain.Identity -Expected $expectedIdentity)) {
                throw 'The selected native toolchain changed after the artifact cache fingerprint was computed.'
            }
        }
        Enter-FilamentWindowsDevShell -Toolchain $toolchain -Architecture x64
        $cmake = $toolchain.CMakeExecutable
        $PythonExecutable = $toolchain.PythonExecutable
        # MSVC object paths can exceed MAX_PATH under worktrees. This temporary alias maps only artifacts.
        foreach ($letter in @('Z','Y','X','W','V','U','T')) {
            if (-not (Test-Path -LiteralPath ($letter + ':\'))) { $mapping = $letter + ':'; break }
        }
        if (-not $mapping) { throw 'No temporary drive letter is available for the native build.' }
        & subst.exe $mapping $OutputRoot
        if ($LASTEXITCODE) { throw 'Could not map the temporary native build directory.' }
        $mappingCreated = $true
        $buildRoot = $mapping + '\'
        if (-not $PythonExecutable) {
            $PythonExecutable = Join-Path $OutputRoot "tools/python-$($lock.windowsPython.version)/python.exe"
            if (-not (Test-Path -LiteralPath $PythonExecutable)) {
                if (-not $FetchBuildTools) { throw 'Provide -PythonExecutable or -FetchBuildTools for the pinned official Python build tool.' }
                $archive = Join-Path $OutputRoot "tools/python-$($lock.windowsPython.version)-embed-amd64.zip"
                New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
                if (-not (Test-Path -LiteralPath $archive)) {
                    $request = @{ Uri = $lock.windowsPython.url; OutFile = $archive; TimeoutSec = 120 }
                    if ($Proxy) { $request.Proxy = $Proxy }
                    Invoke-WebRequest @request
                }
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.windowsPython.sha256) {
                    throw 'The official Python build-tool archive does not match its pinned SHA256.'
                }
                Expand-Archive -LiteralPath $archive -DestinationPath (Split-Path -Parent $PythonExecutable) -Force
            }
        }
        # A downloaded interpreter must identify exactly as the pre-download pinned tool.
        $resolvedToolchain = Get-FilamentWindowsToolchain -RuntimeIdentifier $RuntimeIdentifier -OutputRoot $OutputRoot -PythonExecutable $PythonExecutable
        if (-not (Test-FilamentToolchainIdentity -Actual $resolvedToolchain.Identity -Expected $toolchain.Identity)) {
            throw 'The actual native build tools differ from the artifact cache toolchain identity.'
        }
        $toolchain = $resolvedToolchain
    } else {
        if ($ExpectedToolchainIdentityFile) { throw '-ExpectedToolchainIdentityFile is supported for Windows native builds only.' }
        $cmake = (Get-Command cmake -ErrorAction Stop).Source
        if (-not $PythonExecutable) { $PythonExecutable = (Get-Command python3 -ErrorAction Stop).Source }
    }

    # Filament exports host-tool metadata to its source root. A source view keeps the input checkout read-only.
    $sourceView = Join-Path $buildRoot 's'
    New-Item -ItemType Directory -Path $sourceView -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $FilamentSource -Force) {
        if ($entry.Name -eq '.git') { continue }
        $destination = Join-Path $sourceView $entry.Name
        if ($entry.PSIsContainer) {
            if (Test-Path -LiteralPath $destination) {
                $existingLink = Get-Item -LiteralPath $destination -Force
                if (-not ($existingLink.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Stale non-link source view: $destination. Use a fresh OutputRoot." }
                $linkedDirectory = $existingLink.ResolveLinkTarget($true)
                $comparison = if ($isWindowsHost) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
                if (-not [string]::Equals([IO.Path]::GetFullPath($linkedDirectory.FullName), [IO.Path]::GetFullPath($entry.FullName), $comparison)) { throw "Source view points to another checkout: $destination. Use a fresh OutputRoot." }
            } else {
                $linkType = if ($isWindowsHost) { 'Junction' } else { 'SymbolicLink' }
                New-Item -ItemType $linkType -Path $destination -Target $entry.FullName | Out-Null
            }
        } else { Copy-Item -LiteralPath $entry.FullName -Destination $destination -Force }
    }
    if ($RuntimeIdentifier -eq 'win-arm64') {
        # The upstream desktop cross-build needs host tools but ARM64 target resources.
        # Adapt only the copied top-level CMake file; never modify the pinned checkout.
        # resgen's COFF machine follows its host architecture, so its portable C output
        # must be compiled by the target compiler instead of importing an x64 .obj.
        $viewCmakePath = Join-Path $sourceView 'CMakeLists.txt'
        $viewCmake = [IO.File]::ReadAllText($viewCmakePath)
        $resourcePattern = '(?m)^(\s*)if \(WIN32\)(\r?\n\s*set\(RESGEN_OUTPUTS)'
        $toolsPattern = '(?m)^(\s*)add_subdirectory\(\$\{TOOLS\}/cmgen\)([\s\S]*?add_subdirectory\(\$\{TOOLS\}/specgen\))'
        if ([regex]::Matches($viewCmake, $resourcePattern).Count -ne 1 -or
            [regex]::Matches($viewCmake, $toolsPattern).Count -ne 1 -or
            [regex]::Matches($viewCmake, 'elseif \(WASM OR ANDROID_ON_WINDOWS\)').Count -ne 1) {
            throw 'Pinned Filament Windows cross-build adaptation no longer matches its source.'
        }
        $viewCmake = [regex]::Replace($viewCmake, $resourcePattern, '$1if (WIN32 AND NOT CMAKE_CROSSCOMPILING)$2')
        $viewCmake = $viewCmake.Replace('elseif (WASM OR ANDROID_ON_WINDOWS)', 'elseif (WASM OR ANDROID_ON_WINDOWS OR (WIN32 AND CMAKE_CROSSCOMPILING))')
        $viewCmake = [regex]::Replace($viewCmake, $toolsPattern, '$1if (NOT CMAKE_CROSSCOMPILING)' + "`n" + '$1    add_subdirectory(${TOOLS}/cmgen)$2' + "`n" + '$1endif()')
        [IO.File]::WriteAllText($viewCmakePath, $viewCmake, [Text.UTF8Encoding]::new($false))
    }
    $gitDirectory = & git @gitPathArguments -c "safe.directory=$($FilamentSource.Replace('\','/'))" -C $FilamentSource rev-parse --absolute-git-dir
    if ($LASTEXITCODE) { throw 'Could not resolve the read-only Filament Git directory.' }
    $gitDirectory = ([string]$gitDirectory).Trim()
    Set-Content -LiteralPath (Join-Path $sourceView '.git') -Value ('gitdir: ' + $gitDirectory.Replace('\','/')) -Encoding utf8NoBOM
    $env:GIT_CONFIG_COUNT = '3'
    $env:GIT_CONFIG_KEY_0 = 'safe.directory'
    $env:GIT_CONFIG_VALUE_0 = $FilamentSource.Replace('\','/')
    $env:GIT_CONFIG_KEY_1 = 'safe.directory'
    $env:GIT_CONFIG_VALUE_1 = $sourceView.Replace('\','/').TrimEnd('/')
    $env:GIT_CONFIG_KEY_2 = 'core.longpaths'
    $env:GIT_CONFIG_VALUE_2 = 'true'

    $runtime = Join-Path $OutputRoot "runtimes/$RuntimeIdentifier/native"
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    if ($RuntimeIdentifier -eq 'win-arm64') {
        # Reuse the existing x64 cache, or build the exact pinned host tools on demand.
        $hostBuild = Join-Path $buildRoot 'b'
        $hostArguments = @('-S',(Join-Path $repo 'native/HeyMelody.Filament.Native'),'-B',$hostBuild,'-G','Ninja',
            '-DCMAKE_BUILD_TYPE=Release','-DCMAKE_OBJECT_PATH_MAX=250',"-DHM_FILAMENT_SOURCE=$sourceView",
            "-DHM_RUNTIME_OUTPUT_DIRECTORY=$(Join-Path $OutputRoot 'runtimes/win-x64/native')",
            "-DPython3_EXECUTABLE=$PythonExecutable",'-DHM_RUNTIME_IDENTIFIER=win-x64',
            "-DCMAKE_MAKE_PROGRAM=$($toolchain.NinjaExecutable)","-DCMAKE_C_COMPILER=$($toolchain.HostCompiler)","-DCMAKE_CXX_COMPILER=$($toolchain.HostCompiler)")
        & $cmake @hostArguments
        if ($LASTEXITCODE) { throw 'Filament x64 host-tool CMake configure failed.' }
        & $cmake --build $hostBuild --target matc cmgen filamesh mipgen resgen uberz glslminifier --parallel $Parallel
        if ($LASTEXITCODE) { throw 'Filament x64 host-tool compilation failed.' }
        Enter-FilamentWindowsDevShell -Toolchain $toolchain -Architecture arm64
        $compiler = $toolchain.TargetCompiler
        if ($compiler -notmatch '[\\/]Hostx64[\\/]arm64[\\/]cl\.exe$') { throw 'The ARM64 cross compiler was not selected.' }
    }
    $build = Join-Path $buildRoot $(if ($RuntimeIdentifier -eq 'win-arm64') { 'b-arm64' } elseif ($isWindowsHost) { 'b' } else { 'b/' + $RuntimeIdentifier })
    $arguments = @('-S',(Join-Path $repo 'native/HeyMelody.Filament.Native'),'-B',$build,'-G','Ninja',
        '-DCMAKE_BUILD_TYPE=Release','-DCMAKE_OBJECT_PATH_MAX=250',"-DHM_FILAMENT_SOURCE=$sourceView",
        "-DHM_RUNTIME_OUTPUT_DIRECTORY=$runtime","-DPython3_EXECUTABLE=$PythonExecutable","-DHM_RUNTIME_IDENTIFIER=$RuntimeIdentifier")
    if ($isWindowsHost) {
        $arguments += @("-DCMAKE_MAKE_PROGRAM=$($toolchain.NinjaExecutable)","-DCMAKE_C_COMPILER=$($toolchain.TargetCompiler)","-DCMAKE_CXX_COMPILER=$($toolchain.TargetCompiler)")
    }
    if ($isMacHost) { $arguments += '-DCMAKE_OSX_ARCHITECTURES=' + $(if ($RuntimeIdentifier.EndsWith('arm64')) { 'arm64' } else { 'x86_64' }) }
    if ($RuntimeIdentifier -eq 'win-arm64') {
        $arguments += @('-DCMAKE_SYSTEM_NAME=Windows','-DCMAKE_SYSTEM_PROCESSOR=ARM64','-DDIST_ARCH=arm64',
            "-DCMAKE_SYSTEM_VERSION=$($toolchain.WindowsSdkVersion)")
    }
    & $cmake @arguments
    if ($LASTEXITCODE) { throw 'Filament native CMake configure failed.' }
    & $cmake --build $build --target HeyMelody.Filament.Native hm_filament_probe hm_filament_smoke hm_filament_sha256_tests --parallel $Parallel
    if ($LASTEXITCODE) { throw 'Filament native compilation failed.' }
    if (-not $SkipSmoke -and -not ($RuntimeIdentifier -eq 'win-arm64' -and $hostArchitecture -ne 'arm64')) {
        $smoke = Join-Path $build $(if ($isWindowsHost) { 'hm_filament_smoke.exe' } else { 'hm_filament_smoke' })
        if ($isWindowsHost) { $env:PATH = $runtime + [IO.Path]::PathSeparator + $env:PATH }
        & $smoke
        if ($LASTEXITCODE) { throw 'Native ABI smoke failed.' }
        $hashTests = Join-Path $build $(if ($isWindowsHost) { 'hm_filament_sha256_tests.exe' } else { 'hm_filament_sha256_tests' })
        & $hashTests (Join-Path $repo 'src/HeyMelody.App/Assets/Devices/EncoFree4/model.glb') (Join-Path $repo 'src/HeyMelody.App/Assets/Devices/EncoFree4Dynaudio/model.glb')
        if ($LASTEXITCODE) { throw 'Native SHA-256 reference vectors failed.' }
    } elseif (-not $SkipSmoke) {
        Write-Output 'ARM64 runtime cross-compiled; native execution requires Windows ARM64 hardware.'
    }

    $licenses = @('LICENSE','third_party/cgltf/LICENSE','third_party/stb/LICENSE','third_party/draco/LICENSE','third_party/getopt/LICENSE',
        'third_party/meshoptimizer/LICENSE','third_party/basisu/LICENSE','third_party/robin-map/LICENSE','third_party/zstd/LICENSE')
    if (-not $isMacHost) {
        $licenses += @('third_party/vkmemalloc/LICENSE.txt','third_party/smol-v/LICENSE','third_party/spirv-headers/LICENSE')
    }
    foreach ($name in $licenses) {
        $inputPath = Join-Path $FilamentSource $name
        if (-not (Test-Path -LiteralPath $inputPath)) { throw "Missing static dependency license: $name" }
        $relative = if ($name -eq 'LICENSE') { 'licenses/Filament/LICENSE' } else { 'licenses/Filament/' + $name }
        $destination = Join-Path $runtime $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $inputPath -Destination $destination -Force
    }
    # MikkTSpace carries its license in the small public header, rather than a separate LICENSE file.
    $mikkHeader = [IO.File]::ReadAllText((Join-Path $FilamentSource 'third_party/mikktspace/include/mikktspace/mikktspace.h'))
    $mikkNotice = [regex]::Match($mikkHeader, '(?s)/\*\*\s*\*\s+Copyright \(C\) 2011 by Morten S\. Mikkelsen.*?\*/')
    if (-not $mikkNotice.Success) { throw 'The pinned MikkTSpace license notice could not be extracted.' }
    $mikkDestination = Join-Path $runtime 'licenses/Filament/third_party/mikktspace/NOTICE'
    New-Item -ItemType Directory -Path (Split-Path -Parent $mikkDestination) -Force | Out-Null
    [IO.File]::WriteAllText($mikkDestination, $mikkNotice.Value + "`n", [Text.UTF8Encoding]::new($false))
    if (-not $isMacHost) {
        $bluevkLicense = Join-Path $runtime 'licenses/Filament/libs/bluevk/LICENSE'
        New-Item -ItemType Directory -Path (Split-Path -Parent $bluevkLicense) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $FilamentSource 'LICENSE') -Destination $bluevkLicense -Force
        $vulkanNotice = "Vulkan API headers vendored by Filament under libs/bluevk/include/vulkan/.`n"
        foreach ($header in @('vulkan.h','vk_platform.h')) {
            $headerText = [IO.File]::ReadAllText((Join-Path $FilamentSource ('libs/bluevk/include/vulkan/' + $header)))
            $headerNotice = [regex]::Match($headerText, '(?s)/\*\s*\*\* Copyright.*?\*/')
            if (-not $headerNotice.Success -or $headerNotice.Value -notmatch 'The Khronos Group Inc\.' -or
                $headerNotice.Value -notmatch 'SPDX-License-Identifier: Apache-2\.0') {
                throw "The pinned Vulkan header license notice could not be extracted: $header"
            }
            $vulkanNotice += "`n${header}:`n" + $headerNotice.Value + "`n"
        }
        $vulkanNotice += "`nThe Apache-2.0 license text is included at licenses/Filament/libs/bluevk/LICENSE.`n"
        $vulkanNoticePath = Join-Path $runtime 'licenses/Filament/libs/bluevk/include/vulkan/NOTICE'
        New-Item -ItemType Directory -Path (Split-Path -Parent $vulkanNoticePath) -Force | Out-Null
        [IO.File]::WriteAllText($vulkanNoticePath, $vulkanNotice, [Text.UTF8Encoding]::new($false))
    }
    $apiNotice = if ($isMacHost) { '' } else { "BlueVK: Copyright (C) The Android Open Source Project, Apache-2.0; see libs/bluevk/LICENSE.`nVulkan API headers: Copyright 2014-2024 The Khronos Group Inc., Apache-2.0; see libs/bluevk/include/vulkan/NOTICE.`n" }
    $filamentNotice = "This binary includes Google Filament under Apache-2.0; see LICENSE and the pinned upstream source copyright notices.`n${apiNotice}MikkTSpace: Copyright (C) 2011 Morten S. Mikkelsen; its original header notice is included.`nOther statically linked components retain the copyright and license notices in third_party/.`nNo Android OPLUS VFX SDK or JNI binary is distributed.`n"
    [IO.File]::WriteAllText((Join-Path $runtime 'licenses/Filament/NOTICE'), $filamentNotice, [Text.UTF8Encoding]::new($false))
    $library = if ($isWindowsHost) { 'HeyMelody.Filament.Native.dll' } elseif ($isMacHost) { 'libHeyMelody.Filament.Native.dylib' } else { 'libHeyMelody.Filament.Native.so' }
    $probe = if ($isWindowsHost) { 'hm_filament_probe.exe' } else { 'hm_filament_probe' }
    $backends = @(if ($isMacHost) { 'Metal' } else { 'Vulkan'; 'OpenGL' })
    $materialRelative = 'materials/gltfio.uberz'
    $material = Join-Path $runtime $materialRelative
    New-Item -ItemType Directory -Path (Split-Path -Parent $material) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $build 'filament/libs/gltfio/default.uberz') -Destination $material -Force
    $materialHash = (Get-FileHash -LiteralPath $material -Algorithm SHA256).Hash.ToLowerInvariant()
    $files = @($library,$probe,$materialRelative) + @(Get-ChildItem -LiteralPath (Join-Path $runtime 'licenses') -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($runtime,$_.FullName).Replace('\','/') })
    $manifest = [ordered]@{
        schemaVersion = 2; abiVersion = $lock.abiVersion; filamentCommit = $lock.commit; rid = $RuntimeIdentifier
        supportedBackends = @($backends); defaultBackend = $backends[0]
        logCallbackExport = 'hm_set_log_callback'
        materials = [ordered]@{ path = $materialRelative; sha256 = $materialHash; supportedBackends = @($backends) }
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        files = @($files | Sort-Object | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $runtime $_) -Algorithm SHA256).Hash.ToLowerInvariant() } })
    }
    $after = & git @gitPathArguments -c "safe.directory=$($FilamentSource.Replace('\','/'))" -C $FilamentSource status --porcelain --untracked-files=normal
    if ($LASTEXITCODE) { throw 'Could not verify the Filament source after the build.' }
    if ($after) { throw 'The read-only Filament input changed during the build.' }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtime 'filament-runtime.json.tmp') -Encoding utf8NoBOM
    Move-Item -LiteralPath (Join-Path $runtime 'filament-runtime.json.tmp') -Destination (Join-Path $runtime 'filament-runtime.json') -Force
    [ordered]@{ schemaVersion=1; filamentCommit=$lock.commit; source=$FilamentSource; build=$build; runtime=$runtime; rid=$RuntimeIdentifier
        toolchainIdentity = $(if ($toolchain) { $toolchain.Identity } else { $null }) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot "build-receipt-$RuntimeIdentifier.json") -Encoding utf8NoBOM
    Write-Output "Filament native runtime ready: $runtime"
} finally {
    if ($mappingCreated) { & subst.exe $mapping /d }
    foreach ($key in @([Environment]::GetEnvironmentVariables().Keys)) {
        if (-not $environmentBefore.Contains($key)) { [Environment]::SetEnvironmentVariable([string]$key, [NullString]::Value, 'Process') }
    }
    foreach ($key in $environmentBefore.Keys) { [Environment]::SetEnvironmentVariable([string]$key, [string]$environmentBefore[$key], 'Process') }
}
