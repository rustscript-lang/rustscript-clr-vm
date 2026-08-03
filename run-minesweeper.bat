@echo off
setlocal

set "ROOT=%~dp0"
set "RUNNER=%ROOT%PdVm.Runner.dll"
if not exist "%RUNNER%" set "RUNNER=%ROOT%PdVm.Runner\bin\Release\net10.0\PdVm.Runner.dll"

if not exist "%RUNNER%" (
    echo PdVm.Runner.dll was not found. Build the solution or run this file from a release package.
    exit /b 1
)

dotnet "%RUNNER%" run "%ROOT%examples\dotnet-minesweeper.rss" --profile winforms %*
exit /b %ERRORLEVEL%
