#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet_bin="${KINSHOUT_DOTNET:-dotnet}"
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
export MSBUILDDISABLENODEREUSE=1
exec "$dotnet_bin" run --project tools/CommentQuality -c Release -p:UseSharedCompilation=false -- "${1:-review/comment-quality-live.json}"
