param(
    [ValidateSet('Debug', 'Staging', 'Release')]
    [string]$Configuration = 'Release',

    [string]$GitHubOutputPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'RepositoryTools.psm1') -Force

$solutionPath = Get-RepositorySolution -RepositoryRoot $repositoryRoot -AllowMissing
$hasSolution = $null -ne $solutionPath
$hasExecutables = $false
$portableSolutionPath = ''

if ($hasSolution) {
    $projects = @(Get-SolutionProjects -SolutionPath $solutionPath -RepositoryRoot $repositoryRoot)
    $executables = @(Get-ExecutableProjects -ProjectPaths $projects -Configuration $Configuration)
    $hasExecutables = 0 -lt $executables.Count
    $portableSolutionPath = [System.IO.Path]::GetRelativePath(
        $repositoryRoot,
        $solutionPath
    ).Replace(
        [System.IO.Path]::DirectorySeparatorChar,
        '/'
    )
}

$result = [ordered]@{
    RepositoryRoot = $repositoryRoot
    HasSolution = $hasSolution
    SolutionPath = $portableSolutionPath
    HasExecutables = $hasExecutables
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    "has_solution=$($hasSolution.ToString().ToLowerInvariant())" >> $GitHubOutputPath
    "solution_path=$($result.SolutionPath)" >> $GitHubOutputPath
    "has_executables=$($hasExecutables.ToString().ToLowerInvariant())" >> $GitHubOutputPath
}

[pscustomobject]$result
