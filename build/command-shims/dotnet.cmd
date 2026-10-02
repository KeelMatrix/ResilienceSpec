@echo off
setlocal EnableExtensions
if not defined KEELMATRIX_EXTERNAL_COMMAND_AUTHORIZATION (
    >&2 echo External-command routing violation: 'dotnet' was not launched by build/Invoke-ExternalCommand.ps1; run validation gates through the bounded runner.
    exit /b 86
)
if not defined KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_DOTNET (
    >&2 echo External-command routing failed closed: the authorized dotnet target is missing.
    exit /b 86
)
if /I "%KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_DOTNET%"=="%~f0" (
    >&2 echo External-command routing failed closed: the authorized dotnet target points at its shim.
    exit /b 86
)
if not exist "%KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_DOTNET%" (
    >&2 echo External-command routing failed closed: the authorized dotnet target is missing.
    exit /b 86
)
"%KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_DOTNET%" %*
exit /b %ERRORLEVEL%
