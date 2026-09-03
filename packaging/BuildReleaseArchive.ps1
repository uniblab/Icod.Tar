param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet('Debug', 'Staging', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ArchiveBaseName = '',

    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'RepositoryTools.psm1') -Force

$solutionPath = Get-RepositorySolution -RepositoryRoot $repositoryRoot
$projects = @(Get-SolutionProjects -SolutionPath $solutionPath -RepositoryRoot $repositoryRoot)
$executables = @(Get-ExecutableProjects -ProjectPaths $projects -Configuration $Configuration)
if (0 -eq $executables.Count) {
    throw 'The solution contains no executable projects to archive.'
}

if ([string]::IsNullOrWhiteSpace($ArchiveBaseName)) {
    $ArchiveBaseName = Split-Path $repositoryRoot -Leaf
}

$releaseRoot = Join-Path $repositoryRoot 'artifacts/release'
$publishRoot = Join-Path $releaseRoot "publish/$RuntimeIdentifier"
$stageDirectoryName = "$ArchiveBaseName-$Version-$RuntimeIdentifier"
$stageParent = Join-Path $releaseRoot 'stage'
$stageDirectory = Join-Path $stageParent $stageDirectoryName
$archivePath = Join-Path $releaseRoot "$stageDirectoryName.zip"
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
$stagedExecutables = @()

foreach ($path in @($publishRoot, $stageDirectory)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @('restore', $solutionPath, '-r', $RuntimeIdentifier)

    foreach ($executable in $executables) {
        $publishDirectory = Join-Path $publishRoot $executable.AssemblyName
        New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

        Invoke-DotNet -Arguments @(
            'publish', $executable.ProjectPath,
            '-c', $Configuration,
            '-r', $RuntimeIdentifier,
            '--no-restore',
            '--self-contained', $selfContainedValue,
            "-p:PublishSelfContained=$selfContainedValue",
            '-p:PublishSingleFile=true',
            '-p:PublishTrimmed=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            '-p:ContinuousIntegrationBuild=true',
            '-o', $publishDirectory
        )

        $fileName = if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
            "$($executable.AssemblyName).exe"
        } else {
            $executable.AssemblyName
        }
        $publishedExecutable = Join-Path $publishDirectory $fileName
        if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
            throw "Publish did not produce '$publishedExecutable'."
        }

        $stagedExecutable = Join-Path $stageDirectory $fileName
        Copy-Item -LiteralPath $publishedExecutable -Destination $stagedExecutable
        $stagedExecutables += $stagedExecutable
    }

    foreach ($supportFile in @('LICENSE', 'README.md')) {
        $source = Join-Path $repositoryRoot $supportFile
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $stageDirectory $supportFile)
        }
    }

    if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        Compress-Archive -LiteralPath $stageDirectory -DestinationPath $archivePath -CompressionLevel Optimal
    } else {
        $zipCommand = Get-Command zip -ErrorAction SilentlyContinue
        if ($null -eq $zipCommand) {
            throw "The 'zip' command is required to preserve executable permissions."
        }
        foreach ($stagedExecutable in $stagedExecutables) {
            & chmod +x $stagedExecutable
            if (0 -ne $LASTEXITCODE) {
                throw "chmod failed for '$stagedExecutable'."
            }
        }
        Push-Location $stageParent
        try {
            & $zipCommand.Source -r -q $archivePath $stageDirectoryName
            if (0 -ne $LASTEXITCODE) {
                throw "zip exited with status $LASTEXITCODE."
            }
        } finally {
            Pop-Location
        }
    }

    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Release archive '$archivePath' was not produced."
    }

    Write-Host "Created release archive: $archivePath"
} finally {
    Pop-Location
}
