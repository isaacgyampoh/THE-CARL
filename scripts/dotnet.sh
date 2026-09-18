#!/usr/bin/env bash
#
# Runs the .NET SDK this repository is pinned to, wherever it happens to be installed.
#
# This exists because of a real failure. The machine's `dotnet` on PATH resolved to an
# install root holding only .NET 10, while the 8.0 SDK that global.json pins sat in a
# different root under $HOME. Every backend command then failed with "A compatible .NET SDK
# was not found" — which reads like a broken repository rather than a misresolved muxer.
#
# The subtlety is that DOTNET_ROOT does not fix it. SDK resolution is done by whichever
# `dotnet` binary actually runs, relative to its own location, so exporting a variable while
# still invoking the wrong muxer changes nothing. The only reliable fix is to invoke the
# right binary, which is what this does.
#
# It searches the standard install roots rather than hard-coding this laptop's layout, and
# refuses to fall back to a newer major version: silently building the backend on .NET 10
# because 8 was missing is precisely the drift that pinning is meant to prevent.
#
# Usage:  scripts/dotnet.sh test
#         scripts/dotnet.sh build
#
# Anything after the script name is passed straight through to `dotnet`.

set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
global_json="$repo_root/global.json"

if [ ! -f "$global_json" ]; then
    echo "error: no global.json at $global_json — cannot tell which SDK is intended." >&2
    exit 1
fi

# The pinned version, and the major band it belongs to. rollForward in global.json decides
# how far within that band the SDK may move; what must never happen is crossing it.
pinned="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$global_json" | head -1)"
if [ -z "$pinned" ]; then
    echo "error: global.json does not name an SDK version." >&2
    exit 1
fi
band="${pinned%%.*}"

# Standard install roots, most specific first. DOTNET_ROOT is honoured when someone has
# deliberately set one; the rest are where the official installers put things on macOS and
# Linux.
candidates=(
    "${DOTNET_ROOT:-}"
    "$HOME/.dotnet"
    "/usr/local/share/dotnet"
    "/usr/share/dotnet"
    "/opt/homebrew/opt/dotnet/libexec"
)

for root in "${candidates[@]}"; do
    [ -n "$root" ] || continue
    [ -x "$root/dotnet" ] || continue

    # An SDK in the right major band, not merely any SDK.
    if compgen -G "$root/sdk/$band.*" > /dev/null 2>&1; then
        export DOTNET_ROOT="$root"
        # Keeps child processes (MSBuild node reuse, test hosts) on the same muxer, and puts
        # global tools within reach. Without the tools directory, `dotnet ef` fails with
        # "dotnet-ef does not exist" even though the tool is installed — the muxer resolves
        # tool commands from PATH, not from the SDK root.
        export PATH="$root:$root/tools:$HOME/.dotnet/tools:$PATH"
        # Telemetry is off by default here: a build tool should not phone home from a
        # financial project's developer machine without someone choosing that.
        export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
        exec "$root/dotnet" "$@"
    fi
done

# Nothing suitable. Say exactly what is missing and exactly how to get it, rather than
# leaving the next person to rediscover that the SDK is in a second install root.
{
    echo "error: no .NET $band SDK found, but global.json pins $pinned."
    echo
    echo "Searched:"
    for root in "${candidates[@]}"; do
        [ -n "$root" ] || continue
        if [ -d "$root/sdk" ]; then
            echo "  $root/sdk -> $(ls "$root/sdk" 2>/dev/null | tr '\n' ' ')"
        else
            echo "  $root (no sdk directory)"
        fi
    done
    echo
    echo "Install it into the user-local root with the official script:"
    echo
    echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh \\"
    echo "    | bash -s -- --channel $band.0 --install-dir \"\$HOME/.dotnet\""
    echo
    echo "Then re-run this command. Do not 'fix' this by editing global.json to a newer"
    echo "major version — every project here targets net$band.0."
} >&2
exit 1
