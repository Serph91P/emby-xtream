#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HARNESS="$ROOT/tests/Emby.RuntimeCompatibility/RuntimeAbiHarness/RuntimeAbiHarness.csproj"
LOCK="$ROOT/tests/Emby.RuntimeCompatibility/artifacts.lock"
WORK="${TMPDIR:-/tmp}/emby-runtime-compatibility"
DOWNLOADS="$WORK/downloads"
EXTRACTED="$WORK/extracted"

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
dotnet run --configuration Release --project "$HARNESS" -- \
    "$ROOT/Emby.M3uEditor.Plugin/bin/Release/netstandard2.0/Emby.M3uEditor.Plugin.dll" \
    "$DOWNLOADS/Emby.M3uEditor.Plugin.v1.5.0.dll" \
    "$EXTRACTED/sdk/lib/netstandard2.0" \
    "$EXTRACTED/server-4.9.5.0/opt/emby-server/system" \
    "$EXTRACTED/server-4.10.0.40/opt/emby-server/system"
