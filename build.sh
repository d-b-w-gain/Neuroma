#!/bin/sh
# Build Neuroma on Linux or macOS: run the test harness, then publish a
# self-contained single-file executable to dist/Neuroma.
set -eu

root=$(CDPATH= cd "$(dirname "$0")" && pwd)
cd "$root"

if command -v dotnet >/dev/null 2>&1; then
    dotnet=$(command -v dotnet)
elif [ -x "$HOME/.dotnet/dotnet" ]; then
    dotnet="$HOME/.dotnet/dotnet"
else
    printf '%s\n' "The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 1
fi

if [ -z "${NEUROMA_RID:-}" ]; then
    case "$(uname -s)" in
        Linux) os=linux ;;
        Darwin) os=osx ;;
        *) printf '%s\n' "Unsupported operating system: $(uname -s). Use build.ps1 on Windows." >&2; exit 1 ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) printf '%s\n' "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    NEUROMA_RID="$os-$arch"
fi

"$dotnet" run --project tests/Neuroma.Tests/Neuroma.Tests.csproj -c Release

publish_directory="$root/dist/.publish"
"$dotnet" publish src/Neuroma/Neuroma.csproj -c Release -r "$NEUROMA_RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false -o "$publish_directory"

mkdir -p "$root/dist"
# Replace via rename so a running copy of Neuroma is not disturbed.
cp "$publish_directory/Neuroma" "$root/dist/.Neuroma.new"
chmod +x "$root/dist/.Neuroma.new"
mv -f "$root/dist/.Neuroma.new" "$root/dist/Neuroma"
printf '%s\n' "Built dist/Neuroma ($NEUROMA_RID)"

for support_file in Install-Neuroma-Kokoro.sh Start-Neuroma-Kokoro.sh Register-Neuroma.sh; do
    cp "$root/$support_file" "$root/dist/$support_file"
    chmod +x "$root/dist/$support_file"
done
printf '%s\n' "Copied local Kokoro setup scripts to dist"

if [ -f "$root/neuroma.json" ]; then
    cp "$root/neuroma.json" "$root/dist/neuroma.json"
    printf '%s\n' "Copied local Kokoro settings to dist/neuroma.json"
fi
