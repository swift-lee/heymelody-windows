Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../ci/ArtifactPolicy.psm1') -Force

function Get-FilamentCacheValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function ConvertTo-FilamentCanonicalJson {
    param([Parameter(Mandatory)]$Value)
    function ConvertTo-OrderedCacheValue($Item) {
        if ($null -eq $Item) { return $null }
        if ($Item -is [Collections.IDictionary] -or $Item.GetType() -eq [Management.Automation.PSCustomObject]) {
            $keys = if ($Item -is [Collections.IDictionary]) { @($Item.Keys | ForEach-Object { [string]$_ }) } else { @($Item.PSObject.Properties | ForEach-Object Name) }
            [Array]::Sort($keys, [StringComparer]::Ordinal)
            $result = [ordered]@{}
            foreach ($key in $keys) { $result[$key] = ConvertTo-OrderedCacheValue (Get-FilamentCacheValue $Item $key) }
            return $result
        }
        if ($Item -is [Collections.IEnumerable] -and $Item -isnot [string]) {
            return ,@($Item | ForEach-Object { ConvertTo-OrderedCacheValue $_ })
        }
        return $Item
    }
    ConvertTo-OrderedCacheValue $Value | ConvertTo-Json -Depth 40 -Compress
}

function Get-FilamentCacheFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
        [Parameter(Mandatory)]$ToolchainIdentity
    )
    $RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
    $native = Join-Path $RepoRoot 'native/HeyMelody.Filament.Native'
    if (-not (Test-Path -LiteralPath $native -PathType Container)) { throw "Missing native source directory: $native" }
    $paths = @(Get-ChildItem -LiteralPath $native -Recurse -File | ForEach-Object FullName)
    $paths += @('tools/filament/filament.lock.json', 'tools/filament/Build-FilamentNative.ps1', 'tools/filament/FilamentToolchain.ps1') |
        ForEach-Object { Join-Path $RepoRoot $_ }
    $fileMap = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing native cache input: $path" }
        $item = Get-Item -LiteralPath $path
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Native cache fingerprint inputs cannot be symbolic links.' }
        $relative = [IO.Path]::GetRelativePath($RepoRoot, $path).Replace('\', '/')
        $fileMap.Add($relative, [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    $files = @($fileMap.Values)
    if ($files.Count -lt 4) { throw 'The native cache fingerprint has no source inputs.' }
    $lock = Get-Content -LiteralPath (Join-Path $RepoRoot 'tools/filament/filament.lock.json') -Raw | ConvertFrom-Json
    $filamentCommit = [string](Get-FilamentCacheValue $lock 'commit')
    if ($filamentCommit -cnotmatch '^[a-f0-9]{40}$') { throw 'The Filament lock must identify a fixed commit.' }
    $inputs = [ordered]@{
        schemaVersion = 1
        runtimeIdentifier = $RuntimeIdentifier
        filamentCommit = $filamentCommit
        configuration = 'Release'
        supportedBackends = @('Vulkan', 'OpenGL')
        files = $files
        toolchain = $ToolchainIdentity
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-FilamentCanonicalJson $inputs))
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    [pscustomobject]@{ CacheKey = "filament-native-v1-$RuntimeIdentifier-$hash"; Fingerprint = $hash; Inputs = $inputs }
}

function Invoke-FilamentHttpGet {
    param([string]$Uri, [int]$TimeoutSeconds, [string]$Destination)
    $token = if ($env:GH_TOKEN) { $env:GH_TOKEN } else { $env:GITHUB_TOKEN }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.MaxAutomaticRedirections = 5
    # HttpClient clears Authorization when following the artifact blob redirect.
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $client.MaxResponseContentBufferSize = 16777216
    $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
    $request.Headers.Accept.ParseAdd('application/vnd.github+json')
    $request.Headers.Add('X-GitHub-Api-Version', '2022-11-28')
    $request.Headers.UserAgent.ParseAdd('HeyMelody-Filament-Cache')
    if ($token) { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token) }
    $response = $null
    try {
        $completion = if ($Destination) { [Net.Http.HttpCompletionOption]::ResponseHeadersRead } else { [Net.Http.HttpCompletionOption]::ResponseContentRead }
        $response = $client.SendAsync($request, $completion, $cancellation.Token).GetAwaiter().GetResult()
        [void]$response.EnsureSuccessStatusCode()
        if (-not $Destination) {
            $body = $response.Content.ReadAsStringAsync($cancellation.Token).GetAwaiter().GetResult()
            $cancellation.Token.ThrowIfCancellationRequested()
            return $body | ConvertFrom-Json
        }
        $length = $response.Content.Headers.ContentLength
        if ($null -ne $length -and $length -gt 536870912) { throw 'Filament cache archive exceeds the download size limit.' }
        $source = $response.Content.ReadAsStreamAsync($cancellation.Token).GetAwaiter().GetResult()
        $destinationStream = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write)
        try {
            $buffer = [byte[]]::new(65536)
            $total = 0L
            while (($read = $source.ReadAsync($buffer, 0, $buffer.Length, $cancellation.Token).GetAwaiter().GetResult()) -gt 0) {
                $cancellation.Token.ThrowIfCancellationRequested()
                $total += $read
                if ($total -gt 536870912) { throw 'Filament cache archive exceeds the download size limit.' }
                $destinationStream.Write($buffer, 0, $read)
            }
        } finally { $destinationStream.Dispose(); $source.Dispose() }
    } finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose(); $cancellation.Dispose(); $client.Dispose()
    }
}

