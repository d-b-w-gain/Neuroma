#!/bin/sh
set -eu

script_directory=$(CDPATH= cd "$(dirname "$0")" && pwd)
app_directory=${1:-"$script_directory"}
app_directory=$(CDPATH= cd "$app_directory" && pwd)
kokoro_commit="f901ed81ff9910b80ab24f800a245873221a5445"
short_commit="f901ed81ff99"
uv_version="0.12.10"
uv_installer_sha256="a3196b75f697a1adaa5e4af34ffba7629c710931ab1dac33bab59ecf228080bb"
model_sha256="496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4"
config_sha256="5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f"

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

operating_system=$(uname -s)
machine=$(uname -m)
case "$operating_system" in
    Darwin)
        if [ "$machine" != "arm64" ]; then
            fail "Automatic local Kokoro requires Apple Silicon. Configure a remote endpoint on Intel Macs."
        fi
        macos_major=$(/usr/bin/sw_vers -productVersion | cut -d. -f1)
        if [ "$macos_major" -lt 13 ]; then
            fail "Automatic local Kokoro requires macOS 13 or newer."
        fi
        platform_label="macOS"
        install_root="$HOME/Library/Application Support/Neuroma/Kokoro"
        torch_extra=cpu
        device_type=mps
        ;;
    Linux)
        case "$machine" in
            x86_64|amd64|aarch64|arm64) ;;
            *) fail "Automatic local Kokoro on Linux requires an x86_64 or ARM64 system." ;;
        esac
        platform_label="Linux"
        install_root="${XDG_DATA_HOME:-$HOME/.local/share}/Neuroma/Kokoro"
        # CPU is the safe default; NEUROMA_KOKORO_DEVICE=cuda opts into NVIDIA acceleration.
        case "${NEUROMA_KOKORO_DEVICE:-cpu}" in
            cpu) torch_extra=cpu; device_type=cpu ;;
            cuda) torch_extra=gpu; device_type=cuda ;;
            *) fail "NEUROMA_KOKORO_DEVICE must be cpu or cuda." ;;
        esac
        # Kokoro's Japanese phonemizer (pyopenjtalk) is compiled from source on Linux.
        first_command() {
            for candidate in "$@"; do
                if command -v "$candidate" >/dev/null 2>&1; then command -v "$candidate"; return 0; fi
            done
            return 1
        }
        if [ -z "${CXX:-}" ]; then CXX=$(first_command c++ g++ clang++ || true); fi
        if [ -z "${CC:-}" ]; then CC=$(first_command cc gcc clang || true); fi
        if [ -z "$CXX" ] || [ -z "$CC" ]; then
            fail "Local Kokoro needs a C/C++ compiler on Linux. Install build-essential (Debian/Ubuntu), gcc-c++ (Fedora), or base-devel (Arch), then press I again."
        fi
        export CC CXX
        # A distribution Python usually lacks the headers needed for that build.
        export UV_PYTHON_PREFERENCE=only-managed
        ;;
    *)
        fail "Automatic local Kokoro is supported on macOS and Linux only."
        ;;
esac

