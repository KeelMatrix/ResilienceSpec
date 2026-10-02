#!/usr/bin/env bash
set -euo pipefail

script_directory="${BASH_SOURCE[0]%/*}"
repository_root="$(cd -- "$script_directory/.." && pwd)"
cd "$repository_root"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export KEELMATRIX_NO_TELEMETRY=1

shim_directory="$repository_root/build/command-shims"
if [ ! -x "$shim_directory/dotnet" ] || [ ! -x "$shim_directory/git" ] || [ ! -x "$shim_directory/pwsh" ]; then
    printf '%s\n' 'validate-linux.sh requires the repository external-command shims.' >&2
    exit 1
fi

real_dotnet="$(command -v dotnet || true)"
real_pwsh="$(command -v pwsh || true)"
if [ -z "$real_dotnet" ] || [[ "$real_dotnet" == "$shim_directory"/* ]]; then
    printf '%s\n' 'validate-linux.sh requires the .NET SDK selected by global.json before routing is enabled.' >&2
    exit 1
fi
if [ -z "$real_pwsh" ] || [[ "$real_pwsh" == "$shim_directory"/* ]]; then
    printf '%s\n' 'validate-linux.sh requires an unshimmed PowerShell 7 executable before routing is enabled.' >&2
    exit 1
fi

export PATH="$shim_directory:$PATH"

"$real_pwsh" -NoProfile -File scripts/Validate.ps1 -Mode Full -ResilienceVersion 10.10.0

"$real_pwsh" -NoProfile -File build/Invoke-ExternalScript.ps1 -ScriptPath scripts/Invoke-IntegrationTests.ps1 -ResilienceVersion 9.8.0
"$real_pwsh" -NoProfile -File build/Invoke-ExternalScript.ps1 -ScriptPath scripts/Invoke-IntegrationTests.ps1 -ResilienceVersion 10.10.0
