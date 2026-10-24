[CmdletBinding()]
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'artifacts/filament-cache-checks/summary.json'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
Import-Module (Join-Path $repoRoot 'tools/filament/FilamentArtifactCache.psm1') -Force
$checks = [Collections.Generic.List[object]]::new()
$httpReceipts = [Collections.Generic.List[object]]::new()
$scratchParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'heymelody-filament-cache-tests'))
$scratch = Join-Path $scratchParent ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$verifier = Join-Path $repoRoot 'tools/Verify-FilamentRuntime.ps1'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message (expected '$Expected', got '$Actual')." }
}

function Invoke-Check([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        $checks.Add([pscustomobject]@{ name = $Name; passed = $true })
    } catch {
        $checks.Add([pscustomobject]@{ name = $Name; passed = $false; error = $_.Exception.Message })
        Write-Warning "$Name`: $($_.Exception.Message)"
    }
}

function Write-Json([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}

function New-SourceFixture([string]$Root) {
    foreach ($directory in @('native/HeyMelody.Filament.Native/src', 'tools/filament', 'src/Managed', 'docs')) {
        [IO.Directory]::CreateDirectory((Join-Path $Root $directory)) | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tools/filament/filament.lock.json') -Destination (Join-Path $Root 'tools/filament/filament.lock.json')
    [IO.File]::WriteAllText((Join-Path $Root 'native/HeyMelody.Filament.Native/CMakeLists.txt'), 'project(cache_fixture)')
    [IO.File]::WriteAllText((Join-Path $Root 'native/HeyMelody.Filament.Native/src/bridge.cpp'), 'int cache_fixture = 1;')
    [IO.File]::WriteAllText((Join-Path $Root 'tools/filament/Build-FilamentNative.ps1'), '# mock build input')
    [IO.File]::WriteAllText((Join-Path $Root 'tools/filament/FilamentToolchain.ps1'), '# mock toolchain input')
    [IO.File]::WriteAllText((Join-Path $Root 'src/Managed/Widget.cs'), '// managed input')
    [IO.File]::WriteAllText((Join-Path $Root 'docs/usage.md'), '# user guide')
}

function New-PeFixture([string]$Path, [string]$RuntimeIdentifier) {
    # Metadata-only PE32+ files are never executed. Include the one required diagnostic export.
    $bytes = [byte[]]::new(1024)
    $bytes[0] = 0x4d; $bytes[1] = 0x5a; $bytes[0x3c] = 0x40
    $bytes[0x40] = 0x50; $bytes[0x41] = 0x45
    if ($RuntimeIdentifier -eq 'win-arm64') { $bytes[0x44] = 0x64; $bytes[0x45] = 0xaa }
    else { $bytes[0x44] = 0x64; $bytes[0x45] = 0x86 }
    [BitConverter]::GetBytes([uint16]1).CopyTo($bytes, 0x46)
    [BitConverter]::GetBytes([uint16]0xf0).CopyTo($bytes, 0x54)
    [BitConverter]::GetBytes([uint16]0x2022).CopyTo($bytes, 0x56)
    [BitConverter]::GetBytes([uint16]0x20b).CopyTo($bytes, 0x58)
    [BitConverter]::GetBytes([uint32]0x1000).CopyTo($bytes, 0x78)
    [BitConverter]::GetBytes([uint32]0x200).CopyTo($bytes, 0x7c)
    [BitConverter]::GetBytes([uint32]0x2000).CopyTo($bytes, 0x90)
    [BitConverter]::GetBytes([uint32]0x200).CopyTo($bytes, 0x94)
    [BitConverter]::GetBytes([uint32]16).CopyTo($bytes, 0xc4)
    [BitConverter]::GetBytes([uint32]0x1000).CopyTo($bytes, 0xc8)
    [BitConverter]::GetBytes([uint32]0x100).CopyTo($bytes, 0xcc)
    [Text.Encoding]::ASCII.GetBytes('.edata').CopyTo($bytes, 0x148)
    [BitConverter]::GetBytes([uint32]0x200).CopyTo($bytes, 0x150)
    [BitConverter]::GetBytes([uint32]0x1000).CopyTo($bytes, 0x154)
    [BitConverter]::GetBytes([uint32]0x200).CopyTo($bytes, 0x158)
    [BitConverter]::GetBytes([uint32]0x200).CopyTo($bytes, 0x15c)
    [BitConverter]::GetBytes([uint32]0x40000040).CopyTo($bytes, 0x16c)
    [BitConverter]::GetBytes([uint32]1).CopyTo($bytes, 0x214)
    [BitConverter]::GetBytes([uint32]1).CopyTo($bytes, 0x218)
    [BitConverter]::GetBytes([uint32]0x1030).CopyTo($bytes, 0x21c)
    [BitConverter]::GetBytes([uint32]0x1034).CopyTo($bytes, 0x220)
    [BitConverter]::GetBytes([uint32]0x1038).CopyTo($bytes, 0x224)
    [BitConverter]::GetBytes([uint32]0x1100).CopyTo($bytes, 0x230)
    [BitConverter]::GetBytes([uint32]0x1040).CopyTo($bytes, 0x234)
    [Text.Encoding]::ASCII.GetBytes("hm_set_log_callback`0").CopyTo($bytes, 0x240)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function New-RuntimeFixture([string]$Root, [string]$RuntimeIdentifier, $ToolchainIdentity) {
    $native = Join-Path $Root "runtimes/$RuntimeIdentifier/native"
    [IO.Directory]::CreateDirectory($native) | Out-Null
    New-PeFixture (Join-Path $native 'HeyMelody.Filament.Native.dll') $RuntimeIdentifier
    New-PeFixture (Join-Path $native 'hm_filament_probe.exe') $RuntimeIdentifier
    $textPaths = @('materials/gltfio.uberz', 'licenses/Filament/third_party/vkmemalloc/LICENSE',
        'licenses/Filament/third_party/smol-v/LICENSE', 'licenses/Filament/third_party/spirv-headers/LICENSE',
        'licenses/Filament/libs/bluevk/LICENSE')
    foreach ($relative in $textPaths) {
        $path = Join-Path $native $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, 'Offline cache test fixture; not an executable runtime.')
    }
    $paths = @('HeyMelody.Filament.Native.dll', 'hm_filament_probe.exe') + $textPaths
    $files = @($paths | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $native $_)).Hash.ToLowerInvariant() }
    })
    $materialHash = ($files | Where-Object path -CEQ 'materials/gltfio.uberz').sha256
    Write-Json (Join-Path $native 'filament-runtime.json') ([ordered]@{
        schemaVersion = 2; abiVersion = 2; rid = $RuntimeIdentifier
        filamentCommit = 'ea31afeaf66339e7414aa648aa76c7e61adbde28'
        supportedBackends = @('Vulkan', 'OpenGL'); defaultBackend = 'Vulkan'
        logCallbackExport = 'hm_set_log_callback'
        materials = [ordered]@{ path = 'materials/gltfio.uberz'; sha256 = $materialHash; supportedBackends = @('Vulkan', 'OpenGL') }
        files = $files
    })
    Write-Json (Join-Path $Root "build-receipt-$RuntimeIdentifier.json") ([ordered]@{
        fixture = $true; rid = $RuntimeIdentifier
        filamentCommit = 'ea31afeaf66339e7414aa648aa76c7e61adbde28'; toolchainIdentity = $ToolchainIdentity
    })
}

