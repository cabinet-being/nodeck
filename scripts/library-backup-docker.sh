#!/usr/bin/env bash
# Runs scripts/library-backup.sh inside a container, for hosts (such as ZimaOS)
# that lack tar, awk, find -printf, sha256sum or mktemp. Needs only docker.
# Same arguments as library-backup.sh:
#   scripts/library-backup-docker.sh backup BACKUP_PARENT_DIRECTORY
#   scripts/library-backup-docker.sh restore BACKUP_DIRECTORY --confirm-restore
# The project and backup directories are mounted at their host paths so that
# `docker compose` inside the container resolves bind mounts for the host daemon.
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$project_root"
cli_image="docker:27.5.1-cli"

[[ $# -ge 2 ]] || { printf 'Usage: %s backup DIR | restore DIR --confirm-restore\n' "$0" >&2; exit 2; }
mode="$1"; target="$2"; shift 2
[[ "$mode" == backup || "$mode" == restore ]] || { printf 'Mode must be backup or restore.\n' >&2; exit 2; }
[[ "$mode" == restore ]] || mkdir -p -- "$target"
# Resolve the absolute path through the parent: backup directories are created by root
# with mode 700, so a non-root caller may not be able to cd into them.
target="$(cd -- "$(dirname -- "$target")" && pwd -P)/$(basename -- "$target")"

docker run --rm \
    -v /var/run/docker.sock:/var/run/docker.sock \
    -v "$project_root":"$project_root" \
    -v "$target":"$target" \
    -w "$project_root" \
    -e COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-$(basename "$project_root" | tr 'A-Z' 'a-z' | tr -cd 'a-z0-9_-')}" \
    "$cli_image" \
    sh -c 'apk add --no-cache bash coreutils tar findutils gawk >/dev/null && exec bash scripts/library-backup.sh "$@"' _ "$mode" "$target" "$@"
