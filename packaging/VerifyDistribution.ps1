param(
    [ValidateSet('Debug', 'Staging', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$IsWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows
)

function Get-ProjectProperty {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Project,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($group in $Project.Project.PropertyGroup) {
        $property = $group.SelectSingleNode($Name)
        if ($null -ne $property -and 0 -lt $property.InnerText.Length) {
            return $property.InnerText
        }
    }

    throw "Project property '$Name' was not found."
}

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

function Get-ExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$CommandName
    )

    $fileName = if ($IsWindowsPlatform) {
        "$CommandName.exe"
    } else {
        $CommandName
    }

    $path = Join-Path $Directory $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected executable '$path' was not created."
    }

    return $path
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string[]]$Arguments = @(),

        [int]$ExpectedExitCode = 0
    )

    Write-Host "> $Path $($Arguments -join ' ')"
    & $Path @Arguments
    if ($ExpectedExitCode -ne $LASTEXITCODE) {
        throw "Tool '$Path' exited with status $LASTEXITCODE; expected $ExpectedExitCode."
    }
}

function Read-ToolSettingsFromPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetFramework
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $settingsPath = "tools/$TargetFramework/any/DotnetToolSettings.xml"
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $settingsPath } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "Package '$PackagePath' does not contain '$settingsPath'."
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            [xml]$settings = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }

        $commands = @($settings.DotNetCliTool.Commands.Command)
        if (0 -eq $commands.Count) {
            throw "Package '$PackagePath' declares no .NET tool commands."
        }

        return @{
            Archive = $archive
            Commands = $commands
        }
    } catch {
        $archive.Dispose()
        throw
    }
}

function Assert-ToolPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetFramework,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedCommand,

        [Parameter(Mandatory = $true)]
        [string[]]$RequiredAssemblies
    )

    $result = Read-ToolSettingsFromPackage -PackagePath $PackagePath -TargetFramework $TargetFramework
    $archive = $result.Archive
    try {
        if (1 -ne $result.Commands.Count) {
            throw "Package '$PackagePath' declares $($result.Commands.Count) commands; expected exactly one."
        }

        $command = $result.Commands[0]
        if ($ExpectedCommand -ne "$($command.Name)") {
            throw "Package '$PackagePath' declares command '$($command.Name)'; expected '$ExpectedCommand'."
        }
        if ('dotnet' -ne "$($command.Runner)") {
            throw "Command '$($command.Name)' in '$PackagePath' uses unexpected runner '$($command.Runner)'."
        }

        foreach ($assembly in $RequiredAssemblies) {
            $entryPath = "tools/$TargetFramework/any/$assembly"
            if (-not ($archive.Entries | Where-Object { $_.FullName -eq $entryPath } | Select-Object -First 1)) {
                throw "Package '$PackagePath' does not contain '$entryPath'."
            }
        }
    } finally {
        $archive.Dispose()
    }
}

function Write-LocalNuGetConfig {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $escapedPath = [System.Security.SecurityElement]::Escape($PackageDirectory)
    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedPath" />
  </packageSources>
</configuration>
"@

    [System.IO.File]::WriteAllText($Path, $content, [System.Text.UTF8Encoding]::new($false))
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'Icod.Tar.csproj'
$solutionPath = Join-Path $repositoryRoot 'Icod.Tar.sln'

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$targetFramework = Get-ProjectProperty -Project $project -Name 'TargetFramework'
$packageId = Get-ProjectProperty -Project $project -Name 'PackageId'
$packageVersion = Get-ProjectProperty -Project $project -Name 'PackageVersion'

$validationRoot = Join-Path $repositoryRoot 'artifacts/distribution-validation'
$packageDirectory = Join-Path $validationRoot 'packages'
$toolPath = Join-Path $validationRoot 'tool'
$nugetConfigPath = Join-Path $validationRoot 'NuGet.Config'
$standaloneOutputPath = Join-Path $repositoryRoot "bin/$Configuration/$targetFramework"

if (Test-Path -LiteralPath $validationRoot) {
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @('restore', $solutionPath)
    Invoke-DotNet -Arguments @(
        'build',
        $solutionPath,
        '-c', $Configuration,
        '--no-restore',
        '-p:ContinuousIntegrationBuild=true'
    )
    Invoke-DotNet -Arguments @(
        'test',
        $solutionPath,
        '-c', $Configuration,
        '--no-build',
        '--logger', 'trx'
    )

    $standaloneExecutable = Get-ExecutablePath `
        -Directory $standaloneOutputPath `
        -CommandName 'tar'
    Invoke-Tool -Path $standaloneExecutable -Arguments @('--version')

    Invoke-DotNet -Arguments @(
        'pack',
        $projectPath,
        '-c', $Configuration,
        '-o', $packageDirectory,
        '-p:ContinuousIntegrationBuild=true'
    )

    $packagePath = Join-Path $packageDirectory "$packageId.$packageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Tool package '$packagePath' was not produced."
    }

    Assert-ToolPackage `
        -PackagePath $packagePath `
        -TargetFramework $targetFramework `
        -ExpectedCommand 'tar' `
        -RequiredAssemblies @('tar.dll')

    Write-LocalNuGetConfig -PackageDirectory $packageDirectory -Path $nugetConfigPath

    Invoke-DotNet -Arguments @(
        'tool', 'install', $packageId,
        '--version', $packageVersion,
        '--tool-path', $toolPath,
        '--configfile', $nugetConfigPath
    )

    $toolShim = Get-ExecutablePath -Directory $toolPath -CommandName 'tar'
    Invoke-Tool -Path $toolShim -Arguments @('--version')

    $sampleDirectory = Join-Path $validationRoot 'sample'
    New-Item -ItemType Directory -Path $sampleDirectory -Force | Out-Null
    $samplePath = Join-Path $sampleDirectory 'sample.txt'
    [System.IO.File]::WriteAllText(
        $samplePath,
        "Icod.Tar distribution validation`n",
        [System.Text.UTF8Encoding]::new($false)
    )
    $archivePath = Join-Path $validationRoot 'sample.tar'
    Invoke-Tool -Path $toolShim -Arguments @('-cf', $archivePath, '-C', $sampleDirectory, 'sample.txt')
    Invoke-Tool -Path $toolShim -Arguments @('-tf', $archivePath)

    Write-Host ''
    Write-Host 'Distribution verification completed successfully.'
    Write-Host "  Tool package: $packagePath"
    Write-Host "  Standalone executable: $standaloneExecutable"
} finally {
    Pop-Location
}