function Invoke-FilamentDefaultRequest {
    param([string]$Uri, [int]$TimeoutSeconds)
    Invoke-FilamentHttpGet -Uri $Uri -TimeoutSeconds $TimeoutSeconds
}

function Invoke-FilamentDefaultDownload {
    param([string]$Uri, [string]$Destination, [int]$TimeoutSeconds)
    Invoke-FilamentHttpGet -Uri $Uri -TimeoutSeconds $TimeoutSeconds -Destination $Destination
}

function Invoke-FilamentCacheOperation {
    param([Parameter(Mandatory)][scriptblock]$Operation, [Parameter(Mandatory)][DateTime]$DeadlineUtc)
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $remaining = ($DeadlineUtc - [DateTime]::UtcNow).TotalSeconds
        if ($remaining -lt 1) { throw [TimeoutException]::new('Filament cache restore exceeded its time budget.') }
        $seconds = [Math]::Max(1, [Math]::Min(30, [Math]::Floor($remaining)))
        try {
            $result = & $Operation ([int]$seconds)
            if ([DateTime]::UtcNow -gt $DeadlineUtc) { throw [TimeoutException]::new('Filament cache restore exceeded its time budget.') }
            return $result
        } catch {
            if ($attempt -eq 1 -or [DateTime]::UtcNow -ge $DeadlineUtc) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}

function Get-FilamentCacheCandidates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
        [Parameter(Mandatory)][string]$CacheKey,
        [scriptblock]$Request = ${function:Invoke-FilamentDefaultRequest},
        [DateTime]$DeadlineUtc = [DateTime]::UtcNow.AddSeconds(180),
        [string]$ApiBaseUrl = 'https://api.github.com'
    )
    $base = $ApiBaseUrl.TrimEnd('/') + "/repos/$Repository/actions"
    $artifacts = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[long]]::new()
    for ($page = 1; ; $page++) {
        $uri = "$base/artifacts?name=$([Uri]::EscapeDataString($CacheKey))&per_page=100&page=$page"
        $response = Invoke-FilamentCacheOperation { param($seconds) & $Request $uri $seconds } $DeadlineUtc
        $batch = @((Get-FilamentCacheValue $response 'artifacts'))
        foreach ($artifact in $batch) {
            $id = Get-FilamentCacheValue $artifact 'id'
            if ($id -isnot [ValueType] -or [long]$id -le 0 -or -not $seen.Add([long]$id)) { continue }
            if ((Get-FilamentCacheValue $artifact 'name') -cne $CacheKey -or (Get-FilamentCacheValue $artifact 'expired') -ne $false) { continue }
            $expires = [DateTime]::MinValue
            $created = [DateTime]::MinValue
            if (-not [DateTime]::TryParse([string](Get-FilamentCacheValue $artifact 'expires_at'), [ref]$expires) -or $expires.ToUniversalTime() -le [DateTime]::UtcNow) { continue }
            if (-not [DateTime]::TryParse([string](Get-FilamentCacheValue $artifact 'created_at'), [ref]$created)) { continue }
            $artifacts.Add($artifact)
        }
        if ($batch.Count -lt 100) { break }
        if ([DateTime]::UtcNow -ge $DeadlineUtc) { throw [TimeoutException]::new('Filament cache candidate lookup exceeded its time budget.') }
    }
    $accepted = 0
    foreach ($artifact in ($artifacts | Sort-Object { [DateTime](Get-FilamentCacheValue $_ 'created_at') } -Descending)) {
        $runId = Get-FilamentCacheValue (Get-FilamentCacheValue $artifact 'workflow_run') 'id'
        if ($runId -isnot [ValueType] -or [long]$runId -le 0) { continue }
        $uri = "$base/runs/$runId"
        $run = Invoke-FilamentCacheOperation { param($seconds) & $Request $uri $seconds } $DeadlineUtc
        $runRepository = Get-FilamentCacheValue (Get-FilamentCacheValue $run 'repository') 'full_name'
        $headRepository = Get-FilamentCacheValue (Get-FilamentCacheValue $run 'head_repository') 'full_name'
        $event = Get-FilamentCacheValue $run 'event'
        $path = Get-FilamentCacheValue $run 'path'
        if ($runRepository -ine $Repository -or $headRepository -ine $Repository -or
            $event -notin @('push', 'workflow_dispatch') -or
            $path -cnotin @('.github/workflows/windows-build.yml', '.github/workflows/filament-build.yml')) { continue }
        # Run conclusion is intentionally irrelevant: another platform can fail later.
        [pscustomobject]@{ Artifact = $artifact; Run = $run }
        $accepted++
        if ($accepted -ge 3) { break }
    }
}

