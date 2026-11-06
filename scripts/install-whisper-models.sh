#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd -- "$script_dir/.." && pwd)"
model_dir="${1:-${NODECK_WHISPER_MODEL_DIR:-$project_root/volumes/app_data/models/whisper}}"
whisper_revision="5359861c739e955e79d9a303bcbc70fb988958b1"
vad_revision="9ffd54a1e1ee413ddf265af9913beaf518d1639b"
whisper_base_url="https://huggingface.co/ggerganov/whisper.cpp/resolve/$whisper_revision"
vad_base_url="https://huggingface.co/ggml-org/whisper-vad/resolve/$vad_revision"
whisper_sha256="60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"
vad_sha256="2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987"
temporary_files=()

cleanup() {
    for path in "${temporary_files[@]}"; do
        rm -f -- "$path"
    done
}
trap cleanup EXIT

download_verified() {
    local name="$1" expected="$2" base_url="$3" target temporary
    target="$model_dir/$name"
    if [[ -f "$target" ]] && printf '%s  %s\n' "$expected" "$target" | sha256sum --check --status; then
        printf 'Verified %s\n' "$target"
        return
    fi

    temporary="$(mktemp "$model_dir/.$name.XXXXXX")"
    temporary_files+=("$temporary")
    curl --fail --silent --show-error --location --retry 3 --output "$temporary" "$base_url/$name"
    printf '%s  %s\n' "$expected" "$temporary" | sha256sum --check --status || {
        printf 'Checksum mismatch for %s\n' "$name" >&2
        return 1
    }
    mv -- "$temporary" "$target"
    printf 'Installed %s\n' "$target"
}

command -v curl >/dev/null || { printf 'curl is required\n' >&2; exit 1; }
command -v sha256sum >/dev/null || { printf 'sha256sum is required\n' >&2; exit 1; }
mkdir -p -- "$model_dir"
download_verified ggml-base.bin "$whisper_sha256" "$whisper_base_url"
download_verified ggml-silero-v6.2.0.bin "$vad_sha256" "$vad_base_url"
