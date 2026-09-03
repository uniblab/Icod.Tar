param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [ValidateSet('Debug', 'Staging', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
& (Join-Path $repositoryRoot 'packaging/VerifyPackageArtifact.ps1') `
    -ArtifactDirectory $ArtifactDirectory `
    -Configuration $Configuration `
    -AllowNoPackages
