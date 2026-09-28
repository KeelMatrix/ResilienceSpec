#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export KEELMATRIX_NO_TELEMETRY=1

if ! command -v dotnet >/dev/null 2>&1; then
    printf '%s\n' 'validate-linux.sh requires the .NET SDK selected by global.json.' >&2
    exit 1
fi

if ! command -v pwsh >/dev/null 2>&1; then
    printf '%s\n' 'validate-linux.sh requires PowerShell 7 (pwsh) for the release-contract tests.' >&2
    exit 1
fi

pwsh -NoProfile -File scripts/Validate.ps1 -Mode Full -ResilienceVersion 10.10.0

dotnet test tests/KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=9.8.0
dotnet test tests/KeelMatrix.ResilienceSpec.IntegrationTests -c Release -p:ResilienceVersion=10.10.0
