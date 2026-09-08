#!/bin/sh
set -eu

script_directory=$(CDPATH= cd "$(/usr/bin/dirname "$0")" && pwd)
app_directory=${1:-"$script_directory"}
kokoro_commit="f901ed81ff9910b80ab24f800a245873221a5445"
short_commit="f901ed81ff99"
uv_version="0.12.10"
uv_installer_sha256="a3196b75f697a1adaa5e4af34ffba7629c710931ab1dac33bab59ecf228080bb"
model_sha256="496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4"
config_sha256="5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f"
install_root="$HOME/Library/Application Support/Neuroma/Kokoro"
repository_directory="$install_root/Kokoro-FastAPI-$short_commit"
repository_marker="$install_root/repository-path"
uv_marker="$install_root/uv-path"

progress() {
    percent=$1
    shift
    printf 'NEUROMA_PROGRESS|%s|%s\n' "$percent" "$*"
}

stage() {
    printf 'NEUROMA_SETUP: %s\n' "$*"
}

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

if [ "$(/usr/bin/uname -s)" != "Darwin" ]; then
    fail "The macOS Kokoro installer can only run on macOS."
fi
if [ "$(/usr/bin/uname -m)" != "arm64" ]; then
    fail "Automatic local Kokoro requires Apple Silicon. Configure a remote endpoint on Intel Macs."
fi
macos_major=$(/usr/bin/sw_vers -productVersion | /usr/bin/cut -d. -f1)
if [ "$macos_major" -lt 13 ]; then
    fail "Automatic local Kokoro requires macOS 13 or newer."
fi

progress 2 "Inspecting this macOS account"
/bin/mkdir -p "$install_root"
staging=$(/usr/bin/mktemp -d "$install_root/staging.XXXXXX")
cleanup() {
    case "$staging" in
        "$install_root"/staging.*) /bin/rm -rf "$staging" ;;
    esac
}
trap cleanup EXIT HUP INT TERM

uv_path=""
if [ -f "$uv_marker" ]; then
    IFS= read -r marked_uv < "$uv_marker"
    if [ -x "$marked_uv" ]; then uv_path=$marked_uv; fi
fi
if [ -z "$uv_path" ] && [ -x "$install_root/bin/uv" ]; then
    uv_path="$install_root/bin/uv"
fi
if [ -z "$uv_path" ] && command -v uv >/dev/null 2>&1; then
    uv_path=$(command -v uv)
fi
if [ -z "$uv_path" ]; then
    progress 4 "Downloading pinned Astral uv $uv_version installer"
    uv_installer="$staging/uv-install.sh"
    /usr/bin/curl --fail --location --silent --show-error \
        "https://astral.sh/uv/$uv_version/install.sh" --output "$uv_installer"
    actual_uv_sha=$(/usr/bin/shasum -a 256 "$uv_installer" | /usr/bin/awk '{print $1}')
    if [ "$actual_uv_sha" != "$uv_installer_sha256" ]; then
        fail "The Astral uv installer checksum did not match."
    fi
    progress 6 "Installing the private Astral uv runtime"
    /usr/bin/env UV_UNMANAGED_INSTALL="$install_root/bin" UV_NO_MODIFY_PATH=1 \
        /bin/sh "$uv_installer"
    uv_path="$install_root/bin/uv"
fi
if [ ! -x "$uv_path" ]; then fail "Neuroma could not locate the uv runtime."; fi
progress 10 "Astral uv runtime ready"

model_path="$repository_directory/api/src/models/v1_0/kokoro-v1_0.pth"
model_config_path="$repository_directory/api/src/models/v1_0/config.json"
environment_path="$repository_directory/.venv/bin/python"
already_installed=false
if [ -d "$repository_directory" ] && [ -x "$environment_path" ] && \
   [ -f "$model_path" ] && [ -f "$model_config_path" ]; then
    already_installed=true
fi

if [ ! -d "$repository_directory" ]; then
    progress 12 "Downloading pinned Kokoro-FastAPI source"
    archive="$staging/kokoro.zip"
    expanded="$staging/expanded"
    /bin/mkdir -p "$expanded"
    /usr/bin/curl --fail --location --silent --show-error \
        "https://github.com/remsky/Kokoro-FastAPI/archive/$kokoro_commit.zip" \
        --output "$archive"
    progress 22 "Extracting Kokoro source"
    /usr/bin/ditto -x -k "$archive" "$expanded"
    archive_root=""
    for candidate in "$expanded"/*; do
        if [ -d "$candidate" ]; then archive_root=$candidate; break; fi
    done
    if [ -z "$archive_root" ]; then fail "The Kokoro archive did not contain a source directory."; fi
    /bin/mv "$archive_root" "$repository_directory"
fi
progress 25 "Kokoro source ready"

export UV_PROJECT_ENVIRONMENT="$repository_directory/.venv"
export UV_PYTHON_INSTALL_DIR="$install_root/python"
export UV_CACHE_DIR="$install_root/cache"
export UV_NO_PROGRESS=1

if [ "$already_installed" != true ]; then
    progress 30 "Installing Python and Kokoro dependencies"
    (
        cd "$repository_directory"
        "$uv_path" sync --extra cpu --python 3.12
    )
    progress 68 "Python and Kokoro dependencies ready"
else
    progress 68 "Existing Python and Kokoro dependencies verified"
fi

/bin/mkdir -p "$(dirname "$model_path")"
check_sha256() {
    checked_path=$1
    expected_sha=$2
    [ -f "$checked_path" ] || return 1
    actual_sha=$(/usr/bin/shasum -a 256 "$checked_path" | /usr/bin/awk '{print $1}')
    [ "$actual_sha" = "$expected_sha" ]
}

if ! check_sha256 "$model_path" "$model_sha256"; then
    progress 70 "Downloading Kokoro voice model"
    /usr/bin/curl --fail --location --silent --show-error \
        "https://github.com/remsky/Kokoro-FastAPI/releases/download/v0.1.4/kokoro-v1_0.pth" \
        --output "$model_path"
fi
progress 92 "Kokoro voice model ready"

if ! check_sha256 "$model_config_path" "$config_sha256"; then
    progress 92 "Downloading model configuration"
    /usr/bin/curl --fail --location --silent --show-error \
        "https://github.com/remsky/Kokoro-FastAPI/releases/download/v0.1.4/config.json" \
        --output "$model_config_path"
fi
if ! check_sha256 "$model_path" "$model_sha256" || \
   ! check_sha256 "$model_config_path" "$config_sha256"; then
    fail "Kokoro model verification failed."
fi
progress 95 "Kokoro model verified"

printf '%s\n' "$repository_directory" > "$repository_marker"
printf '%s\n' "$uv_path" > "$uv_marker"

progress 96 "Saving local Neuroma voice configuration"
config_path="$app_directory/neuroma.json"
(
    cd "$repository_directory"
    "$uv_path" run --no-sync python - "$config_path" <<'PY'
import json
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
configuration = {}
if path.exists():
    try:
        configuration = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        configuration = {}
configuration["kokoroUrl"] = "http://127.0.0.1:8880"
configuration.setdefault("voice", "af_bella")
configuration.setdefault("speed", 1.0)
path.write_text(json.dumps(configuration, indent=2) + "\n", encoding="utf-8")
PY
)

progress 97 "Starting the private Apple Silicon Kokoro service"
/bin/sh "$app_directory/Start-Neuroma-Kokoro.sh" "$install_root"
progress 100 "Local Kokoro is ready"
stage "Apple Silicon MPS acceleration enabled"
