param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet('Debug', 'Staging', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$IsWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows
)
$IsLinuxPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Linux
)
$IsMacOSPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::OSX
)

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "> dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if (0 -ne $LASTEXITCODE) {
        throw "dotnet exited with status $LASTEXITCODE."
    }
}

function Get-ExecutableFileName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Rid
    )

    if ($Rid.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'tar.exe'
    }

    return 'tar'
}

function Get-CurrentRuntimeIdentifier {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $architectureName = if ([System.Runtime.InteropServices.Architecture]::X64 -eq $architecture) {
        'x64'
    } elseif ([System.Runtime.InteropServices.Architecture]::Arm64 -eq $architecture) {
        'arm64'
    } else {
        return ''
    }

    if ($IsWindowsPlatform) {
        return "win-$architectureName"
    }
    if ($IsLinuxPlatform) {
        return "linux-$architectureName"
    }
    if ($IsMacOSPlatform) {
        return "osx-$architectureName"
    }

    return ''
}

function Invoke-Executable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Write-Host "> $Path --version"
    & $Path --version
    if (0 -ne $LASTEXITCODE) {
        throw "Executable '$Path' exited with status $LASTEXITCODE."
    }
}

function Assert-ArchiveContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,

        [Parameter(Mandatory = $true)]
        [string]$RootDirectoryName,

        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedFileNames
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($fileName in $ExpectedFileNames) {
            $expectedEntry = "$RootDirectoryName/$fileName"
            if ($entries -notcontains $expectedEntry) {
                throw "Archive '$ArchivePath' does not contain '$expectedEntry'."
            }
        }
    } finally {
        $archive.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    throw 'RuntimeIdentifier must not be empty.'
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version must not be empty.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'Icod.Tar.csproj'
$releaseRoot = Join-Path $repositoryRoot 'artifacts/release'
$publishDirectory = Join-Path $releaseRoot "publish/$RuntimeIdentifier"
$stageDirectoryName = "Icod.Tar-$Version-$RuntimeIdentifier"
$stageParent = Join-Path $releaseRoot 'stage'
$stageDirectory = Join-Path $stageParent $stageDirectoryName
$archivePath = Join-Path $releaseRoot "$stageDirectoryName.zip"
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

foreach ($path in @($publishDirectory, $stageDirectory)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @(
        'publish',
        $projectPath,
        '-c', $Configuration,
        '-r', $RuntimeIdentifier,
        '--self-contained', $selfContainedValue,
        "-p:PublishSelfContained=$selfContainedValue",
        '-p:PublishSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true',
        '-o', $publishDirectory
    )

    $executableFileName = Get-ExecutableFileName -Rid $RuntimeIdentifier
    $publishedExecutable = Join-Path $publishDirectory $executableFileName
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw "Publish did not produce '$publishedExecutable'."
    }

    Copy-Item -LiteralPath $publishedExecutable -Destination (Join-Path $stageDirectory $executableFileName)
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $stageDirectory 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $stageDirectory 'README.md')

    $currentRid = Get-CurrentRuntimeIdentifier
    if ($RuntimeIdentifier -eq $currentRid) {
        $stagedExecutable = Join-Path $stageDirectory $executableFileName
        if (-not $IsWindowsPlatform) {
            & chmod +x $stagedExecutable
            if (0 -ne $LASTEXITCODE) {
                throw "chmod failed for '$stagedExecutable'."
            }
        }
        Invoke-Executable -Path $stagedExecutable
    } else {
        Write-Host "Skipping executable smoke test because host RID '$currentRid' does not match '$RuntimeIdentifier'."
    }

    if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        Compress-Archive -LiteralPath $stageDirectory -DestinationPath $archivePath -CompressionLevel Optimal
    } else {
        $zipCommand = Get-Command zip -ErrorAction SilentlyContinue
        if ($null -eq $zipCommand) {
            throw "The 'zip' command is required to preserve executable permissions for '$RuntimeIdentifier' archives."
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

    Assert-ArchiveContents `
        -ArchivePath $archivePath `
        -RootDirectoryName $stageDirectoryName `
        -ExpectedFileNames @($executableFileName, 'LICENSE', 'README.md')

    Write-Host ''
    Write-Host "Created release archive: $archivePath"
    Write-Host "  Runtime identifier: $RuntimeIdentifier"
    Write-Host "  Self-contained:     $selfContainedValue"
} finally {
    Pop-Location
}
