#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_executable="${KINSHOUT_DOTNET:-dotnet}"

# Single-process MSBuild and direct compilation also work in environments that
# do not allow the Unix sockets used by MSBuild/Roslyn background servers.
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export MSBUILDDISABLENODEREUSE=1

exec "$dotnet_executable" test "$repo_root/Kinshout.Api.Tests/Kinshout.Api.Tests.csproj" \
    --configuration Release -m:1 -p:UseSharedCompilation=false -nodeReuse:false "$@"