function Expand-FilamentSafeArchive {
    param([Parameter(Mandatory)][string]$ArchivePath, [Parameter(Mandatory)][string]$Destination, [DateTime]$DeadlineUtc)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($zip.Entries.Count -gt 512) { throw 'Filament cache archive contains too many files.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $total = 0L
        foreach ($entry in $zip.Entries) {
            if ([DateTime]::UtcNow -ge $DeadlineUtc) { throw [TimeoutException]::new('Filament cache extraction exceeded its time budget.') }
            $name = $entry.FullName.Replace('\', '/')
            if (-not $name -or $name.StartsWith('/') -or $name.Contains(':') -or
                ($name.Split('/') | Where-Object { $_ -in @('.', '..') }) -or
                (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw 'Unsafe path or symbolic link in Filament cache archive.' }
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Filament cache archive escapes its destination.' }
            if (-not $seen.Add($target)) { throw 'Duplicate path in Filament cache archive.' }
            $total += $entry.Length
            if ($total -gt 536870912) { throw 'Filament cache archive exceeds the extraction size limit.' }
            if ($name.EndsWith('/')) { New-Item -ItemType Directory -Path $target -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            $source = $entry.Open()
            $destinationStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try {
                $buffer = [byte[]]::new(65536)
                while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ([DateTime]::UtcNow -ge $DeadlineUtc) { throw [TimeoutException]::new('Filament cache extraction exceeded its time budget.') }
                    $destinationStream.Write($buffer, 0, $read)
                }
            } finally { $destinationStream.Dispose(); $source.Dispose() }
        }
    } finally { $zip.Dispose() }
}

function Assert-FilamentCachePayload {
    param([string]$Directory, [string]$RuntimeIdentifier, $ExpectedIdentity, [scriptblock]$Verify)
    $receiptPath = Join-Path $Directory 'filament-cache.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf) -or (Get-Item -LiteralPath $receiptPath).Length -gt 1048576) { throw 'Filament cache identity receipt is missing or too large.' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ((Get-FilamentCacheValue $receipt 'schemaVersion') -ne 1 -or
        (Get-FilamentCacheValue $receipt 'cacheKey') -cne $ExpectedIdentity.CacheKey -or
        (Get-FilamentCacheValue $receipt 'fingerprint') -cne $ExpectedIdentity.Fingerprint -or
        (Get-FilamentCacheValue $receipt 'runtimeIdentifier') -cne $RuntimeIdentifier -or
        (ConvertTo-FilamentCanonicalJson (Get-FilamentCacheValue $receipt 'inputs')) -cne (ConvertTo-FilamentCanonicalJson $ExpectedIdentity.Inputs)) { throw 'Filament cache identity does not match the requested native build inputs.' }
    $buildReceipt = Join-Path $Directory "build-receipt-$RuntimeIdentifier.json"
    if (-not (Test-Path -LiteralPath $buildReceipt -PathType Leaf) -or (Get-Item -LiteralPath $buildReceipt).Length -gt 1048576) { throw 'Filament build receipt is missing or too large.' }
    $build = Get-Content -LiteralPath $buildReceipt -Raw | ConvertFrom-Json
    $actualToolchain = Get-FilamentCacheValue $build 'toolchainIdentity'
    if ((Get-FilamentCacheValue $build 'rid') -cne $RuntimeIdentifier -or
        (Get-FilamentCacheValue $build 'filamentCommit') -cne $ExpectedIdentity.Inputs.filamentCommit -or
        $null -eq $actualToolchain -or
        (ConvertTo-FilamentCanonicalJson $actualToolchain) -cne (ConvertTo-FilamentCanonicalJson $ExpectedIdentity.Inputs.toolchain)) { throw 'Filament build receipt does not match the requested RID, commit or actual toolchain.' }
    $runtime = Join-Path $Directory "runtimes/$RuntimeIdentifier/native"
    & $Verify $runtime $RuntimeIdentifier | Out-Null
    $manifest = Get-Content -LiteralPath (Join-Path $runtime 'filament-runtime.json') -Raw | ConvertFrom-Json
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('filament-cache.json', "build-receipt-$RuntimeIdentifier.json", "runtimes/$RuntimeIdentifier/native/filament-runtime.json")) { [void]$allowed.Add($name) }
    foreach ($file in @($manifest.files)) { [void]$allowed.Add("runtimes/$RuntimeIdentifier/native/$($file.path)") }
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Filament cache payload cannot contain symbolic links.' }
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
        if (-not $allowed.Contains($relative)) { throw "Unexpected file in Filament cache payload: $relative" }
    }
    return $runtime
}

