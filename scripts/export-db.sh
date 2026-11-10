#!/usr/bin/env bash
# Writes a readable, portable SQL dump of the nodeck database without stopping
# the backend. Usage: scripts/export-db.sh [OUTPUT_DIR (default ./backups)]
# Needs only docker; credentials come from the MySQL container environment.
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$project_root"
umask 077
out_dir="${1:-$project_root/backups}"
mkdir -p -- "$out_dir"
out_dir="$(cd -- "$out_dir" && pwd -P)"
target="$out_dir/nodeck-db-$(date -u +%Y%m%dT%H%M%SZ).sql"
temp="$target.partial"
trap 'rm -f -- "$temp"' EXIT

docker compose exec -T mysql sh -c \
    'MYSQL_PWD="$MYSQL_PASSWORD" exec mysqldump -u "$MYSQL_USER" --single-transaction --routines --triggers --hex-blob --set-gtid-purged=OFF --no-tablespaces "$MYSQL_DATABASE"' \
    > "$temp"
[[ -s "$temp" ]] || { printf 'Error: empty dump.\n' >&2; exit 1; }
mv -- "$temp" "$target"
trap - EXIT
printf '%s\n' "$target"