function New-TestBuilder($State) {
    $maker = ${function:New-RuntimeFixture}
    return {
        param($BuildRoot, $RuntimeIdentifier, $ToolchainIdentityFile)
        $State.Builds++
        if ($State.BuildFailure) { throw 'Synthetic native compiler failure.' }
        & $maker $BuildRoot $RuntimeIdentifier ([IO.File]::ReadAllText($ToolchainIdentityFile) | ConvertFrom-Json)
    }.GetNewClosure()
}

function Compress-Fixture([string]$Directory, [string]$ZipPath) {
    [IO.Compression.ZipFile]::CreateFromDirectory($Directory, $ZipPath)
}

function New-Artifact([long]$Id, [string]$Name, [long]$RunId, [bool]$Expired = $false) {
    [pscustomobject]@{
        id = $Id; name = $Name; expired = $Expired
        created_at = [DateTime]::UtcNow.AddMinutes(-$Id).ToString('o')
        expires_at = [DateTime]::UtcNow.AddDays($(if ($Expired) { -1 } else { 30 })).ToString('o')
        archive_download_url = "https://fixture.invalid/artifacts/$Id/zip"
        workflow_run = [pscustomobject]@{ id = $RunId; repository_id = 42; head_repository_id = 42; head_branch = 'feature/cache'; head_sha = ('a' * 40) }
    }
}

function New-Run([long]$Id, [string]$Event = 'push', [string]$Path = '.github/workflows/windows-build.yml', [string]$Conclusion = 'success') {
    [pscustomobject]@{
        id = $Id; event = $Event; path = $Path; status = 'completed'; conclusion = $Conclusion
        head_branch = 'feature/cache'; head_sha = ('a' * 40)
        repository = [pscustomobject]@{ id = 42; full_name = 'fixture/project' }
        head_repository = [pscustomobject]@{ id = 42; full_name = 'fixture/project' }
    }
}

function New-RestFixture($Pages, $Runs, $State) {
    return {
        param($Uri, $TimeoutSeconds)
        $State.Requests.Add([string]$Uri)
        if ($State.ApiFailure) { throw $State.ApiFailure }
        if ($Uri -match '/actions/artifacts\?') {
            $pageNumber = if ($Uri -match '(?:[?&])page=(\d+)') { [int]$Matches[1] } else { 1 }
            $artifacts = if ($Pages.ContainsKey($pageNumber)) { @($Pages[$pageNumber]) } else { @() }
            return [pscustomobject]@{ total_count = @($Pages.Values | ForEach-Object { $_ }).Count; artifacts = $artifacts }
        }
        if ($Uri -match '/actions/runs/(\d+)') { return $Runs[[long]$Matches[1]] }
        throw "Unexpected fixture API URI: $Uri"
    }.GetNewClosure()
}

function New-TestDownload([string]$ZipPath, $State) {
    return {
        param($Uri, $Destination, $TimeoutSeconds)
        $State.Downloads++
        if ($State.DownloadFailure) { throw $State.DownloadFailure }
        [IO.File]::Copy($ZipPath, $Destination, $true)
    }.GetNewClosure()
}

function New-TestState {
    [pscustomobject]@{ Builds = 0; Downloads = 0; Requests = [Collections.Generic.List[string]]::new(); BuildFailure = $false; ApiFailure = $null; DownloadFailure = $null }
}

