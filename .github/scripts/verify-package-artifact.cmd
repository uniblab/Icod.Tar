@echo off
setlocal EnableExtensions

if "%~1"=="" goto usage
if "%~2"=="" goto usage

set "ARTIFACT_DIR=%~1"
set "CONFIGURATION=%~2"

pushd "%~dp0\..\.." >nul || exit /b 1
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File packaging\VerifyPackageArtifact.ps1 -ArtifactDirectory "%ARTIFACT_DIR%" -Configuration "%CONFIGURATION%" -AllowNoPackages
set "RESULT=%errorlevel%"
popd
exit /b %RESULT%

:usage
echo Usage: %~nx0 ^<artifact-directory^> ^<Debug^|Staging^|Release^> 1>&2
exit /b 1
