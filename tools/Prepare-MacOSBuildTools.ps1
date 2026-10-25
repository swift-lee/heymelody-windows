#Requires -Version 7.4
[CmdletBinding()]
param([string]$ToolsRoot, [string]$Proxy)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsMacOS -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'Arm64') {
    throw 'These pinned application build tools target Apple silicon macOS.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $repo 'artifacts/tools' }
$ToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
New-Item -ItemType Directory -Path $ToolsRoot -Force | Out-Null
$lock = Get-Content -LiteralPath (Join-Path $repo 'packaging/macos/toolchain.lock.json') -Raw | ConvertFrom-Json
function Get-VerifiedArchive($Tool, [string]$FileName) {
    $path = Join-Path $ToolsRoot $FileName
    if (Test-Path -LiteralPath $path) {
        if ((Get-FileHash -LiteralPath $path -Algorithm $Tool.hashAlgorithm).Hash.ToLowerInvariant() -eq $Tool.hash) { return $path }
        throw "Existing tool archive has an unexpected checksum; inspect it before retrying: $path"
    }
    $temporary = $path + '.partial'
    $arguments = @{ Uri = $Tool.url; OutFile = $temporary; TimeoutSec = 300 }
    try { Invoke-WebRequest @arguments }
    catch {
        if (-not $Proxy) { throw }
        $arguments.Proxy = $Proxy
        Invoke-WebRequest @arguments
    }
    if ((Get-FileHash -LiteralPath $temporary -Algorithm $Tool.hashAlgorithm).Hash.ToLowerInvariant() -ne $Tool.hash) {
        throw "Downloaded tool archive does not match the official checksum: $FileName"
    }
    Move-Item -LiteralPath $temporary -Destination $path
    return $path
}
$dotnetArchive = Get-VerifiedArchive $lock.dotnet "dotnet-sdk-$($lock.dotnet.version)-osx-arm64.tar.gz"
$cmakeArchive = Get-VerifiedArchive $lock.cmake "cmake-$($lock.cmake.version)-macos-universal.tar.gz"
$ninjaArchive = Get-VerifiedArchive $lock.ninja "ninja-mac-v$($lock.ninja.version).zip"
$dotnetDirectory = Join-Path $ToolsRoot 'dotnet'
if (-not (Test-Path -LiteralPath (Join-Path $dotnetDirectory 'dotnet'))) {
    New-Item -ItemType Directory -Path $dotnetDirectory -Force | Out-Null
    & tar -xzf $dotnetArchive -C $dotnetDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the .NET SDK archive.' }
}
$cmake = Join-Path $ToolsRoot "cmake-$($lock.cmake.version)-macos-universal/CMake.app/Contents/bin/cmake"
if (-not (Test-Path -LiteralPath $cmake)) {
    & tar -xzf $cmakeArchive -C $ToolsRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the CMake archive.' }
}
$ninjaDirectory = Join-Path $ToolsRoot "ninja-$($lock.ninja.version)"
$ninja = Join-Path $ninjaDirectory 'ninja'
if (-not (Test-Path -LiteralPath $ninja)) { Expand-Archive -LiteralPath $ninjaArchive -DestinationPath $ninjaDirectory }
& chmod +x $ninja
if ($LASTEXITCODE -ne 0) { throw 'Could not mark Ninja executable.' }
[ordered]@{ runtimeIdentifier = 'osx-arm64'; dotnet = (Join-Path $dotnetDirectory 'dotnet');
    cmake = $cmake; ninja = $ninja; source = 'official archives verified against packaging/macos/toolchain.lock.json' } | ConvertTo-Json -Compress