function Invoke-LocalHttpFixture([string]$Kind, [string]$Mode) {
    # TcpListener avoids HTTP.sys permissions. The shared state proves that body
    # cancellation tests reached response reading rather than a connection error.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $state = [hashtable]::Synchronized(@{ Ready = $false; Accepted = $false; HeadersSent = $false; BodyBytesSent = 0; Error = $null })
    $job = Start-ThreadJob -ArgumentList $listener, $state, $Mode -ScriptBlock {
        param($Listener, $State, $Mode)
        $client = $null
        try {
            $State.Ready = $true
            $client = $Listener.AcceptTcpClient()
            $State.Accepted = $true
            $stream = $client.GetStream()
            $requestBytes = [byte[]]::new(8192)
            [void]$stream.Read($requestBytes, 0, $requestBytes.Length)
            if ($Mode -eq 'delayed-headers') { Start-Sleep -Milliseconds 1800 }
            $body = [Text.Encoding]::UTF8.GetBytes('{"ok":123}')
            $headers = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: $($body.Length)`r`nContent-Type: application/json`r`nConnection: close`r`n`r`n")
            $stream.Write($headers, 0, $headers.Length)
            $stream.Flush()
            $State.HeadersSent = $true
            if ($Mode -in @('delayed-body', 'drip-body')) {
                foreach ($index in 0..($body.Length - 1)) {
                    $stream.WriteByte($body[$index]); $stream.Flush()
                    $State.BodyBytesSent++
                    if ($Mode -eq 'delayed-body' -and $index -eq 0) { Start-Sleep -Milliseconds 1800 }
                    elseif ($Mode -eq 'drip-body') { Start-Sleep -Milliseconds 300 }
                }
            } else {
                $stream.Write($body, 0, $body.Length); $stream.Flush()
                $State.BodyBytesSent = $body.Length
            }
        } catch { $State.Error = $_.Exception.Message }
        finally { if ($null -ne $client) { $client.Dispose() }; $Listener.Stop() }
    }
    $savedGhToken = $env:GH_TOKEN
    $savedGithubToken = $env:GITHUB_TOKEN
    $clock = [Diagnostics.Stopwatch]::new()
    $caught = $null
    $value = $null
    try {
        [Environment]::SetEnvironmentVariable('GH_TOKEN', $null, 'Process')
        [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', $null, 'Process')
        for ($attempt = 0; -not $state.Ready -and $attempt -lt 200; $attempt++) { Start-Sleep -Milliseconds 10 }
        Assert-True $state.Ready 'Local HTTP fixture server did not start'
        $module = Get-Module FilamentArtifactCache
        $uri = "http://127.0.0.1:$port/cache-fixture"
        $clock.Start()
        try {
            if ($Kind -eq 'request') {
                $value = & $module { param($Uri) Invoke-FilamentDefaultRequest $Uri 1 } $uri
            } else {
                $destination = Join-Path $scratch ("http-$Mode-$([Guid]::NewGuid().ToString('N')).bin")
                & $module { param($Uri, $Destination) Invoke-FilamentDefaultDownload $Uri $Destination 1 } $uri $destination
                $value = [IO.File]::ReadAllText($destination)
            }
        } catch { $caught = $_.Exception }
        $clock.Stop()
        $witness = [pscustomobject]@{ Accepted = $state.Accepted; HeadersSent = $state.HeadersSent; BodyBytesSent = $state.BodyBytesSent }
    } finally {
        [Environment]::SetEnvironmentVariable('GH_TOKEN', $savedGhToken, 'Process')
        [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', $savedGithubToken, 'Process')
        $listener.Stop()
        $job | Wait-Job -Timeout 3 | Receive-Job -ErrorAction SilentlyContinue | Out-Null
        $job | Remove-Job -Force
    }
    $canceled = $false
    $exceptionTypes = [Collections.Generic.List[string]]::new()
    for ($exception = $caught; $null -ne $exception; $exception = $exception.InnerException) {
        $exceptionTypes.Add($exception.GetType().Name)
        if ($exception -is [OperationCanceledException]) { $canceled = $true }
    }
    [pscustomobject]@{ Exception = $caught; Canceled = $canceled; ExceptionTypes = @($exceptionTypes); ElapsedSeconds = $clock.Elapsed.TotalSeconds; Witness = $witness; Value = $value }
}

$source = Join-Path $scratch 'source'
$toolchain = [ordered]@{
    msvc = '14.44.35207'; windowsSdk = '10.0.26100.0'; cmake = '4.0.2'; ninja = '1.12.1'; python = '3.12.10'
}
$verify = {
    param($RuntimeDirectory, $RuntimeIdentifier)
    & $verifier -Directory $RuntimeDirectory -RuntimeIdentifier $RuntimeIdentifier -RequireCurrent
}.GetNewClosure()

try {
    New-SourceFixture $source
    $fingerprint = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-x64 -ToolchainIdentity $toolchain

    Invoke-Check 'Current runtime requires the explicit log callback declaration' {
        $root = Join-Path $scratch 'missing-log-declaration'
        New-RuntimeFixture $root win-x64 $toolchain
        $path = Join-Path $root 'runtimes/win-x64/native/filament-runtime.json'
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $manifest.PSObject.Properties.Remove('logCallbackExport')
        Write-Json $path $manifest
        $message = ''
        try { & $verifier -Directory (Split-Path -Parent $path) -RuntimeIdentifier win-x64 -RequireCurrent | Out-Null }
        catch { $message = $_.Exception.Message }
        Assert-True ($message -like '*required native log callback export*') 'Missing logging declaration was not rejected'
    }

    Invoke-Check 'Log callback declaration cannot replace the actual PE export' {
        $root = Join-Path $scratch 'missing-log-export'
        New-RuntimeFixture $root win-arm64 $toolchain
        $native = Join-Path $root 'runtimes/win-arm64/native'
        $library = Join-Path $native 'HeyMelody.Filament.Native.dll'
        $bytes = [IO.File]::ReadAllBytes($library)
        $bytes[0x240] = [byte][char]'x'
        [IO.File]::WriteAllBytes($library, $bytes)
        $path = Join-Path $native 'filament-runtime.json'
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        ($manifest.files | Where-Object path -CEQ 'HeyMelody.Filament.Native.dll').sha256 = (Get-FileHash -LiteralPath $library).Hash.ToLowerInvariant()
        Write-Json $path $manifest
        $message = ''
        try { & $verifier -Directory $native -RuntimeIdentifier win-arm64 -RequireCurrent | Out-Null }
        catch { $message = $_.Exception.Message }
        Assert-True ($message -like '*does not export hm_set_log_callback*') 'A renamed export passed the strict verifier'
    }

    Invoke-Check 'Fingerprint is deterministic and independent of checkout/output location' {
        $other = Join-Path $scratch 'another-checkout'
        Copy-Item -LiteralPath $source -Destination $other -Recurse
        $again = Get-FilamentCacheFingerprint -RepoRoot $other -RuntimeIdentifier win-x64 -ToolchainIdentity $toolchain
        Assert-Equal $again.CacheKey $fingerprint.CacheKey 'Identical native inputs should share a cache key'
    }

    Invoke-Check 'Managed and documentation changes retain native cache identity' {
        [IO.File]::AppendAllText((Join-Path $source 'src/Managed/Widget.cs'), '// change')
        [IO.File]::AppendAllText((Join-Path $source 'docs/usage.md'), 'changed')
        $again = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-x64 -ToolchainIdentity $toolchain
        Assert-Equal $again.CacheKey $fingerprint.CacheKey 'Non-native changes should reuse native output'
    }

    foreach ($case in @(
        @{ Name = 'Native bridge'; Path = 'native/HeyMelody.Filament.Native/src/bridge.cpp'; Content = 'int changed = 2;' },
        @{ Name = 'Native build script'; Path = 'tools/filament/Build-FilamentNative.ps1'; Content = '# changed build' },
        @{ Name = 'Toolchain selection script'; Path = 'tools/filament/FilamentToolchain.ps1'; Content = '# changed toolchain' }
    )) {
        Invoke-Check "$($case.Name) changes invalidate native cache identity" {
            $path = Join-Path $source $case.Path
            $original = [IO.File]::ReadAllBytes($path)
            try {
                [IO.File]::WriteAllText($path, $case.Content)
                $again = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-x64 -ToolchainIdentity $toolchain
                Assert-True ($again.CacheKey -cne $fingerprint.CacheKey) 'Native input mutation must miss'
            } finally { [IO.File]::WriteAllBytes($path, $original) }
        }
    }

    foreach ($field in @('commit', 'abiVersion')) {
        Invoke-Check "Filament lock $field changes invalidate native cache identity" {
            $path = Join-Path $source 'tools/filament/filament.lock.json'
            $original = [IO.File]::ReadAllBytes($path)
            try {
                $lock = [IO.File]::ReadAllText($path) | ConvertFrom-Json
                if ($field -eq 'commit') { $lock.commit = 'b' * 40 } else { $lock.abiVersion++ }
                Write-Json $path $lock
                $again = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-x64 -ToolchainIdentity $toolchain
                Assert-True ($again.CacheKey -cne $fingerprint.CacheKey) 'Lock mutation must miss'
            } finally { [IO.File]::WriteAllBytes($path, $original) }
        }
    }

    Invoke-Check 'RID and actual toolchain changes invalidate native cache identity' {
        $arm = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-arm64 -ToolchainIdentity $toolchain
        Assert-True ($arm.CacheKey -cne $fingerprint.CacheKey) 'Target architectures must not share a cache'
        $changed = [ordered]@{}; foreach ($key in $toolchain.Keys) { $changed[$key] = $toolchain[$key] }
        $changed.msvc = '14.45.1'
        $again = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-x64 -ToolchainIdentity $changed
        Assert-True ($again.CacheKey -cne $fingerprint.CacheKey) 'Toolchain changes must miss'
    }

    Invoke-Check 'Toolchain field ordering is stable while every selected compiler tool participates in identity' {
        $reordered = [ordered]@{}
        foreach ($key in @($toolchain.Keys)[($toolchain.Count - 1)..0]) { $reordered[$key] = $toolchain[$key] }
        $again = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-x64 -ToolchainIdentity $reordered
        Assert-Equal $again.CacheKey $fingerprint.CacheKey 'Object property order must not invalidate an identical toolchain'
        foreach ($tool in @('windowsSdk', 'cmake', 'ninja', 'python')) {
            $changed = [ordered]@{}; foreach ($key in $toolchain.Keys) { $changed[$key] = $toolchain[$key] }
            $changed[$tool] += '-changed'
            $again = Get-FilamentCacheFingerprint -RepoRoot $source -RuntimeIdentifier win-x64 -ToolchainIdentity $changed
            Assert-True ($again.CacheKey -cne $fingerprint.CacheKey) "$tool version must participate in native identity"
        }
    }

    foreach ($event in @('push', 'workflow_dispatch')) {
        Invoke-Check "$event publishes trusted native cache and main application packages for seven days" {
            $policy = Get-FilamentArtifactPolicy -EventName $event -GitRef refs/heads/main
            Assert-True $policy.CachePublishEligible 'Trusted builds should publish native cache'
            Assert-True $policy.PackagePublishEligible 'Trusted builds should publish application packages'
            Assert-Equal $policy.PackageRetentionDays 7 'Main package retention'
            Assert-Equal $policy.ReceiptRetentionDays 1 'Receipt retention'
        }
    }

    foreach ($ref in @('refs/heads/feature/cache', 'refs/tags/v0.9.2', 'refs/heads/main-feature')) {
        Invoke-Check "Non-main ref $ref retains packages for one day" {
            $policy = Get-FilamentArtifactPolicy -EventName push -GitRef $ref
            Assert-True $policy.PackagePublishEligible 'Non-main push still publishes its package'
            Assert-Equal $policy.PackageRetentionDays 1 'Only the exact main branch gets seven days'
        }
    }

    foreach ($event in @('pull_request', 'pull_request_target', 'schedule')) {
        Invoke-Check "$event cannot publish trusted cache or duplicate packages" {
            $policy = Get-FilamentArtifactPolicy -EventName $event -GitRef refs/heads/main
            Assert-True (-not $policy.CachePublishEligible) 'Untrusted/unsupported event cannot populate trusted cache'
            Assert-True (-not $policy.PackagePublishEligible) 'PR does not publish application packages'
            Assert-Equal $policy.ReceiptRetentionDays 1 'Receipt retention'
        }
    }

    Invoke-Check 'Candidate lookup pages, rejects untrusted sources, accepts failed multi-platform runs, and caps at three' {
        $state = New-TestState
        $firstPage = @(1001..1100 | ForEach-Object { New-Artifact $_ $fingerprint.CacheKey $_ $true })
        $secondPage = @(1..7 | ForEach-Object { New-Artifact $_ $fingerprint.CacheKey (100 + $_) })
        $runs = @{}
        foreach ($id in 101..107) { $runs[[long]$id] = New-Run $id }
        $runs[101L] = New-Run 101 -Conclusion failure
        $runs[102L] = New-Run 102 -Event pull_request
        $runs[103L].head_repository.full_name = 'outsider/fork'
        $runs[104L] = New-Run 104 -Path '.github/workflows/unrelated.yml'
        $runs[105L] = New-Run 105 -Event workflow_dispatch
        $runs[106L] = New-Run 106 -Path '.github/workflows/filament-build.yml' -Conclusion failure
        $request = New-RestFixture @{ 1 = $firstPage; 2 = $secondPage } $runs $state
        $candidates = @(Get-FilamentCacheCandidates -Repository fixture/project -CacheKey $fingerprint.CacheKey -Request $request)
        Assert-Equal $candidates.Count 3 'Only three legal candidates should be attempted'
        Assert-Equal (($candidates | ForEach-Object { $_.Artifact.id }) -join ',') '1,5,6' 'Candidate trust and newest-first ordering'
        Assert-True (@($state.Requests | Where-Object { $_ -match '[?&]page=2(?:&|$)' }).Count -eq 1) 'Lookup must follow the second page'
        Assert-Equal $candidates[0].Run.conclusion failure 'Cache reuse must not depend on whole-workflow success'
    }

    $cold = $null
    Invoke-Check 'A cold miss invokes the builder, verifies and packages only the complete native runtime' {
        $state = New-TestState
        $request = New-RestFixture @{ 1 = @() } @{} $state
        $script:cold = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'cold') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/main -Request $request -Build (New-TestBuilder $state) -Verify $verify
        Assert-Equal $cold.CacheStatus rebuilt 'Cold cache status'
        Assert-Equal $state.Builds 1 'A miss should build exactly once'
        Assert-Equal $state.Downloads 0 'A miss should not download an artifact'
        Assert-True (Test-Path -LiteralPath (Join-Path $cold.RuntimeRoot 'runtimes/win-x64/native/filament-runtime.json')) 'Published runtime root should have the Build-Client layout'
        Assert-True (Test-Path -LiteralPath (Join-Path $cold.CacheDirectory 'filament-cache.json')) 'The cache must bind output to native build inputs'
        Assert-True (Test-Path -LiteralPath (Join-Path $cold.CacheDirectory 'build-receipt-win-x64.json')) 'Build receipt is retained'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $cold.CacheDirectory 'expected-toolchain.json'))) 'Build scratch must not enter the reusable payload'
    }
    if ($null -eq $cold) { throw 'Cold fixture setup failed; cache restore tests cannot continue.' }
    $goodZip = Join-Path $scratch 'valid-runtime.zip'
    Compress-Fixture $cold.CacheDirectory $goodZip

    Invoke-Check 'A warm PR consumes trusted cache without invoking builder or publishing packages/cache' {
        $state = New-TestState
        $request = New-RestFixture @{ 1 = @(New-Artifact 1 $fingerprint.CacheKey 101) } @{ 101L = (New-Run 101 -Conclusion failure) } $state
        $warm = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'warm') -ToolchainIdentity $toolchain -Repository fixture/project -EventName pull_request -GitRef refs/pull/4/merge -Request $request -Download (New-TestDownload $goodZip $state) -Build (New-TestBuilder $state) -Verify $verify
        Assert-Equal $warm.CacheStatus hit 'A valid artifact should be reused'
        Assert-Equal $state.Builds 0 'A warm cache must skip the native builder'
        Assert-Equal $state.Downloads 1 'One valid artifact download is sufficient'
        Assert-True (-not $warm.CachePublishEligible) 'PR must not seed the trusted cache pool'
        Assert-True (-not $warm.PackagePublishEligible) 'PR must not duplicate package uploads'
        Assert-Equal $warm.SourceRunId 101L 'Provenance should identify the producer run'
    }

    Invoke-Check 'Force rebuild skips all cache lookup/download work' {
        $state = New-TestState
        $request = New-RestFixture @{ 1 = @(New-Artifact 1 $fingerprint.CacheKey 101) } @{ 101L = (New-Run 101) } $state
        $forced = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'forced') -ToolchainIdentity $toolchain -Repository fixture/project -EventName workflow_dispatch -GitRef refs/heads/feature/cache -Request $request -Download (New-TestDownload $goodZip $state) -Build (New-TestBuilder $state) -Verify $verify -ForceRebuild
        Assert-Equal $forced.CacheStatus forced 'Manual rebuild status'
        Assert-Equal $state.Builds 1 'Forced build invokes builder once'
        Assert-Equal $state.Requests.Count 0 'Forced build must bypass candidate API calls'
        Assert-Equal $state.Downloads 0 'Forced build must not download cached output'
    }

    Invoke-Check 'ARM64 cold and warm paths use independent verified runtime payloads' {
        $state = New-TestState
        $emptyRequest = New-RestFixture @{ 1 = @() } @{} $state
        $armCold = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-arm64 -OutputRoot (Join-Path $scratch 'arm-cold') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $emptyRequest -Build (New-TestBuilder $state) -Verify $verify
        Assert-Equal $state.Builds 1 'ARM64 cold path should build once'
        Assert-True ($armCold.CacheKey -cne $cold.CacheKey) 'ARM64 cannot consume an x64 cache'
        $armZip = Join-Path $scratch 'valid-arm64.zip'
        Compress-Fixture $armCold.CacheDirectory $armZip
        $state = New-TestState
        $request = New-RestFixture @{ 1 = @(New-Artifact 1 $armCold.CacheKey 101) } @{ 101L = (New-Run 101) } $state
        $armWarm = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-arm64 -OutputRoot (Join-Path $scratch 'arm-warm') -ToolchainIdentity $toolchain -Repository fixture/project -EventName workflow_dispatch -GitRef refs/heads/feature/cache -Request $request -Download (New-TestDownload $armZip $state) -Build (New-TestBuilder $state) -Verify $verify
        Assert-Equal $armWarm.CacheStatus hit 'ARM64 verified payload should be reused'
        Assert-Equal $state.Builds 0 'ARM64 warm path should not compile'
        Assert-True $armWarm.CachePublishEligible 'A trusted warm run should refresh the cache artifact'
        & $verify (Join-Path $armWarm.RuntimeRoot 'runtimes/win-arm64/native') win-arm64 | Out-Null
    }

    # These variants are downloaded through the real safe extraction, identity and
    # runtime verifier paths. Only the compiler and network are simulated.
    $zipVariants = @{ valid = $goodZip }
    foreach ($variant in @('identity', 'abi', 'hash', 'architecture', 'undeclared-file', 'missing-build-receipt', 'receipt-rid', 'receipt-commit', 'receipt-toolchain')) {
        $variantRoot = Join-Path $scratch "variant-$variant"
        Copy-Item -LiteralPath $cold.CacheDirectory -Destination $variantRoot -Recurse
        $native = Join-Path $variantRoot 'runtimes/win-x64/native'
        $manifestPath = Join-Path $native 'filament-runtime.json'
        switch ($variant) {
            identity {
                $receiptPath = Join-Path $variantRoot 'filament-cache.json'
                $receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
                $receipt.fingerprint = 'f' * 64
                Write-Json $receiptPath $receipt
            }
            abi {
                $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
                $manifest.abiVersion = 1
                Write-Json $manifestPath $manifest
            }
            hash { [IO.File]::AppendAllText((Join-Path $native 'HeyMelody.Filament.Native.dll'), 'tampered') }
            architecture {
                New-PeFixture (Join-Path $native 'HeyMelody.Filament.Native.dll') win-arm64
                $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
                ($manifest.files | Where-Object path -CEQ 'HeyMelody.Filament.Native.dll').sha256 = (Get-FileHash -LiteralPath (Join-Path $native 'HeyMelody.Filament.Native.dll')).Hash.ToLowerInvariant()
                Write-Json $manifestPath $manifest
            }
            undeclared-file { [IO.File]::WriteAllText((Join-Path $variantRoot 'unexpected.dll'), 'not declared') }
            missing-build-receipt { Remove-Item -LiteralPath (Join-Path $variantRoot 'build-receipt-win-x64.json') }
            { $_ -in @('receipt-rid', 'receipt-commit', 'receipt-toolchain') } {
                $buildReceiptPath = Join-Path $variantRoot 'build-receipt-win-x64.json'
                $buildReceipt = [IO.File]::ReadAllText($buildReceiptPath) | ConvertFrom-Json
                if ($variant -eq 'receipt-rid') { $buildReceipt.rid = 'win-arm64' }
                elseif ($variant -eq 'receipt-commit') { $buildReceipt.filamentCommit = 'b' * 40 }
                else { $buildReceipt.toolchainIdentity.msvc = '14.45.1' }
                Write-Json $buildReceiptPath $buildReceipt
            }
        }
        $zipVariants[$variant] = Join-Path $scratch "$variant.zip"
        Compress-Fixture $variantRoot $zipVariants[$variant]
    }
    $zipVariants['corrupt-zip'] = Join-Path $scratch 'corrupt.zip'
    [IO.File]::WriteAllText($zipVariants['corrupt-zip'], 'This is not a ZIP file.')

    $zipVariants['path-traversal'] = Join-Path $scratch 'path-traversal.zip'
    $archive = [IO.Compression.ZipFile]::Open($zipVariants['path-traversal'], [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('../escaped-cache-fixture.txt')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('must never be extracted') } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }

    foreach ($variant in @('identity', 'abi', 'hash', 'architecture', 'undeclared-file', 'missing-build-receipt', 'receipt-rid', 'receipt-commit', 'receipt-toolchain', 'corrupt-zip', 'path-traversal')) {
        Invoke-Check "Invalid cached $variant falls back to a fresh verified native build" {
            $state = New-TestState
            $request = New-RestFixture @{ 1 = @(New-Artifact 1 $fingerprint.CacheKey 101) } @{ 101L = (New-Run 101) } $state
            $result = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch "fallback-$variant") -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Download (New-TestDownload $zipVariants[$variant] $state) -Build (New-TestBuilder $state) -Verify $verify
            Assert-Equal $result.CacheStatus rebuilt 'Rejected cache must lead to a fresh build'
            Assert-Equal $state.Builds 1 'The fallback should build once'
            Assert-Equal $state.Downloads 1 'One invalid candidate should be tried once'
            Assert-True ($result.RuntimeRoot -notmatch '[\\/]restore[\\/]') 'Damaged restore must not become the official runtime root'
            & $verify (Join-Path $result.RuntimeRoot 'runtimes/win-x64/native') win-x64 | Out-Null
            if ($variant -eq 'path-traversal') {
                Assert-Equal @(Get-ChildItem -LiteralPath $scratch -Filter 'escaped-cache-fixture.txt' -Recurse -File).Count 0 'ZIP paths must not escape extraction'
            }
        }
    }

    Invoke-Check 'Artifact digest mismatch rejects an otherwise valid cache and rebuilds' {
        $state = New-TestState
        $artifact = New-Artifact 1 $fingerprint.CacheKey 101
        $artifact | Add-Member -NotePropertyName digest -NotePropertyValue ('sha256:' + ('f' * 64))
        $request = New-RestFixture @{ 1 = @($artifact) } @{ 101L = (New-Run 101) } $state
        $result = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'fallback-digest') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Download (New-TestDownload $goodZip $state) -Build (New-TestBuilder $state) -Verify $verify
        Assert-Equal $result.CacheStatus rebuilt 'GitHub archive digest mismatch must rebuild'
        Assert-Equal $state.Builds 1 'Digest mismatch should build once'
    }

    Invoke-Check 'A rejected newest artifact can fall back to a valid older candidate without compiling' {
        $state = New-TestState
        $request = New-RestFixture @{ 1 = @(New-Artifact 1 $fingerprint.CacheKey 101; New-Artifact 2 $fingerprint.CacheKey 102) } @{ 101L = (New-Run 101); 102L = (New-Run 102) } $state
        $badZip = $zipVariants['corrupt-zip']
        $validZipForDownload = $goodZip
        $download = {
            param($Uri, $Destination, $TimeoutSeconds)
            $state.Downloads++
            [IO.File]::Copy($(if ($Uri -match '/artifacts/1/zip$') { $badZip } else { $validZipForDownload }), $Destination, $true)
        }.GetNewClosure()
        $result = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'older-valid-candidate') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Download $download -Build (New-TestBuilder $state) -Verify $verify
        Assert-Equal $result.CacheStatus hit 'An older valid candidate should still be usable'
        Assert-Equal $result.SourceArtifactId 2L 'The accepted candidate should be reported'
        Assert-Equal $state.Downloads 2 'Newest and second newest candidates should be tried'
        Assert-Equal $state.Builds 0 'A valid older candidate avoids compilation'
    }

    foreach ($failure in @('403', '404', 'timeout', 'expired', 'download-404')) {
        Invoke-Check "Cache $failure is recoverable through a new native build" {
            $state = New-TestState
            if ($failure -eq 'timeout') { $state.ApiFailure = [TimeoutException]::new('Synthetic request timeout.') }
            elseif ($failure -in @('403', '404')) { $state.ApiFailure = [Net.Http.HttpRequestException]::new("Synthetic HTTP $failure") }
            elseif ($failure -eq 'download-404') { $state.DownloadFailure = [Net.Http.HttpRequestException]::new('Synthetic download HTTP 404') }
            $request = New-RestFixture @{ 1 = @(New-Artifact 1 $fingerprint.CacheKey 101 ($failure -eq 'expired')) } @{ 101L = (New-Run 101) } $state
            $result = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch "fallback-$failure") -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Download (New-TestDownload $goodZip $state) -Build (New-TestBuilder $state) -Verify $verify
            Assert-Equal $result.CacheStatus rebuilt 'Cache retrieval failure should not prevent a new build'
            Assert-Equal $state.Builds 1 'The fallback should build once'
            if ($failure -eq 'expired') { Assert-Equal $state.Downloads 0 'Expired artifacts must not be downloaded' }
        }
    }

    Invoke-Check 'Restore budget expiry stops cache work and builds without retrying indefinitely' {
        $state = New-TestState
        $request = {
            param($Uri, $TimeoutSeconds)
            $state.Requests.Add([string]$Uri)
            Start-Sleep -Milliseconds 2100
            return [pscustomobject]@{ artifacts = @() }
        }.GetNewClosure()
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'fallback-budget') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Build (New-TestBuilder $state) -Verify $verify -TimeoutSeconds 2
        $clock.Stop()
        Assert-Equal $result.CacheStatus rebuilt 'Expired cache time budget should rebuild'
        Assert-Equal $state.Builds 1 'Expired cache time budget should build once'
        Assert-Equal $state.Requests.Count 1 'Expired cache time budget should stop retries'
        Assert-True ($clock.Elapsed.TotalSeconds -lt 15) 'The cache budget should not introduce an unbounded wait'
    }

    Invoke-Check 'Only three valid candidate downloads are attempted before building' {
        $state = New-TestState
        $artifacts = @(1..4 | ForEach-Object { New-Artifact $_ $fingerprint.CacheKey (100 + $_) })
        $runs = @{}; foreach ($id in 101..104) { $runs[[long]$id] = New-Run $id }
        $request = New-RestFixture @{ 1 = $artifacts } $runs $state
        $result = Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'three-candidates') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Download (New-TestDownload $zipVariants['corrupt-zip'] $state) -Build (New-TestBuilder $state) -Verify $verify
        Assert-Equal $result.CacheStatus rebuilt 'No valid candidate should build'
        Assert-Equal $state.Downloads 3 'A fourth candidate must not be downloaded'
        Assert-Equal $state.Builds 1 'Exhausted candidates should trigger one build'
    }

    Invoke-Check 'Real native build failures propagate instead of being converted to cache warnings' {
        $state = New-TestState; $state.BuildFailure = $true
        $request = New-RestFixture @{ 1 = @() } @{} $state
        $caught = $null
        try {
            Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'broken-builder') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Build (New-TestBuilder $state) -Verify $verify | Out-Null
        } catch { $caught = $_.Exception.Message }
        Assert-True ($null -ne $caught -and $caught.Contains('Synthetic native compiler failure.')) 'Compiler failure must fail the caller'
        Assert-Equal $state.Builds 1 'Compiler failure must not cause duplicate build attempts'
    }

    Invoke-Check 'A newly compiled invalid runtime fails verification instead of being cached' {
        $state = New-TestState
        $request = New-RestFixture @{ 1 = @() } @{} $state
        $maker = ${function:New-RuntimeFixture}
        $badBuilder = {
            param($BuildRoot, $RuntimeIdentifier, $ToolchainIdentityFile)
            $state.Builds++
            & $maker $BuildRoot $RuntimeIdentifier ([IO.File]::ReadAllText($ToolchainIdentityFile) | ConvertFrom-Json)
            [IO.File]::AppendAllText((Join-Path $BuildRoot "runtimes/$RuntimeIdentifier/native/HeyMelody.Filament.Native.dll"), 'invalid newly-built output')
        }.GetNewClosure()
        $caught = $null
        try {
            Invoke-FilamentRuntimePreparation -RepoRoot $source -RuntimeIdentifier win-x64 -OutputRoot (Join-Path $scratch 'broken-new-runtime') -ToolchainIdentity $toolchain -Repository fixture/project -EventName push -GitRef refs/heads/feature/cache -Request $request -Build $badBuilder -Verify $verify | Out-Null
        } catch { $caught = $_.Exception.Message }
        Assert-True ($null -ne $caught -and $caught.Contains('hash mismatch')) 'Invalid build output must fail the caller'
        Assert-Equal $state.Builds 1 'Invalid new build should not be silently rebuilt'
    }

    foreach ($kind in @('request', 'download')) {
        Invoke-Check "Default HTTP $kind reads a complete response without mock transport" {
            $response = Invoke-LocalHttpFixture $kind complete
            $httpReceipts.Add([ordered]@{ kind = $kind; mode = 'complete'; canceled = $response.Canceled; elapsedSeconds = $response.ElapsedSeconds; witness = $response.Witness; exceptionTypes = $response.ExceptionTypes })
            Assert-True ($null -eq $response.Exception) 'A complete local HTTP response should succeed'
            Assert-True ($response.Witness.Accepted -and $response.Witness.HeadersSent -and $response.Witness.BodyBytesSent -gt 0) 'The server must witness a real request and response'
            if ($kind -eq 'request') { Assert-Equal $response.Value.ok 123 'Default REST must deserialize its actual body' }
            else { Assert-Equal $response.Value '{"ok":123}' 'Default download must preserve actual response bytes' }
        }
        foreach ($mode in @('delayed-headers', 'delayed-body', 'drip-body')) {
            Invoke-Check "Default HTTP $kind cancels $mode within a single total transfer budget" {
                $response = Invoke-LocalHttpFixture $kind $mode
                $httpReceipts.Add([ordered]@{ kind = $kind; mode = $mode; canceled = $response.Canceled; elapsedSeconds = $response.ElapsedSeconds; witness = $response.Witness; exceptionTypes = $response.ExceptionTypes })
                Assert-True $response.Witness.Accepted 'Cancellation must follow an accepted local connection'
                if ($mode -in @('delayed-body', 'drip-body')) {
                    Assert-True ($response.Witness.HeadersSent -and $response.Witness.BodyBytesSent -gt 0) 'Body timeout must be witnessed after headers and a body byte, not during connection'
                }
                Assert-True $response.Canceled "The complete transfer must honor its cancellation budget (exceptions: $($response.ExceptionTypes -join ','); elapsed: $($response.ElapsedSeconds))"
                Assert-True ($response.ElapsedSeconds -lt 4) 'A one-second transfer must not block indefinitely'
            }
        }
    }
} catch {
    $checks.Add([pscustomobject]@{ name = 'Test fixture setup'; passed = $false; error = $_.Exception.Message })
    Write-Warning $_.Exception.Message
} finally {
    $passed = @($checks | Where-Object passed).Count
    $failed = $checks.Count - $passed
    $receipt = [ordered]@{ schemaVersion = 1; kind = 'offline-filament-artifact-cache-checks'; passed = $passed; failed = $failed; checks = @($checks); httpTransfers = @($httpReceipts) }
    Write-Json $OutputPath $receipt
    $prefix = $scratchParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedScratch) -cmatch '^[a-f0-9]{32}$' -and
        (Test-Path -LiteralPath $resolvedScratch -PathType Container) -and
        -not ((Get-Item -LiteralPath $resolvedScratch).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
    Write-Host "Filament artifact cache: $passed passed, $failed failed. Receipt: $OutputPath"
}
if (@($checks | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
