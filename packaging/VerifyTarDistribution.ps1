param(
    [ValidateSet('Debug', 'Staging', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'RepositoryTools.psm1') -Force
$solutionPath = Get-RepositorySolution -RepositoryRoot $repositoryRoot
$projects = @(Get-SolutionProjects -SolutionPath $solutionPath -RepositoryRoot $repositoryRoot)
$executables = @(Get-ExecutableProjects -ProjectPaths $projects -Configuration $Configuration)
$tarProject = @($executables | Where-Object { $_.AssemblyName -eq 'tar' })
if (1 -ne $tarProject.Count) {
    throw "Expected exactly one executable named 'tar'; found $($tarProject.Count)."
}

$projectPath = $tarProject[0].ProjectPath
$targetFramework = Get-MSBuildProperty -ProjectPath $projectPath -Name 'TargetFramework' -Configuration $Configuration
$packageId = Get-MSBuildProperty -ProjectPath $projectPath -Name 'PackageId' -Configuration $Configuration
$packageVersion = Get-MSBuildProperty -ProjectPath $projectPath -Name 'PackageVersion' -Configuration $Configuration
$validationRoot = Join-Path $repositoryRoot 'artifacts/distribution-validation'
$packageDirectory = Join-Path $validationRoot 'packages'
$toolPath = Join-Path $validationRoot 'tool'
$nugetConfigPath = Join-Path $validationRoot 'NuGet.Config'
$outputDirectory = Join-Path $repositoryRoot "bin/$Configuration/$targetFramework"
$isWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows
)
$executableName = if ($isWindowsPlatform) { 'tar.exe' } else { 'tar' }
$standaloneExecutable = Join-Path $outputDirectory $executableName
if (-not (Test-Path -LiteralPath $standaloneExecutable -PathType Leaf)) {
    throw "Expected standalone executable '$standaloneExecutable'."
}

Write-Host "> $standaloneExecutable --version"
& $standaloneExecutable --version
if (0 -ne $LASTEXITCODE) {
    throw "Standalone tar exited with status $LASTEXITCODE."
}

$packagePath = Join-Path $packageDirectory "$packageId.$packageVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Expected package '$packagePath'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $settingsEntry = $archive.Entries |
        Where-Object { $_.FullName -eq "tools/$targetFramework/any/DotnetToolSettings.xml" } |
        Select-Object -First 1
    if ($null -eq $settingsEntry) {
        throw "Package '$packagePath' does not contain DotnetToolSettings.xml."
    }
    $reader = [System.IO.StreamReader]::new($settingsEntry.Open())
    try {
        [xml]$settings = $reader.ReadToEnd()
    } finally {
        $reader.Dispose()
    }
    $commands = @($settings.DotNetCliTool.Commands.Command)
    if (1 -ne $commands.Count -or 'tar' -ne "$($commands[0].Name)" -or 'dotnet' -ne "$($commands[0].Runner)") {
        throw "Package '$packagePath' does not declare the expected tar .NET tool command."
    }
    if (-not ($archive.Entries | Where-Object { $_.FullName -eq "tools/$targetFramework/any/tar.dll" } | Select-Object -First 1)) {
        throw "Package '$packagePath' does not contain tar.dll."
    }
} finally {
    $archive.Dispose()
}

$escapedPackageDirectory = [System.Security.SecurityElement]::Escape($packageDirectory)
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedPackageDirectory" />
  </packageSources>
</configuration>
"@
[System.IO.File]::WriteAllText(
    $nugetConfigPath,
    $nugetConfig,
    [System.Text.UTF8Encoding]::new($false)
)

if (Test-Path -LiteralPath $toolPath) {
    Remove-Item -LiteralPath $toolPath -Recurse -Force
}
Invoke-DotNet -Arguments @(
    'tool', 'install', $packageId,
    '--version', $packageVersion,
    '--tool-path', $toolPath,
    '--configfile', $nugetConfigPath
)
$toolShim = Join-Path $toolPath $executableName
if (-not (Test-Path -LiteralPath $toolShim -PathType Leaf)) {
    throw "Expected installed tool shim '$toolShim'."
}

Write-Host "> $toolShim --version"
& $toolShim --version
if (0 -ne $LASTEXITCODE) {
    throw "Installed tar tool exited with status $LASTEXITCODE."
}

$sampleDirectory = Join-Path $validationRoot 'tar-smoke'
if (Test-Path -LiteralPath $sampleDirectory) {
    Remove-Item -LiteralPath $sampleDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $sampleDirectory -Force | Out-Null
$samplePath = Join-Path $sampleDirectory 'sample.txt'
[System.IO.File]::WriteAllText(
    $samplePath,
    "Icod.Tar distribution validation`n",
    [System.Text.UTF8Encoding]::new($false)
)
$archivePath = Join-Path $validationRoot 'tar-smoke.tar'
& $toolShim '-cf' $archivePath '-C' $sampleDirectory 'sample.txt'
if (0 -ne $LASTEXITCODE) {
    throw "Installed tar failed to create a sample archive with status $LASTEXITCODE."
}
& $toolShim '-tf' $archivePath
if (0 -ne $LASTEXITCODE) {
    throw "Installed tar failed to list the sample archive with status $LASTEXITCODE."
}

Write-Host 'Icod.Tar product distribution smoke completed successfully.'
