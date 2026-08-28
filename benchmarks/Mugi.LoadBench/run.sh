#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project="$script_dir/Mugi.LoadBench.csproj"
assembly="$script_dir/bin/Release/net10.0/Mugi.LoadBench.dll"

export DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build "$project" -c Release
exec dotnet "$assembly" run "$@"
