#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$parseErrors = $null
$tokens = $null
$helper = Join-Path $repository 'tools/Publish-AotExperimental.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($helper, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors.Message -join '; ') }
$mergeFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Merge-ExperimentalPublication' }, $true)
if (-not $mergeFunction) { throw 'Missing publication merge helper.' }
. ([ScriptBlock]::Create($mergeFunction.Extent.Text))
foreach ($name in @('Get-ExperimentalPackageLicense', 'Get-ExperimentalLicenseSource')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw ('Missing license source helper: ' + $name) }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$buildAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'Build-Client.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors.Message -join '; ') }
foreach ($name in @('Resolve-PackageDirectory', 'Get-DownloadedCompilerPackages')) {
    $definition = $buildAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw ('Missing compiler package helper: ' + $name) }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('heymelody-aot-packaging-' + [Guid]::NewGuid().ToString('N'))
try {
    $source = Join-Path $temporaryRoot 'source'
    $destination = Join-Path $temporaryRoot 'destination'
    New-Item -ItemType Directory -Path $source, $destination | Out-Null
    [IO.File]::WriteAllText((Join-Path $source 'libSkiaSharp.dll'), 'same native fixture')
    [IO.File]::WriteAllText((Join-Path $destination 'libSkiaSharp.dll'), 'same native fixture')
    [IO.File]::WriteAllText((Join-Path $source 'HeyMelody.exe'), 'native app fixture')
    [IO.File]::WriteAllText((Join-Path $source 'HeyMelody.pdb'), 'symbols')
    Merge-ExperimentalPublication $source $destination -RequireIdentical
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'HeyMelody.exe'))) { throw 'New native executable was not merged.' }
    if (Test-Path -LiteralPath (Join-Path $destination 'HeyMelody.pdb')) { throw 'Debug symbols were merged into the package.' }
    [IO.File]::WriteAllText((Join-Path $source 'libSkiaSharp.dll'), 'conflicting native fixture')
    $conflictRejected = $false
    try { Merge-ExperimentalPublication $source $destination -RequireIdentical } catch {
        if ($_.Exception.Message -notlike '*publication file conflict*') { throw }
        $conflictRejected = $true
    }
    if (-not $conflictRejected) { throw 'Conflicting shared native libraries were silently overwritten.' }
    if ([IO.File]::ReadAllText((Join-Path $destination 'libSkiaSharp.dll')) -ne 'same native fixture') { throw 'The rejected merge changed the shared library.' }
    $downloadRoot = Join-Path $temporaryRoot 'packages'
    $packageCache = $downloadRoot
    $compilerDirectory = Join-Path $downloadRoot 'runtime.win-x64.microsoft.dotnet.ilcompiler/10.0.12'
    New-Item -ItemType Directory -Path $compilerDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $compilerDirectory 'runtime.win-x64.microsoft.dotnet.ilcompiler.10.0.12.nupkg.sha512'), 'package-digest-fixture')
    [IO.File]::WriteAllText((Join-Path $compilerDirectory 'LICENSE.TXT'), 'Native AOT runtime license fixture')
    [IO.File]::WriteAllText((Join-Path $compilerDirectory 'THIRD-PARTY-NOTICES.TXT'), 'Native AOT runtime notices fixture')
    $download = [pscustomobject]@{ name = 'runtime.win-x64.Microsoft.DotNet.ILCompiler'; version = '[10.0.12, 10.0.12]' }
    $framework = [pscustomobject]@{ downloadDependencies = @($download) }
    $assets = [pscustomobject]@{
        packageFolders = [pscustomobject]@{ $downloadRoot = [pscustomobject]@{} }
        project = [pscustomobject]@{ frameworks = [pscustomobject]@{ net10 = $framework; secondFramework = $framework } }
    }
    $downloads = @(Get-DownloadedCompilerPackages $assets)
    if ($downloads.Count -ne 1 -or $downloads[0].version -ne '10.0.12' -or $downloads[0].sha512 -ne 'package-digest-fixture' -or $downloads[0].files.Count -ne 2) {
        throw 'PackageDownload compiler runtime/license evidence was not resolved or deduplicated correctly.'
    }
    $sharedDirectory = Join-Path $temporaryRoot 'shared-runtime'
    New-Item -ItemType Directory -Path $sharedDirectory | Out-Null
    $licenseMetadata = '<package><metadata><license type="expression">MIT</license><repository url="https://example.test/runtime" commit="exact-fixture-revision" /></metadata></package>'
    [IO.File]::WriteAllText((Join-Path $compilerDirectory 'compiler.nuspec'), $licenseMetadata)
    [IO.File]::WriteAllText((Join-Path $sharedDirectory 'runtime.nuspec'), $licenseMetadata)
    [IO.File]::WriteAllText((Join-Path $sharedDirectory 'LICENSE.TXT'), 'Unmodified shared MIT license fixture')
    Remove-Item -LiteralPath (Join-Path $compilerDirectory 'LICENSE.TXT')
    $selected = Get-ExperimentalLicenseSource $compilerDirectory 'compiler-package' $sharedDirectory 'runtime-package' 'LICENSE.TXT'
    if ($selected.package -ne 'runtime-package' -or $selected.basis -ne 'matching-MIT-and-source-revision' -or
        [IO.File]::ReadAllText($selected.path) -ne 'Unmodified shared MIT license fixture') {
        throw 'The missing compiler license did not preserve the original shared runtime text and provenance.'
    }
    foreach ($invalid in @(
        ($licenseMetadata -replace '>MIT<', '>Apache-2.0<'),
        ($licenseMetadata -replace 'exact-fixture-revision', 'different-revision')
    )) {
        [IO.File]::WriteAllText((Join-Path $compilerDirectory 'compiler.nuspec'), $invalid)
        $rejected = $false
        try { $null = Get-ExperimentalLicenseSource $compilerDirectory 'compiler-package' $sharedDirectory 'runtime-package' 'LICENSE.TXT' } catch {
            if ($_.Exception.Message -notlike '*matching MIT declarations*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'A mismatched license or source revision was accepted as shared license evidence.' }
    }
    Write-Output 'PASS AOT packaging: shared-file conflicts, PackageDownload discovery, SPDX-only MIT source preservation and mismatch rejection'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporaryRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe AOT packaging test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
