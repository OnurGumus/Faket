#!/usr/bin/env bash
set -euo pipefail

# Faket build — .NET 10 only. The legacy FAKE/mono/ILRepack pipeline (build.fsx) is being
# retired; this wraps the actual working flow. Pass a target: build (default) | test | pack.

target="${1:-build}"

dotnet tool restore
dotnet paket restore

case "$target" in
  build)
    dotnet build Paket.sln -c Release
    ;;
  test)
    dotnet build Paket.sln -c Release
    dotnet test tests/Paket.Tests/Paket.Tests.fsproj -c Release --no-build
    ;;
  pack)
    dotnet pack src/Paket/Paket.fsproj -c Release
    dotnet pack src/Paket.Core/Paket.Core.fsproj -c Release
    dotnet pack src/FSharp.DependencyManager.Paket/FSharp.DependencyManager.Paket.fsproj -c Release
    ;;
  *)
    echo "unknown target: $target (expected build|test|pack)" >&2
    exit 2
    ;;
esac