function Restore-FilamentArtifactCache {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$CacheKey,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
        [Parameter(Mandatory)]$ExpectedIdentity,
        [Parameter(Mandatory)][string]$DestinationRoot,
        [scriptblock]$Request = ${function:Invoke-FilamentDefaultRequest},
        [scriptblock]$Download = ${function:Invoke-FilamentDefaultDownload},
        [Parameter(Mandatory)][scriptblock]$Verify,
        [ValidateRange(1, 180)][int]$TimeoutSeconds = 180,
        [string]$ApiBaseUrl = 'https://api.github.com'
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $reason = 'No matching unexpired trusted artifact was found.'
    try {
        $candidates = @(Get-FilamentCacheCandidates -Repository $Repository -CacheKey $CacheKey -Request $Request -DeadlineUtc $deadline -ApiBaseUrl $ApiBaseUrl)
        foreach ($candidate in $candidates) {
            if ([DateTime]::UtcNow -ge $deadline) { throw [TimeoutException]::new('Filament cache restore exceeded its time budget.') }
            $id = [long]$candidate.Artifact.id
            $attemptDirectory = Join-Path $DestinationRoot "candidate-$id-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $attemptDirectory -Force | Out-Null
            $archivePath = Join-Path $attemptDirectory 'artifact.zip'
            $payload = Join-Path $attemptDirectory 'payload'
            try {
                $uri = $ApiBaseUrl.TrimEnd('/') + "/repos/$Repository/actions/artifacts/$id/zip"
                Invoke-FilamentCacheOperation { param($seconds) & $Download $uri $archivePath $seconds } $deadline | Out-Null
                $digest = [string](Get-FilamentCacheValue $candidate.Artifact 'digest')
                if ($digest) {
                    if ($digest -cnotmatch '^sha256:([a-f0-9]{64})$' -or
                        (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Matches[1]) { throw 'Filament cache archive digest does not match GitHub metadata.' }
                }
                Expand-FilamentSafeArchive $archivePath $payload $deadline
                $runtime = Assert-FilamentCachePayload $payload $RuntimeIdentifier $ExpectedIdentity $Verify
                if ([DateTime]::UtcNow -gt $deadline) { throw [TimeoutException]::new('Filament cache verification exceeded its time budget.') }
                return [pscustomobject]@{ Hit = $true; RuntimeRoot = $payload; CacheDirectory = $payload; Reason = 'Verified artifact restored.'; SourceArtifactId = $id; SourceRunId = [long]$candidate.Run.id }
            } catch {
                $reason = "Artifact ${id}: $($_.Exception.Message)"
                Write-Warning ($reason.Substring(0, [Math]::Min(800, $reason.Length)))
            }
        }
    } catch { $reason = $_.Exception.Message; Write-Warning ($reason.Substring(0, [Math]::Min(800, $reason.Length))) }
    [pscustomobject]@{ Hit = $false; RuntimeRoot = $null; CacheDirectory = $null; Reason = $reason; SourceArtifactId = $null; SourceRunId = $null }
}

function Invoke-FilamentRuntimePreparation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)]$ToolchainIdentity,
        [string]$Repository = $env:GITHUB_REPOSITORY,
        [string]$EventName = $env:GITHUB_EVENT_NAME,
        [string]$GitRef = $env:GITHUB_REF,
        [switch]$ForceRebuild,
        [scriptblock]$Request = ${function:Invoke-FilamentDefaultRequest},
        [scriptblock]$Download = ${function:Invoke-FilamentDefaultDownload},
        [scriptblock]$Verify,
        [scriptblock]$Build,
        [ValidateRange(1, 180)][int]$TimeoutSeconds = 180,
        [string]$ApiBaseUrl = 'https://api.github.com'
    )
    $RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
    $OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    if (-not $Verify) {
        $verifier = Join-Path $RepoRoot 'tools/Verify-FilamentRuntime.ps1'
        $Verify = { param($directory, $rid) & $verifier -Directory $directory -RuntimeIdentifier $rid -RequireCurrent }.GetNewClosure()
    }
    if (-not $Build) {
        $builder = Join-Path $RepoRoot 'tools/filament/Build-FilamentNative.ps1'
        $Build = { param($buildRoot, $rid, $identityFile) & $builder -RuntimeIdentifier $rid -OutputRoot $buildRoot -FilamentSource '' -FetchSource -FetchBuildTools -SkipSmoke -ExpectedToolchainIdentityFile $identityFile | Out-Host }.GetNewClosure()
    }
    $identity = Get-FilamentCacheFingerprint -RepoRoot $RepoRoot -RuntimeIdentifier $RuntimeIdentifier -ToolchainIdentity $ToolchainIdentity
    $policy = Get-CiArtifactPolicy -EventName $EventName -GitRef $GitRef
    $restore = [pscustomobject]@{ Hit = $false; Reason = $(if ($ForceRebuild) { 'Manual native rebuild requested.' } else { 'Repository identity is unavailable.' }); SourceArtifactId = $null; SourceRunId = $null }
    if (-not $ForceRebuild -and $Repository) {
        $restore = Restore-FilamentArtifactCache -Repository $Repository -CacheKey $identity.CacheKey -RuntimeIdentifier $RuntimeIdentifier -ExpectedIdentity $identity -DestinationRoot (Join-Path $OutputRoot 'restore') -Request $Request -Download $Download -Verify $Verify -TimeoutSeconds $TimeoutSeconds -ApiBaseUrl $ApiBaseUrl
    }
    if ($restore.Hit) {
        $payload = $restore.CacheDirectory
        $status = 'hit'
    } else {
        Write-Warning "Building Filament locally: $($restore.Reason)"
        # Each fallback gets a fresh source/build root; a damaged restore is never reused.
        $buildRoot = Join-Path $OutputRoot ('build-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
        $identityFile = Join-Path $buildRoot 'expected-toolchain.json'
        $ToolchainIdentity | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $identityFile -Encoding utf8NoBOM
        & $Build $buildRoot $RuntimeIdentifier $identityFile | Out-Host
        $runtime = Join-Path $buildRoot "runtimes/$RuntimeIdentifier/native"
        & $Verify $runtime $RuntimeIdentifier | Out-Null
        $payload = Join-Path $OutputRoot ('cache-payload-' + [Guid]::NewGuid().ToString('N'))
        $payloadRuntime = Join-Path $payload "runtimes/$RuntimeIdentifier/native"
        New-Item -ItemType Directory -Path $payloadRuntime -Force | Out-Null
        $manifest = Get-Content -LiteralPath (Join-Path $runtime 'filament-runtime.json') -Raw | ConvertFrom-Json
        foreach ($relative in @('filament-runtime.json') + @($manifest.files | ForEach-Object path)) {
            $source = Join-Path $runtime $relative
            $target = Join-Path $payloadRuntime $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
        Copy-Item -LiteralPath (Join-Path $buildRoot "build-receipt-$RuntimeIdentifier.json") -Destination $payload
        [ordered]@{
            schemaVersion = 1; cacheKey = $identity.CacheKey; fingerprint = $identity.Fingerprint
            runtimeIdentifier = $RuntimeIdentifier; inputs = $identity.Inputs; createdAtUtc = [DateTime]::UtcNow.ToString('o')
        } | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $payload 'filament-cache.json') -Encoding utf8NoBOM
        Assert-FilamentCachePayload $payload $RuntimeIdentifier $identity $Verify | Out-Null
        $status = if ($ForceRebuild) { 'forced' } else { 'rebuilt' }
    }
    [pscustomobject]@{
        RuntimeRoot = $payload; CacheKey = $identity.CacheKey; CacheStatus = $status; CacheDirectory = $payload
        CachePublishEligible = $policy.CachePublishEligible; CacheRetentionDays = $policy.CacheRetentionDays
        RestoreReason = $restore.Reason; SourceArtifactId = $restore.SourceArtifactId; SourceRunId = $restore.SourceRunId
        Fingerprint = $identity.Fingerprint; ToolchainIdentity = $ToolchainIdentity
    }
}

Export-ModuleMember -Function ConvertTo-FilamentCanonicalJson, Get-FilamentCacheFingerprint, Get-FilamentCacheCandidates, Restore-FilamentArtifactCache, Invoke-FilamentRuntimePreparation
