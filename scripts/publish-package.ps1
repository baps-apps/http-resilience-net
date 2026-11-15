#!/usr/bin/env pwsh
#Requires -Version 7.0
# Cross-platform script to publish HttpResilience.NET package to GitHub Packages
# Works on Windows, macOS and Linux with PowerShell 7 (pwsh). Windows PowerShell 5.1 is not supported:
# the script uses ?? and three-argument Join-Path, and the #Requires line above refuses 5.1 up front.
# Usage: pwsh scripts/publish-package.ps1 [VERSION] [GITHUB_PAT] [-AllowUnsigned]
#        or: ./scripts/publish-package.ps1 [VERSION] [GITHUB_PAT] [-AllowUnsigned] (if executable)
#
# Features:
# - Refuses to run on a working tree with uncommitted or untracked changes, so the package is built from
#   exactly one commit, and tags that commit
# - Runs every release gate (CONTRIBUTING.md#release-gates) and stops before packing if any fails: whitespace,
#   vulnerable packages, benchmark docs, README links, shipped public API, Native AOT smoke, then the full test
#   suite in Release on the minimum and latest dependency versions
# - Signs and verifies the package (see scripts/README.md#package-signing); refuses to start without a signing
#   certificate unless -AllowUnsigned is passed
# - Automatically deletes and republishes if package version already exists
# - Creates a git tag and GitHub release after successful publish
# - Requires GitHub PAT with 'write:packages', 'delete:packages', and 'public_repo' (or 'repo') permissions

param(
    [string]$Version = "",
    [string]$GitHubPAT = $env:GITHUB_PAT,
    # Publishes without a signature. Consumers that require signed packages will refuse the result.
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"

# Repository root. Git is required: the package is built from one commit, and that commit is tagged.
$RepoRoot = (git -C ($PSScriptRoot ? $PSScriptRoot : (Get-Location).Path) rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RepoRoot)) {
    Write-Host "Error: Not inside a git repository. Publish from a clone of http-resilience-net." -ForegroundColor Red
    exit 1
}
$RepoRoot = $RepoRoot.Trim()

# Untracked files count: the project compiles every .cs file under src/, so an untracked one ships too.
function Get-WorkingTreeChanges {
    # stderr is not captured: a git warning (line endings, safe.directory hints) is not a change.
    $changes = git -C $RepoRoot status --porcelain --untracked-files=all 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: git status failed in $RepoRoot" -ForegroundColor Red
        exit 1
    }
    return $changes
}

function Get-HeadCommit {
    $sha = git -C $RepoRoot rev-parse --verify HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        Write-Host "Error: Could not resolve HEAD; the repository has no commit to publish." -ForegroundColor Red
        exit 1
    }
    return $sha.Trim()
}

$pendingChanges = Get-WorkingTreeChanges
if ($pendingChanges) {
    Write-Host "Error: The working tree has uncommitted or untracked changes. A package must be built from a commit," -ForegroundColor Red
    Write-Host "       so the tag points at exactly what shipped. Commit or stash these first:" -ForegroundColor Red
    $pendingChanges | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    exit 1
}
$CommitSha = Get-HeadCommit

# Configuration (using Join-Path for cross-platform compatibility)
$ProjectPath = Join-Path $RepoRoot "src" "HttpResilience.NET" "HttpResilience.NET.csproj"
$SolutionPath = Join-Path $RepoRoot "HttpResilience.NET.slnx"
$PackageOutput = Join-Path $RepoRoot "nupkgs"
$Namespace = $env:GITHUB_NAMESPACE ?? "baps-apps"
$RepoName = "http-resilience-net"
$PackageName = "HttpResilience.NET"

# The version is the committed <Version> in the .csproj; this script never edits it. A version passed as an
# argument is only a check that the commit is the release you meant.
if (-not (Test-Path $ProjectPath)) {
    Write-Host "Error: Project file not found at $ProjectPath" -ForegroundColor Red
    exit 1
}

