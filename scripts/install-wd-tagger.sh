#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd -- "$script_dir/.." && pwd)"
model_dir="${1:-${NODECK_TAGGER_MODEL_DIR:-$project_root/volumes/app_data/models/wd-vit-tagger-v3}}"
revision="7f6b584d0bd3f55c4531f14ba3d4761b2bccdc0f"
base_url="https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/$revision"
model_sha256="35f23693620b668f4d53fd3c62bf65e40af739bc52c7eb0fbc49258b58d065b6"
labels_sha256="298633d94d0031d2081c0893f29c82eab7f0df00b08483ba8f29d1e979441217"
temporary_files=()

cleanup() {
    for path in "${temporary_files[@]}"; do
        rm -f -- "$path"
    done
}
trap cleanup EXIT

download_verified() {
    local name="$1" expected="$2" target temporary
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
download_verified model.onnx "$model_sha256"
download_verified selected_tags.csv "$labels_sha256"
