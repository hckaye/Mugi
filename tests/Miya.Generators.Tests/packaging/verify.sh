#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(CDPATH= cd -- "$script_dir/../../.." && pwd)
artifacts_dir="$script_dir/artifacts"
packages_dir="$artifacts_dir/packages"
nuget_packages_dir="$artifacts_dir/nuget"
direct_project="$script_dir/DirectLibrary/DirectLibrary.csproj"
transitive_project="$script_dir/TransitiveApp/TransitiveApp.csproj"

mkdir -p "$artifacts_dir"
find "$artifacts_dir" -mindepth 1 -delete
mkdir -p "$packages_dir" "$nuget_packages_dir"

export NUGET_PACKAGES="$nuget_packages_dir"
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1

dotnet pack "$repository_root/src/Miya.Json/Miya.Json.csproj" -c Release \
    -p:PackageVersion=1.0.0 -p:PackageOutputPath="$packages_dir"
dotnet pack "$repository_root/src/Miya/Miya.csproj" -c Release \
    -p:PackageVersion=1.0.0 -p:PackageOutputPath="$packages_dir"
dotnet pack "$repository_root/src/Miya.Generators/Miya.Generators.csproj" -c Release \
    -p:PackageVersion=1.0.0 -p:PackageOutputPath="$packages_dir"

unzip -l "$packages_dir/Miya.Generators.1.0.0.nupkg" | grep 'analyzers/dotnet/cs/Miya.Generators.dll'
if unzip -l "$packages_dir/Miya.Generators.1.0.0.nupkg" | grep 'analyzers/dotnet/cs/Miya.Generators.Core.dll'; then
    echo "Miya.Generators must contain a single analyzer assembly." >&2
    exit 1
fi
unzip -l "$packages_dir/Miya.Generators.1.0.0.nupkg" | grep 'buildTransitive/Miya.Generators.props'
if unzip -p "$packages_dir/Miya.Generators.1.0.0.nupkg" '*.nuspec' | grep 'Microsoft.CodeAnalysis'; then
    echo "Miya.Generators must not expose Roslyn package dependencies." >&2
    exit 1
fi

dotnet restore "$transitive_project" --configfile "$script_dir/NuGet.config" --force
dotnet build "$direct_project" -c Release --no-restore
dotnet build "$transitive_project" -c Release --no-restore

find "$script_dir/DirectLibrary/obj/generated" -name 'Miya.Interceptor.*.g.cs' -print | grep .
find "$script_dir/TransitiveApp/obj/generated" -name 'Miya.Interceptor.*.g.cs' -print | grep .

jit_output=$(dotnet run --project "$transitive_project" -c Release --no-restore)
printf '%s\n' "$jit_output"
printf '%s\n' "$jit_output" | grep '{"Id":1,"Name":"direct"}|direct'
printf '%s\n' "$jit_output" | grep '{"id":2,"name":"transitive"}|transitive'

case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) runtime_identifier=osx-arm64 ;;
    Darwin-x86_64) runtime_identifier=osx-x64 ;;
    Linux-aarch64) runtime_identifier=linux-arm64 ;;
    Linux-x86_64) runtime_identifier=linux-x64 ;;
    *)
        echo "Unsupported NativeAOT host: $(uname -s)-$(uname -m)" >&2
        exit 1
        ;;
esac

publish_dir="$artifacts_dir/publish"
dotnet restore "$transitive_project" -r "$runtime_identifier" \
    --configfile "$script_dir/NuGet.config" --force -p:PublishAot=true
dotnet publish "$transitive_project" -c Release -r "$runtime_identifier" \
    --self-contained true --no-restore \
    -p:PublishAot=true -p:IsAotCompatible=true -p:PublishDir="$publish_dir/"

aot_output=$($publish_dir/TransitiveApp)
printf '%s\n' "$aot_output"
test "$aot_output" = "$jit_output"

echo "Package verification passed for direct, transitive, JIT, and NativeAOT consumers."
