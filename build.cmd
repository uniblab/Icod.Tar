@echo off
setlocal

if "%~1"=="" goto all

if /I "%~1"=="clean"   goto run-clean
if /I "%~1"=="restore" goto run-restore
if /I "%~1"=="build"   goto run-build
if /I "%~1"=="test"    goto run-test

echo Invalid section: "%~1"
echo Usage: %~nx0 [clean^|restore^|build^|test]
exit /b 1


:all
call :clean   || exit /b 1
call :restore || exit /b 1
call :build   || exit /b 1
call :test    || exit /b 1
exit /b 0


:run-clean
call :clean
exit /b %errorlevel%


:run-restore
call :restore
exit /b %errorlevel%


:run-build
call :build
exit /b %errorlevel%


:run-test
call :test
exit /b %errorlevel%


:clean
echo.
echo === Clean ===
dotnet clean Icod.Tar.sln -c Debug
exit /b %errorlevel%


:restore
echo.
echo === Restore ===
dotnet restore Icod.Tar.sln
exit /b %errorlevel%


:build
echo.
echo === Build ===
dotnet build Icod.Tar.sln -c Debug --no-restore
exit /b %errorlevel%


:test
echo.
echo === Test ===
dotnet test Icod.Tar.sln -c Debug --no-build
exit /b %errorlevel%
