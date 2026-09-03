param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [ValidateSet('Debug', 'Staging', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ExpectedVersion = '',

    [switch]$AllowNoPackages,

    [string]$GitHubOutputPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'RepositoryTools.psm1') -Force

if (-not [System.IO.Path]::IsPathRooted($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repositoryRoot $ArtifactDirectory
}
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) {
    throw "Artifact directory '$ArtifactDirectory' does not exist."
}

$packages = @(
    Get-ChildItem -LiteralPath $ArtifactDirectory -Filter '*.nupkg' -File |
        Where-Object { -not $_.Name.EndsWith('.symbols.nupkg', [System.StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object Name
)

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $packages = @(
        $packages |
            Where-Object {
                (Get-PackageMetadata -PackagePath $_.FullName).Version -eq $ExpectedVersion
            }
    )
}

if (0 -eq $packages.Count -and -not $AllowNoPackages) {
    $suffix = if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) { '' } else { " with version '$ExpectedVersion'" }
    throw "No NuGet packages$suffix were found in '$ArtifactDirectory'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $metadata = Get-PackageMetadata -PackagePath $package.FullName
    Write-Host "Verifying $($metadata.Id) $($metadata.Version): $($package.FullName)"

    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        if (0 -eq $archive.Entries.Count) {
            throw "Package '$($package.FullName)' is empty."
        }

        if (-not [string]::IsNullOrWhiteSpace($metadata.Readme)) {
            $readmeEntry = $archive.Entries |
                Where-Object { $_.FullName -eq $metadata.Readme } |
                Select-Object -First 1
            if ($null -eq $readmeEntry) {
                throw "Package '$($package.FullName)' declares missing readme '$($metadata.Readme)'."
            }
        }

        $toolSettings = @(
            $archive.Entries |
                Where-Object { $_.FullName.EndsWith('/DotnetToolSettings.xml', [System.StringComparison]::OrdinalIgnoreCase) }
        )
        if (1 -lt $toolSettings.Count) {
            throw "Package '$($package.FullName)' contains multiple DotnetToolSettings.xml files."
        }
    } finally {
        $archive.Dispose()
    }
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    "package_count=$($packages.Count)" >> $GitHubOutputPath
    "has_packages=$((0 -lt $packages.Count).ToString().ToLowerInvariant())" >> $GitHubOutputPath
}

Write-Host "Exact package verification completed successfully for $($packages.Count) package(s) ($Configuration)."