$committedVersion = $null
$versionMatch = Select-String -Path $ProjectPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if ($versionMatch -and $versionMatch.Matches.Groups.Count -gt 1) {
    $committedVersion = $versionMatch.Matches.Groups[1].Value.Trim()
}

if ([string]::IsNullOrEmpty($committedVersion)) {
    Write-Host "Error: Could not extract <Version> from $ProjectPath" -ForegroundColor Red
    Write-Host "Add <Version>1.0.0</Version> to the first <PropertyGroup>, commit it, then run this script." -ForegroundColor Yellow
    exit 1
}

if ([string]::IsNullOrEmpty($Version)) {
    $Version = $committedVersion
    Write-Host "Publishing the committed version: $Version" -ForegroundColor Yellow
} elseif ($Version -ne $committedVersion) {
    Write-Host "Error: Version $Version was requested, but the committed .csproj says $committedVersion." -ForegroundColor Red
    Write-Host "       Set <Version>$Version</Version> in HttpResilience.NET.csproj, commit it, then run this script." -ForegroundColor Yellow
    exit 1
}

# Get GitHub PAT from argument or environment variable
if ([string]::IsNullOrEmpty($GitHubPAT)) {
    Write-Host "Error: GitHub Personal Access Token required" -ForegroundColor Red
    Write-Host "Usage: pwsh scripts/publish-package.ps1 [VERSION] [GITHUB_PAT]"
    Write-Host "Or set GITHUB_PAT environment variable"
    Write-Host ""
    Write-Host "Note: Your PAT needs 'write:packages' and 'delete:packages' to publish/overwrite packages,"
    Write-Host "      and 'repo' or 'public_repo' to create GitHub releases."
    exit 1
}

# Signing is decided before anything runs, so a missing certificate does not surface after minutes of gates.
#   NUGET_SIGN_CERT_PATH (+ NUGET_SIGN_CERT_PASSWORD)  a .pfx file, or
#   NUGET_SIGN_CERT_FINGERPRINT                         the SHA-256 fingerprint of a certificate in the user's store
#   NUGET_SIGN_TIMESTAMPER                              RFC 3161 timestamp server (default below)
$signCertPath = $env:NUGET_SIGN_CERT_PATH
$signCertFingerprint = $env:NUGET_SIGN_CERT_FINGERPRINT
$timestamper = $env:NUGET_SIGN_TIMESTAMPER ?? "http://timestamp.digicert.com"
$signingConfigured = -not [string]::IsNullOrEmpty($signCertPath) -or -not [string]::IsNullOrEmpty($signCertFingerprint)

if (-not $signingConfigured -and -not $AllowUnsigned) {
    Write-Host "Error: No signing certificate configured; nothing was run." -ForegroundColor Red
    Write-Host "       Set NUGET_SIGN_CERT_PATH or NUGET_SIGN_CERT_FINGERPRINT (scripts/README.md#package-signing)," -ForegroundColor Yellow
    Write-Host "       or pass -AllowUnsigned to publish an unsigned package." -ForegroundColor Yellow
    exit 1
}

# Python runs the repository's check scripts. -I keeps the current directory and user site-packages off the path.
$Python = Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Python) {
    $Python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $Python) {
    Write-Host "Error: Python 3 is required for the release gates (scripts/check-*.py); nothing was run." -ForegroundColor Red
    exit 1
}

Write-Host "Publishing HttpResilience.NET v$Version from commit $CommitSha to GitHub Packages" -ForegroundColor Green
Write-Host ""

# Step 1: Verify credentials
Write-Host "Step 1: Verifying credentials..." -ForegroundColor Yellow
$sourceUrl = "https://nuget.pkg.github.com/$Namespace/index.json"
Write-Host "Target feed: $sourceUrl" -ForegroundColor Green
Write-Host ""

# Step 2: Release gates (CONTRIBUTING.md#release-gates). Each one stops the script on failure.
Write-Host "Step 2: Running release gates..." -ForegroundColor Yellow