download() {
    if command -v curl >/dev/null 2>&1; then
        curl --fail --location --silent --show-error "$1" --output "$2"
    elif command -v wget >/dev/null 2>&1; then
        wget --quiet --output-document="$2" "$1"
    else
        fail "Neuroma needs curl or wget to download Kokoro."
    fi
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

for required_tool in tar awk mktemp; do
    command -v "$required_tool" >/dev/null 2>&1 || fail "Neuroma needs '$required_tool' to install Kokoro."
done

repository_directory="$install_root/Kokoro-FastAPI-$short_commit"
repository_marker="$install_root/repository-path"
uv_marker="$install_root/uv-path"
device_marker="$install_root/device"

progress 2 "Inspecting this $platform_label account"
mkdir -p "$install_root"
staging=$(mktemp -d "$install_root/staging.XXXXXX")
cleanup() {
    case "$staging" in
        "$install_root"/staging.*) rm -rf "$staging" ;;
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
    download "https://astral.sh/uv/$uv_version/install.sh" "$uv_installer"
    actual_uv_sha=$(sha256_of "$uv_installer")
    if [ "$actual_uv_sha" != "$uv_installer_sha256" ]; then
        fail "The Astral uv installer checksum did not match."
    fi
    progress 6 "Installing the private Astral uv runtime"
    env UV_UNMANAGED_INSTALL="$install_root/bin" UV_NO_MODIFY_PATH=1 \
        sh "$uv_installer"
    uv_path="$install_root/bin/uv"
fi
if [ ! -x "$uv_path" ]; then fail "Neuroma could not locate the uv runtime."; fi
progress 10 "Astral uv runtime ready"

model_path="$repository_directory/api/src/models/v1_0/kokoro-v1_0.pth"
model_config_path="$repository_directory/api/src/models/v1_0/config.json"
environment_path="$repository_directory/.venv/bin/python"
already_installed=false
installed_device=""
if [ -f "$device_marker" ]; then IFS= read -r installed_device < "$device_marker" || true; fi
if [ -z "$installed_device" ] && [ "$operating_system" = "Darwin" ]; then installed_device=mps; fi
if [ -d "$repository_directory" ] && [ -x "$environment_path" ] && \
   [ -f "$model_path" ] && [ -f "$model_config_path" ] && \
   [ "$installed_device" = "$device_type" ]; then
    already_installed=true
fi

if [ ! -d "$repository_directory" ]; then
    progress 12 "Downloading pinned Kokoro-FastAPI source"
    archive="$staging/kokoro.tar.gz"
    expanded="$staging/expanded"
    mkdir -p "$expanded"
    download "https://github.com/remsky/Kokoro-FastAPI/archive/$kokoro_commit.tar.gz" "$archive"
    progress 22 "Extracting Kokoro source"
    tar -xzf "$archive" -C "$expanded"
    archive_root=""
    for candidate in "$expanded"/*; do
        if [ -d "$candidate" ]; then archive_root=$candidate; break; fi
    done
    if [ -z "$archive_root" ]; then fail "The Kokoro archive did not contain a source directory."; fi
    mv "$archive_root" "$repository_directory"
fi
progress 25 "Kokoro source ready"

export UV_PROJECT_ENVIRONMENT="$repository_directory/.venv"
export UV_PYTHON_INSTALL_DIR="$install_root/python"
export UV_CACHE_DIR="$install_root/cache"
export UV_NO_PROGRESS=1

if [ "$already_installed" != true ]; then
    # Rebuild an environment left by an earlier attempt on a distribution Python.
    if [ "${UV_PYTHON_PREFERENCE:-}" = "only-managed" ] && [ -f "$repository_directory/.venv/pyvenv.cfg" ] && \
       ! grep -q "^home = $install_root/python/" "$repository_directory/.venv/pyvenv.cfg"; then
        rm -rf "$repository_directory/.venv"
    fi
    progress 30 "Installing Python and Kokoro dependencies"
    (
        cd "$repository_directory"
        "$uv_path" sync --extra "$torch_extra" --python 3.12
    )
    progress 68 "Python and Kokoro dependencies ready"
else
    progress 68 "Existing Python and Kokoro dependencies verified"
fi

mkdir -p "$(dirname "$model_path")"
check_sha256() {
    checked_path=$1
    expected_sha=$2
    [ -f "$checked_path" ] || return 1
    actual_sha=$(sha256_of "$checked_path")
    [ "$actual_sha" = "$expected_sha" ]
}

if ! check_sha256 "$model_path" "$model_sha256"; then
    progress 70 "Downloading Kokoro voice model"
    download "https://github.com/remsky/Kokoro-FastAPI/releases/download/v0.1.4/kokoro-v1_0.pth" "$model_path"
fi
progress 92 "Kokoro voice model ready"

if ! check_sha256 "$model_config_path" "$config_sha256"; then
    progress 92 "Downloading model configuration"
    download "https://github.com/remsky/Kokoro-FastAPI/releases/download/v0.1.4/config.json" "$model_config_path"
fi
if ! check_sha256 "$model_path" "$model_sha256" || \
   ! check_sha256 "$model_config_path" "$config_sha256"; then
    fail "Kokoro model verification failed."
fi
progress 95 "Kokoro model verified"

printf '%s\n' "$repository_directory" > "$repository_marker"
printf '%s\n' "$uv_path" > "$uv_marker"
printf '%s\n' "$device_type" > "$device_marker"

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

progress 97 "Starting the private local Kokoro service"
sh "$script_directory/Start-Neuroma-Kokoro.sh" "$install_root"
progress 100 "Local Kokoro is ready"
case "$device_type" in
    mps) stage "Apple Silicon MPS acceleration enabled" ;;
    cuda) stage "NVIDIA CUDA acceleration enabled" ;;
    *) stage "Running on the CPU" ;;
esac
