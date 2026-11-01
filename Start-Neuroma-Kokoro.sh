#!/bin/sh
set -eu

if [ "$(uname -s)" = "Darwin" ]; then
    default_install_root="$HOME/Library/Application Support/Neuroma/Kokoro"
else
    default_install_root="${XDG_DATA_HOME:-$HOME/.local/share}/Neuroma/Kokoro"
fi
install_root=${1:-"$default_install_root"}
endpoint="http://127.0.0.1:8880"

test_kokoro() {
    if command -v curl >/dev/null 2>&1; then
        curl --fail --silent --max-time 2 "$endpoint/openapi.json" >/dev/null 2>&1
    else
        wget --quiet --timeout=2 --tries=1 --output-document=/dev/null "$endpoint/openapi.json" >/dev/null 2>&1
    fi
}

if test_kokoro; then
    printf '%s\n' "Kokoro is already ready on $endpoint"
    exit 0
fi

repository_marker="$install_root/repository-path"
uv_marker="$install_root/uv-path"
if [ ! -f "$repository_marker" ] || [ ! -f "$uv_marker" ]; then
    printf '%s\n' "Local Kokoro has not been installed." >&2
    exit 1
fi

IFS= read -r repository_directory < "$repository_marker"
IFS= read -r uv_path < "$uv_marker"
if [ ! -d "$repository_directory" ]; then
    printf '%s\n' "The Kokoro source directory is missing." >&2
    exit 1
fi
if [ ! -x "$uv_path" ]; then
    printf '%s\n' "The uv runtime is missing." >&2
    exit 1
fi

log_directory="$install_root/logs"
mkdir -p "$log_directory"
stdout_log="$log_directory/kokoro.stdout.log"
stderr_log="$log_directory/kokoro.stderr.log"
pid_path="$install_root/kokoro.pid"

device_type=""
if [ -f "$install_root/device" ]; then IFS= read -r device_type < "$install_root/device" || true; fi
if [ -z "$device_type" ]; then
    if [ "$(uname -s)" = "Darwin" ] && [ "$(uname -m)" = "arm64" ]; then device_type=mps; else device_type=cpu; fi
fi
case "$device_type" in
    mps|cuda) use_gpu=true ;;
    *) use_gpu=false; device_type=cpu ;;
esac

(
    cd "$repository_directory"
    nohup env \
        PYTHONUTF8=1 \
        PROJECT_ROOT="$repository_directory" \
        USE_GPU="$use_gpu" \
        DEVICE_TYPE="$device_type" \
        PYTORCH_ENABLE_MPS_FALLBACK=1 \
        PYTHONPATH="$repository_directory:$repository_directory/api" \
        MODEL_DIR=src/models \
        VOICES_DIR=src/voices/v1_0 \
        WEB_PLAYER_PATH="$repository_directory/web" \
        API_LOG_LEVEL=WARNING \
        UV_PROJECT_ENVIRONMENT="$repository_directory/.venv" \
        UV_PYTHON_INSTALL_DIR="$install_root/python" \
        UV_CACHE_DIR="$install_root/cache" \
        "$uv_path" run --no-sync uvicorn api.src.main:app \
        --host 127.0.0.1 --port 8880 \
        >"$stdout_log" 2>"$stderr_log" </dev/null &
    printf '%s\n' "$!" > "$pid_path"
)

IFS= read -r process_id < "$pid_path"
attempt=0
while [ "$attempt" -lt 240 ]; do
    if ! kill -0 "$process_id" 2>/dev/null; then
        printf '%s\n' "Kokoro exited during startup." >&2
        if [ -f "$stderr_log" ]; then tail -n 20 "$stderr_log" >&2; fi
        exit 1
    fi
    if test_kokoro; then
        printf '%s\n' "Kokoro is ready on $endpoint"
        exit 0
    fi
    attempt=$((attempt + 1))
    sleep 0.5
done

kill "$process_id" 2>/dev/null || true
printf '%s\n' "Kokoro did not become ready within two minutes. See $stderr_log" >&2
exit 1