function Invoke-ReleaseGate {
    param(
        [string]$Name,
        [scriptblock]$Command
    )
    Write-Host "  Gate: $Name" -ForegroundColor Yellow
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Release gate failed: $Name; nothing was packed or published" -ForegroundColor Red
        exit 1
    }
}

Push-Location $RepoRoot
try {
    Invoke-ReleaseGate "whitespace" { dotnet format whitespace $SolutionPath --verify-no-changes }

    # Exits 0 whether or not it finds anything, so the report is read instead.
    Write-Host "  Gate: vulnerable packages" -ForegroundColor Yellow
    $vulnerableReport = dotnet list $SolutionPath package --vulnerable --include-transitive 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $vulnerableReport -match "has the following vulnerable packages") {
        Write-Host $vulnerableReport
        Write-Host "Error: Release gate failed: vulnerable packages; nothing was packed or published" -ForegroundColor Red
        exit 1
    }

    # The shipped project only: the test project needs the deprecated Polly v7 stack it detects
    # (Directory.Packages.props, Microsoft.Extensions.Http.Polly).
    Write-Host "  Gate: deprecated packages (shipped project)" -ForegroundColor Yellow
    $deprecatedReport = dotnet list $ProjectPath package --deprecated --include-transitive 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $deprecatedReport -match "has the following deprecated packages") {
        Write-Host $deprecatedReport
        Write-Host "Error: Release gate failed: deprecated packages; nothing was packed or published" -ForegroundColor Red
        exit 1
    }

    Invoke-ReleaseGate "benchmark docs" { & $Python.Source -I (Join-Path $RepoRoot "scripts" "check-benchmark-docs.py") }
    Invoke-ReleaseGate "README links" { & $Python.Source -I (Join-Path $RepoRoot "scripts" "check-readme-links.py") }
    Invoke-ReleaseGate "shipped public API" { & $Python.Source -I (Join-Path $RepoRoot "scripts" "check-public-api-shipped.py") }

    # Publishes and runs the Native AOT binary for this machine. On macOS the test adds Homebrew's lib
    # directory to LIBRARY_PATH itself, which the Homebrew .NET SDK's native link needs.
    $env:HTTPRESILIENCE_AOT_SMOKE = "1"
    try {
        Invoke-ReleaseGate "Native AOT smoke" {
            dotnet test (Join-Path $RepoRoot "tests" "HttpResilience.NET.IntegrationTests") --configuration Release `
                --filter "FullyQualifiedName~NativeAotSmokeTests"
        }
    } finally {
        Remove-Item Env:HTTPRESILIENCE_AOT_SMOKE -ErrorAction SilentlyContinue
    }
} finally {
    Pop-Location
}
Write-Host "Release gates passed" -ForegroundColor Green
Write-Host ""

# Step 3: Test. Release is the suite of record: Debug skips every [ReleaseOnlyFact] (allocation ceilings).
# Builds every project first, so no test assembly is stale. Runs twice: on the minimum dependency versions the
# package declares, which a consumer may resolve, then on the latest, which restores the normal package graph.
Write-Host "Step 3: Running tests (Release) on the minimum dependency versions..." -ForegroundColor Yellow
dotnet test $SolutionPath --configuration Release -p:TestMinimumDependencies=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Tests failed on the minimum dependency versions; nothing was packed or published" -ForegroundColor Red
    exit 1
}

Write-Host "Step 3: Running tests (Release) on the latest dependency versions..." -ForegroundColor Yellow
dotnet test $SolutionPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Tests failed; nothing was packed or published" -ForegroundColor Red
    exit 1
}
Write-Host "Tests passed on minimum and latest dependency versions" -ForegroundColor Green
Write-Host ""

# Step 4: Build. ContinuousIntegrationBuild maps source paths to /_/ (Source Link resolves them to the commit), so
# the package is reproducible and carries no path from the publishing machine. Pack takes the same property.
Write-Host "Step 4: Building project..." -ForegroundColor Yellow
$ReproducibleBuild = "-p:ContinuousIntegrationBuild=true"
dotnet build $ProjectPath --configuration Release $ReproducibleBuild
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "Build completed" -ForegroundColor Green
Write-Host ""

# Step 5: Pack
Write-Host "Step 5: Creating NuGet package..." -ForegroundColor Yellow
if (-not (Test-Path $PackageOutput)) {
    New-Item -ItemType Directory -Path $PackageOutput | Out-Null
}
dotnet pack $ProjectPath --configuration Release --no-build --output $PackageOutput $ReproducibleBuild
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Package creation failed" -ForegroundColor Red
    exit 1
}
Write-Host "Package created" -ForegroundColor Green
Write-Host ""

$actualVersion = $Version

# The tests, build and pack took minutes. Nothing may have changed the tree or moved HEAD meanwhile, or the
# package would no longer match the commit about to be tagged.
$changesAfterPack = Get-WorkingTreeChanges
if ($changesAfterPack) {
    Write-Host "Error: The working tree changed while testing and packing; nothing was published:" -ForegroundColor Red
    $changesAfterPack | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    exit 1
}
$headAfterPack = Get-HeadCommit
if ($headAfterPack -ne $CommitSha) {
    Write-Host "Error: HEAD moved from $CommitSha to $headAfterPack while testing and packing; nothing was published." -ForegroundColor Red
    exit 1
}

$PackageFile = Join-Path $PackageOutput "$PackageName.$actualVersion.nupkg"
if (-not (Test-Path $PackageFile)) {
    Write-Host "Error: Package file not found: $PackageFile" -ForegroundColor Red
    $existingPackages = Get-ChildItem -Path $PackageOutput -Filter "*.nupkg" -ErrorAction SilentlyContinue
    if ($existingPackages) {
        Write-Host "Found package files:" -ForegroundColor Yellow
        $existingPackages | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Yellow }
    }
    exit 1
}

# Step 6: Sign and verify. Without a certificate the script stopped at the start unless -AllowUnsigned was passed.
if ($signingConfigured) {
    Write-Host "Step 6: Signing package..." -ForegroundColor Yellow
    $signArgs = @("nuget", "sign", $PackageFile, "--timestamper", $timestamper, "--overwrite")
    if (-not [string]::IsNullOrEmpty($signCertPath)) {
        $signArgs += @("--certificate-path", $signCertPath)
        if (-not [string]::IsNullOrEmpty($env:NUGET_SIGN_CERT_PASSWORD)) {
            $signArgs += @("--certificate-password", $env:NUGET_SIGN_CERT_PASSWORD)
        }
    } else {
        $signArgs += @("--certificate-fingerprint", $signCertFingerprint)
    }

    & dotnet @signArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Signing failed; nothing was published" -ForegroundColor Red
        exit 1
    }

    dotnet nuget verify $PackageFile --all
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Signature verification failed; nothing was published" -ForegroundColor Red
        exit 1
    }
    Write-Host "Package signed and verified" -ForegroundColor Green
} else {
    Write-Host "Step 6: Signing skipped (-AllowUnsigned): the package is UNSIGNED." -ForegroundColor Yellow
    Write-Host "  Consumers with signatureValidationMode=require will refuse it." -ForegroundColor Yellow
}
Write-Host ""

Write-Host "Step 7: Publishing package to GitHub Packages..." -ForegroundColor Yellow

function Test-PackageVersion {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$Token,
        [string]$OrgName
    )
    try {
        $headers = @{
            "Authorization" = "Bearer $Token"
            "Accept"        = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $versionsUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions"
        $versionsResponse = Invoke-RestMethod -Uri $versionsUrl -Method Get -Headers $headers -ErrorAction Stop
        $existingVersion = $versionsResponse | Where-Object { $_.name -eq $PackageVersion } | Select-Object -First 1
        if ($existingVersion) {
            return @{ Exists = $true; VersionId = $existingVersion.id }
        } else {
            return @{ Exists = $false; VersionId = $null }
        }
    } catch {
        return @{ Exists = $null; VersionId = $null }
    }
}

function Remove-PackageVersion {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$Token,
        [string]$OrgName,
        [string]$VersionId
    )
    try {
        $headers = @{
            "Authorization" = "Bearer $Token"
            "Accept"        = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $deleteUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions/$VersionId"
        Invoke-RestMethod -Uri $deleteUrl -Method Delete -Headers $headers -ErrorAction Stop
        Write-Host "  Package version deleted successfully" -ForegroundColor Green
        return $true
    } catch {
        Write-Host "  Could not delete package via API: $_" -ForegroundColor Yellow
        return $false
    }
}

$versionCheck = Test-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace

if ($versionCheck.Exists -eq $true) {
    Write-Host "  Package version $actualVersion already exists, deleting to override..." -ForegroundColor Yellow
    $deleted = Remove-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace -VersionId $versionCheck.VersionId
    if (-not $deleted) {
        Write-Host "Error: Could not delete existing package version" -ForegroundColor Red
        Write-Host "  1. Go to https://github.com/$Namespace/$RepoName/packages" -ForegroundColor Yellow
        Write-Host "  2. Select $PackageName, delete version $actualVersion, then run this script again" -ForegroundColor Yellow
        exit 1
    }
    Start-Sleep -Seconds 2
    Write-Host "  Publishing new version..." -ForegroundColor Yellow
} elseif ($versionCheck.Exists -eq $false) {
    Write-Host "  Package version $actualVersion does not exist, creating new version..." -ForegroundColor Green
} else {
    Write-Host "  Publishing package (will handle errors if version exists)..." -ForegroundColor Yellow
}

dotnet nuget push $PackageFile `
    --api-key $GitHubPAT `
    --source $sourceUrl

if ($LASTEXITCODE -ne 0) {
    $pushOutput = dotnet nuget push $PackageFile --api-key $GitHubPAT --source $sourceUrl 2>&1 | Out-String
    $pushOutputLower = $pushOutput.ToLower()
    $packageExists = $pushOutputLower -match "already exists" -or $pushOutputLower -match "conflict" -or $pushOutputLower -match "409" -or $pushOutputLower -match "package.*exist"

    if ($packageExists) {
        Write-Host "  Package version still exists, attempting to delete and republish..." -ForegroundColor Yellow
        $versionCheck = Test-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace
        if ($versionCheck.Exists -eq $true) {
            $deleted = Remove-PackageVersion -PackageName $PackageName -PackageVersion $actualVersion -Token $GitHubPAT -OrgName $Namespace -VersionId $versionCheck.VersionId
            if ($deleted) {
                Start-Sleep -Seconds 2
                dotnet nuget push $PackageFile --api-key $GitHubPAT --source $sourceUrl
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Error: Package publish failed after deletion" -ForegroundColor Red
                    exit 1
                }
            } else {
                Write-Host "Error: Could not delete existing package version" -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host "Error: Package publish failed" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "Error: Package publish failed" -ForegroundColor Red
        Write-Host "Error details: $pushOutput" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "Package published successfully!" -ForegroundColor Green
Write-Host ""

# Step 8: Create git tag and GitHub release
Write-Host "Step 8: Creating git tag and GitHub release..." -ForegroundColor Yellow
$tagName = "v$actualVersion"

function Get-ChangelogSection {
    param(
        [string]$Version,
        [string]$ChangelogPath
    )
    if (-not (Test-Path $ChangelogPath)) {
        return $null
    }

    # Escape regex metacharacters in version
    $escapedVersion = [Regex]::Escape($Version)
    # Match a top-level "## ..." heading containing the version (with or without v-prefix, optional [link]).
    # Stops at the next top-level "## " heading or EOF.
    $content = Get-Content $ChangelogPath -Raw
    $pattern = '(?ms)^##\s+\[?v?' + $escapedVersion + '\]?[^\r\n]*\r?\n(.*?)(?=^##\s|\z)'
    $match = [Regex]::Match($content, $pattern)
    if (-not $match.Success) {
        return $null
    }

    $section = $match.Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($section)) {
        return $null
    }
    return $section
}

# Delete existing tag (local + remote) if present, then create and push fresh
$tagExists = git tag -l $tagName 2>&1
if (-not [string]::IsNullOrWhiteSpace($tagExists)) {
    Write-Host "  Tag $tagName already exists, deleting to recreate..." -ForegroundColor Yellow
    git tag -d $tagName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Local tag $tagName deleted" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not delete local tag $tagName" -ForegroundColor Yellow
    }
    git push origin ":refs/tags/$tagName" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Remote tag $tagName deleted" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not delete remote tag $tagName (may not exist on origin)" -ForegroundColor Yellow
    }
}

# The commit the package was built from, not whatever HEAD is now.
git tag $tagName $CommitSha
if ($LASTEXITCODE -eq 0) {
    git push origin $tagName
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Git tag $tagName pushed to origin" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not push tag $tagName to origin" -ForegroundColor Yellow
    }
} else {
    Write-Host "  Warning: Could not create git tag $tagName" -ForegroundColor Yellow
}

# Create GitHub release (requires 'repo' or 'public_repo' scope on the PAT)
try {
    $headers = @{
        "Authorization"        = "Bearer $GitHubPAT"
        "Accept"               = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }

    # Delete ALL existing releases for this tag, including drafts, then recreate.
    # Deleting the remote tag above converts its published release into an orphan
    # draft, and the by-tag endpoint (releases/tags/{tag}) never returns drafts —
    # so list all releases and filter by tag_name instead.
    try {
        $allReleases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Namespace/$RepoName/releases?per_page=100" `
            -Method Get -Headers $headers -ErrorAction Stop
        $staleReleases = @($allReleases | Where-Object { $_.tag_name -eq $tagName })
        foreach ($stale in $staleReleases) {
            $kind = if ($stale.draft) { "draft" } else { "release" }
            Write-Host "  Existing $kind for $tagName found (id $($stale.id)), deleting to recreate..." -ForegroundColor Yellow
            try {
                Invoke-RestMethod -Uri "https://api.github.com/repos/$Namespace/$RepoName/releases/$($stale.id)" `
                    -Method Delete -Headers $headers -ErrorAction Stop | Out-Null
                Write-Host "  Existing $kind deleted" -ForegroundColor Green
            } catch {
                Write-Host "  Warning: Could not delete existing ${kind}: $_" -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host "  Warning: Could not list existing releases: $_" -ForegroundColor Yellow
    }

    $changelogPath = Join-Path $RepoRoot "CHANGELOG.md"
    $changelogBody = Get-ChangelogSection -Version $actualVersion -ChangelogPath $changelogPath
    if ($changelogBody) {
        Write-Host "  Using CHANGELOG.md section for $actualVersion as release body" -ForegroundColor Green
        $releaseBody = $changelogBody
    } else {
        Write-Host "  No CHANGELOG.md section found for $actualVersion, publishing release with empty body" -ForegroundColor Yellow
        $releaseBody = ""
    }

    $releasePayload = @{
        tag_name                 = $tagName
        name                     = $tagName
        body                     = $releaseBody
        draft                    = $false
        prerelease               = $false
        generate_release_notes   = $false
    } | ConvertTo-Json

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Namespace/$RepoName/releases" `
        -Method Post -Headers $headers -Body $releasePayload -ContentType "application/json" -ErrorAction Stop
    Write-Host "  GitHub release created: $($release.html_url)" -ForegroundColor Green
} catch {
    Write-Host "  Warning: Could not create GitHub release: $_" -ForegroundColor Yellow
    Write-Host "  Note: PAT needs 'repo' or 'public_repo' scope to create releases" -ForegroundColor Yellow
    Write-Host "  Create manually: https://github.com/$Namespace/$RepoName/releases/new?tag=$tagName" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "View your package at:  https://github.com/$Namespace/$RepoName/packages"
Write-Host "View your release at:  https://github.com/$Namespace/$RepoName/releases/tag/$tagName"
Write-Host ""
