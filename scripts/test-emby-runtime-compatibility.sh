#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HARNESS="$ROOT/tests/Emby.RuntimeCompatibility/RuntimeAbiHarness/RuntimeAbiHarness.csproj"
HARNESS_DLL="$ROOT/tests/Emby.RuntimeCompatibility/RuntimeAbiHarness/bin/Release/net8.0/RuntimeAbiHarness.dll"
LOCK="$ROOT/tests/Emby.RuntimeCompatibility/artifacts.lock"
WORK="${TMPDIR:-/tmp}/emby-runtime-compatibility"
DOWNLOADS="$WORK/downloads"
EXTRACTED="$WORK/extracted"
# Set EMBY_RUNTIME_DOTNET_HOST to the dotnet host containing Microsoft.NETCore.App 8.0.28.
RUNTIME_DOTNET_HOST="${EMBY_RUNTIME_DOTNET_HOST:-dotnet}"

if ! command -v "$RUNTIME_DOTNET_HOST" >/dev/null 2>&1; then
    printf 'Runtime ABI compatibility requires a dotnet host; %s was not found. Set EMBY_RUNTIME_DOTNET_HOST to a host with Microsoft.NETCore.App 8.0.28.\n' "$RUNTIME_DOTNET_HOST" >&2
    exit 1
fi

if ! "$RUNTIME_DOTNET_HOST" --list-runtimes | grep -Eq '^Microsoft\.NETCore\.App 8\.0\.28 \['; then
    printf 'Runtime ABI compatibility requires Microsoft.NETCore.App 8.0.28 in %s. Set EMBY_RUNTIME_DOTNET_HOST to a host with that exact runtime; the harness will not roll forward.\n' "$RUNTIME_DOTNET_HOST" >&2
    exit 1
fi

printf 'Verified Microsoft.NETCore.App 8.0.28 in %s.\n' "$RUNTIME_DOTNET_HOST"

download() {
    local name="$1"
    local hash="$2"
    local url="$3"
    local path="$DOWNLOADS/$name"

    mkdir -p "$DOWNLOADS"
    if [[ ! -f "$path" ]] || [[ "$(sha256sum "$path" | cut -d ' ' -f 1)" != "$hash" ]]; then
        rm -f "$path"
        curl --fail --location --retry 3 --silent --show-error --output "$path" "$url"
    fi
    printf '%s  %s\n' "$hash" "$path" | sha256sum --check --status
}

while read -r name hash url; do
    [[ -n "$name" ]] || continue
    download "$name" "$hash" "$url"
done < "$LOCK"

rm -rf "$EXTRACTED"
mkdir -p "$EXTRACTED/sdk" "$EXTRACTED/server-4.9.5.0" "$EXTRACTED/server-4.10.0.40"
unzip -q "$DOWNLOADS/mediabrowser.server.core.4.8.0.80.nupkg" -d "$EXTRACTED/sdk"
unzip -q "$DOWNLOADS/mediabrowser.common.4.8.0.80.nupkg" -d "$EXTRACTED/sdk-common"
cp "$EXTRACTED/sdk-common/lib/netstandard2.0/"*.dll "$EXTRACTED/sdk/lib/netstandard2.0/"
dpkg-deb --extract "$DOWNLOADS/emby-server-deb_4.9.5.0_amd64.deb" "$EXTRACTED/server-4.9.5.0"
dpkg-deb --extract "$DOWNLOADS/emby-server-deb_4.10.0.40_amd64.deb" "$EXTRACTED/server-4.10.0.40"

printf '%s  %s\n' "ec4b97b23cddbb6f6bf2d22259eaef5b304e5fd716e6e3df237fb328fe90d180" "$EXTRACTED/sdk/lib/netstandard2.0/MediaBrowser.Controller.dll" | sha256sum --check --status
printf '%s  %s\n' "be9001d50e829df1096b325bf8923ed54355686348b652fe8da0cf1c652c82ef" "$EXTRACTED/server-4.9.5.0/opt/emby-server/system/MediaBrowser.Controller.dll" | sha256sum --check --status
printf '%s  %s\n' "1a45d6c80ff1a19083f8bc4d3578cedbb04c71159a370370e6e02964ffe7ff86" "$EXTRACTED/server-4.10.0.40/opt/emby-server/system/MediaBrowser.Controller.dll" | sha256sum --check --status

dotnet build --configuration Release "$ROOT/Emby.M3uEditor.Plugin/Emby.M3uEditor.Plugin.csproj"
dotnet build --configuration Release "$HARNESS"
"$RUNTIME_DOTNET_HOST" "$HARNESS_DLL" \
    "$ROOT/Emby.M3uEditor.Plugin/bin/Release/netstandard2.0/Emby.M3uEditor.Plugin.dll" \
    "$DOWNLOADS/Emby.M3uEditor.Plugin.v1.5.0.dll" \
    "$EXTRACTED/sdk/lib/netstandard2.0" \
    "$EXTRACTED/server-4.9.5.0/opt/emby-server/system" \
    "$EXTRACTED/server-4.10.0.40/opt/emby-server/system"
