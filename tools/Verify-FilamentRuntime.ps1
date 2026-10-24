[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','osx-x64','osx-arm64','linux-x64','linux-arm64')][string]$RuntimeIdentifier,
    [switch]$RequireCurrent
)
$ErrorActionPreference = 'Stop'
$runtimeDirectory = [IO.Path]::GetFullPath($Directory)
$runtimePrefix = $runtimeDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $runtimeDirectory 'filament-runtime.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Filament runtime manifest is missing.' }
if ((Get-Item -LiteralPath $manifestPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Filament runtime manifest cannot be a symbolic link.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expectedCommit = 'ea31afeaf66339e7414aa648aa76c7e61adbde28'
$current = $manifest.schemaVersion -eq 2 -and $manifest.abiVersion -eq 2
$legacy = $manifest.schemaVersion -eq 1 -and $manifest.abiVersion -eq 1
$hasLogCallback = $manifest.PSObject.Properties.Name -ccontains 'logCallbackExport' -and $manifest.logCallbackExport -ceq 'hm_set_log_callback'
if ((-not $current -and -not $legacy) -or ($RequireCurrent -and -not $current) -or
    $manifest.filamentCommit -ne $expectedCommit -or $manifest.rid -ne $RuntimeIdentifier) {
    throw 'Filament runtime schema, ABI, source commit or RID does not match this build.'
}
if ($RequireCurrent -and -not $hasLogCallback) { throw 'The current Filament runtime must declare its required native log callback export.' }
$libraryName = if ($RuntimeIdentifier.StartsWith('win-')) { 'HeyMelody.Filament.Native.dll' } elseif ($RuntimeIdentifier.StartsWith('osx-')) { 'libHeyMelody.Filament.Native.dylib' } else { 'libHeyMelody.Filament.Native.so' }
$probeName = if ($RuntimeIdentifier.StartsWith('win-')) { 'hm_filament_probe.exe' } else { 'hm_filament_probe' }
$expectedBackends = @(if ($RuntimeIdentifier.StartsWith('osx-')) { 'Metal' } else { 'Vulkan'; 'OpenGL' })
$expectedDefault = $expectedBackends[0]
if ($current) {
    if ((@($manifest.supportedBackends) -join ',') -cne ($expectedBackends -join ',') -or $manifest.defaultBackend -cne $expectedDefault) {
        throw 'Filament runtime has the wrong compiled backends or default backend.'
    }
    if ($manifest.materials.path -cne 'materials/gltfio.uberz' -or
        $manifest.materials.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
        (@($manifest.materials.supportedBackends) -join ',') -cne ($expectedBackends -join ',')) {
        throw 'Filament material archive metadata does not match the compiled backends.'
    }
} else {
    $expectedLegacy = if ($RuntimeIdentifier.StartsWith('osx-')) { 'Metal' } else { 'OpenGL' }
    if ($manifest.backend -cne $expectedLegacy) { throw 'Legacy Filament runtime has the wrong graphics backend.' }
}
$files = @($manifest.files)
$requiredFiles = @($libraryName)
if ($current) { $requiredFiles += @($probeName, 'materials/gltfio.uberz') }
foreach ($required in $requiredFiles) {
    if (@($files | Where-Object { $_.path -ceq $required }).Count -ne 1) { throw ('Filament runtime must declare: ' + $required) }
}
if (@($files | Where-Object { $_.path -like 'licenses/*' }).Count -eq 0) { throw 'Filament runtime license declarations are missing.' }
if ($current -and $expectedBackends -contains 'Vulkan') {
    foreach ($licensePrefix in @('third_party/vkmemalloc/', 'third_party/smol-v/', 'third_party/spirv-headers/', 'libs/bluevk/')) {
        if (@($files | Where-Object { $_.path -like ('licenses/Filament/' + $licensePrefix + '*') }).Count -eq 0) {
            throw ('Filament Vulkan dependency license is missing: ' + $licensePrefix)
        }
    }
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    if ([string]::IsNullOrWhiteSpace($file.path) -or [IO.Path]::IsPathRooted($file.path) -or
        $file.path.Contains('\') -or @($file.path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
        -not $seen.Add($file.path)) { throw 'Invalid or duplicate Filament runtime file path.' }
    $path = [IO.Path]::GetFullPath((Join-Path $runtimeDirectory $file.path))
    if (-not $path.StartsWith($runtimePrefix, [StringComparison]::Ordinal) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw ('Missing or unsafe Filament runtime file: ' + $file.path)
    }
    $item = Get-Item -LiteralPath $path
    $ancestor = $item
    while ($null -ne $ancestor -and ($ancestor.FullName -eq $runtimeDirectory -or $ancestor.FullName.StartsWith($runtimePrefix, [StringComparison]::Ordinal))) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Filament runtime files and directories cannot be symbolic links.' }
        $ancestor = if ($ancestor -is [IO.DirectoryInfo]) { $ancestor.Parent } else { $ancestor.Directory }
    }
    if ($item.Length -le 0 -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
        throw ('Filament runtime hash mismatch: ' + $file.path)
    }
}
if ($current -and @($files | Where-Object { $_.path -ceq $manifest.materials.path -and $_.sha256 -ceq $manifest.materials.sha256 }).Count -ne 1) {
    throw 'Filament material archive hash is not bound to its runtime file.'
}
function Assert-NativeArchitecture([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($RuntimeIdentifier.StartsWith('win-')) {
            if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Filament runtime is not a PE binary.' }
            $reader.BaseStream.Position = 0x3c
            $header = $reader.ReadInt32()
            if ($header -lt 64 -or $header -gt $reader.BaseStream.Length - 6) { throw 'Invalid Filament PE header.' }
            $reader.BaseStream.Position = $header
            $expected = if ($RuntimeIdentifier -eq 'win-arm64') { 0xaa64 } else { 0x8664 }
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $expected) { throw 'Filament PE architecture does not match its RID.' }
        } elseif ($RuntimeIdentifier.StartsWith('linux-')) {
            if ($reader.ReadUInt32() -ne 0x464c457f -or $reader.ReadByte() -ne 2 -or $reader.ReadByte() -ne 1) { throw 'Filament runtime must be a little-endian ELF64 binary.' }
            $reader.BaseStream.Position = 18
            $expected = if ($RuntimeIdentifier.EndsWith('arm64')) { 0xb7 } else { 0x3e }
            if ($reader.ReadUInt16() -ne $expected) { throw 'Filament ELF architecture does not match its RID.' }
        } else {
            if ($reader.ReadUInt32() -ne 4277009103) { throw 'Filament runtime must be a thin little-endian Mach-O64 binary for its RID.' }
            $expected = if ($RuntimeIdentifier.EndsWith('arm64')) { 0x0100000c } else { 0x01000007 }
            if ($reader.ReadUInt32() -ne $expected) { throw 'Filament Mach-O architecture does not match its RID.' }
        }
    } finally { $reader.Dispose() }
}
function Assert-NativeLogExport([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = $null
    $reader = $null
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        function Resolve-ExportRva([long]$Rva) {
            foreach ($section in $pe.PEHeaders.SectionHeaders) {
                if ($Rva -ge $section.VirtualAddress -and
                    $Rva -lt $section.VirtualAddress + [Math]::Max($section.VirtualSize, $section.SizeOfRawData)) {
                    return [long]$section.PointerToRawData + $Rva - $section.VirtualAddress
                }
            }
            throw 'Filament export RVA is outside its PE sections.'
        }
        $directory = $pe.PEHeaders.PEHeader.ExportTableDirectory
        if ($directory.RelativeVirtualAddress -le 0) { throw 'Filament PE export directory is missing.' }
        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
        $reader.BaseStream.Position = (Resolve-ExportRva $directory.RelativeVirtualAddress) + 24
        $count = $reader.ReadUInt32()
        [void]$reader.ReadUInt32()
        $namesRva = $reader.ReadUInt32()
        if ($count -eq 0 -or $count -gt 65536 -or $namesRva -eq 0) { throw 'Invalid Filament PE exports.' }
        $namesOffset = Resolve-ExportRva $namesRva
        for ($index = 0; $index -lt $count; $index++) {
            $reader.BaseStream.Position = $namesOffset + $index * 4
            $nameRva = $reader.ReadUInt32()
            $reader.BaseStream.Position = Resolve-ExportRva $nameRva
            $bytes = [Collections.Generic.List[byte]]::new()
            while ($reader.BaseStream.Position -lt $stream.Length -and $bytes.Count -lt 256) {
                $value = $reader.ReadByte()
                if ($value -eq 0) { break }
                $bytes.Add($value)
            }
            if ([Text.Encoding]::ASCII.GetString($bytes.ToArray()) -ceq 'hm_set_log_callback') { return }
        }
        throw 'Filament runtime does not export hm_set_log_callback.'
    } finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $pe) { $pe.Dispose() }
        $stream.Dispose()
    }
}
$libraryPath = Join-Path $runtimeDirectory $libraryName
Assert-NativeArchitecture $libraryPath
if ($current) { Assert-NativeArchitecture (Join-Path $runtimeDirectory $probeName) }
if ($hasLogCallback -and $RuntimeIdentifier.StartsWith('win-')) { Assert-NativeLogExport $libraryPath }
$result = [ordered]@{
    success = $true; schemaVersion = $manifest.schemaVersion; rid = $RuntimeIdentifier; abiVersion = $manifest.abiVersion
    filamentCommit = $manifest.filamentCommit; library = $libraryName
    sha256 = (Get-FileHash -LiteralPath $libraryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); filesVerified = $files.Count
}
if ($current) {
    $result.supportedBackends = @($manifest.supportedBackends)
    $result.defaultBackend = $manifest.defaultBackend
    $result.probe = $probeName
    $result.probeSha256 = (Get-FileHash -LiteralPath (Join-Path $runtimeDirectory $probeName) -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.materials = $manifest.materials
    $result.logCallbackExport = if ($hasLogCallback) { 'hm_set_log_callback' } else { $null }
    $result.logCallbackExportVerified = $hasLogCallback -and $RuntimeIdentifier.StartsWith('win-')
} else { $result.backend = $manifest.backend }
$result
