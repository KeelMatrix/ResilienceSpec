@echo off
>&2 echo External-command routing violation: 'pwsh' reached the repository PATH shim; run validation gates through build/Invoke-ExternalCommand.ps1.
exit /b 86
