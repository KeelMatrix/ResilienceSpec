@echo off
setlocal EnableExtensions
if not defined KEELMATRIX_EXTERNAL_COMMAND_AUTHORIZATION (
    >&2 echo External-command routing violation: 'pwsh' was not launched by build/Invoke-ExternalCommand.ps1.
    exit /b 86
)
if not defined KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_PWSH (
    >&2 echo External-command routing failed closed: the authorized pwsh target is missing.
    exit /b 86
)
if /I not "%~1"=="-NoProfile" if /I not "%~1"=="-noprofile" (
    >&2 echo External-command routing violation: authorized pwsh calls must use -NoProfile -File for a repository script.
    exit /b 86
)
if /I not "%~2"=="-File" if /I not "%~2"=="-file" (
    >&2 echo External-command routing violation: authorized pwsh calls must use -NoProfile -File for a repository script.
    exit /b 86
)
if "%~3"=="" (
    >&2 echo External-command routing violation: authorized pwsh calls require a repository script.
    exit /b 86
)
if /I not "%~x3"==".ps1" (
    >&2 echo External-command routing violation: authorized pwsh calls require a PowerShell script.
    exit /b 86
)
if not exist "%~3" (
    >&2 echo External-command routing violation: the authorized pwsh script does not exist.
    exit /b 86
)
if /I "%KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_PWSH%"=="%~f0" (
    >&2 echo External-command routing failed closed: the authorized pwsh target points at its shim.
    exit /b 86
)
if not exist "%KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_PWSH%" (
    >&2 echo External-command routing failed closed: the authorized pwsh target is missing.
    exit /b 86
)
"%KEELMATRIX_EXTERNAL_COMMAND_REAL_PATH_PWSH%" %*
exit /b %ERRORLEVEL%
