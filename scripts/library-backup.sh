#!/usr/bin/env bash
set -euo pipefail

# Run from any directory. Credentials stay in the MySQL container environment.
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$project_root"
umask 077

usage() {
    cat <<'EOF'
Usage:
  scripts/library-backup.sh backup BACKUP_PARENT_DIRECTORY
  scripts/library-backup.sh restore BACKUP_DIRECTORY --confirm-restore

Backup stops the backend while it records one MySQL snapshot and app_data.tar.
It leaves the backend stopped so no writes can stale the snapshot before upgrade.
If backup fails, it restarts a backend that it stopped. Keep backups outside volumes/.
The destination receives a new nodeck-UTC_TIMESTAMP directory with a manifest.

Restore requires the backend to be stopped. It verifies both SHA-256 hashes,
replaces the configured MySQL database, and moves the current app_data directory
aside as app_data.before-restore-UTC_TIMESTAMP before installing saved files.
Inspect the manifest and keep a fresh backup before restoring.
EOF
}

die() { printf 'Error: %s\n' "$*" >&2; exit 1; }

backup_temp=''
restart_backend_on_failure=false
restore_stage=''
restore_previous=''
restore_db_replaced=false
cleanup_backup_failure() {
    local status=$?
    if [[ $status -ne 0 ]]; then
        if [[ -n "$backup_temp" ]]; then rm -rf -- "$backup_temp"; fi
        if [[ "$restart_backend_on_failure" == true ]]; then
            docker compose start backend >/dev/null || printf 'Warning: backend could not be restarted.\n' >&2
        fi
    fi
}

cleanup_restore_failure() {
    local status=$?
    if [[ $status -ne 0 ]]; then
        if [[ -n "$restore_previous" && -d "$restore_previous" && ! -e volumes/app_data ]]; then
            mv -- "$restore_previous" volumes/app_data || true
        fi
        if [[ -n "$restore_stage" ]]; then rm -rf -- "$restore_stage"; fi
        if [[ "$restore_db_replaced" == true ]]; then
            printf 'Warning: database restore started. Keep the backend stopped and retry restore.\n' >&2
        fi
    fi
}

require_tools() {
    for tool in docker tar sha256sum awk mktemp date find wc; do
        command -v "$tool" >/dev/null 2>&1 || die "Missing required tool: $tool"
    done
    docker compose version >/dev/null 2>&1 || die 'Docker Compose is unavailable.'
}

mysql_env() {
    docker compose exec -T mysql sh -c 'printf "%s" "$MYSQL_DATABASE"'
}

mysql_query() {
    printf '%s\n' "$1" | docker compose exec -T mysql sh -c \
        'MYSQL_PWD="$MYSQL_PASSWORD" exec mysql -u "$MYSQL_USER" "$MYSQL_DATABASE" -N -s'
}

backend_running() {
    local container
    container="$(docker compose ps -q backend)"
    [[ -n "$container" ]] && [[ "$(docker inspect -f '{{.State.Running}}' "$container")" == true ]]
}

mysql_running() {
    local container
    container="$(docker compose ps -q mysql)"
    [[ -n "$container" ]] && [[ "$(docker inspect -f '{{.State.Running}}' "$container")" == true ]]
}

manifest_value() {
    local key="$1" file="$2"
    awk -v prefix="$key=" 'index($0, prefix) == 1 { print substr($0, length(prefix) + 1); exit }' "$file"
}

table_count() {
    local table="$1" exists
    [[ "$table" =~ ^[A-Za-z0-9_]+$ ]] || die 'Invalid table name.'
    exists="$(mysql_query "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '$table';")"
    if [[ "$exists" == 0 ]]; then printf '0\n'
    else mysql_query "SELECT COUNT(*) FROM \`$table\`;"; fi
}

write_counts() {
    local output="$1" table count
    for table in cards card_relations deck_cards decks tags sources tag_aliases card_tags card_sources card_tag_suggestions card_image_analysis_jobs; do
        count="$(table_count "$table")"
        [[ "$count" =~ ^[0-9]+$ ]] || die "Invalid row count for $table"
        printf 'count.%s=%s\n' "$table" "$count" >> "$output"
    done
}

starting_version() {
    local exists
    exists="$(mysql_query "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'schema_migrations';")"
    if [[ "$exists" == 0 ]]; then printf '0\n'
    else mysql_query 'SELECT COALESCE(MAX(version), 0) FROM schema_migrations;'; fi
}

verify_counts() {
    local manifest="$1" line table expected actual
    while IFS= read -r line; do
        [[ "$line" == count.* ]] || continue
        table="${line%%=*}"
        table="${table#count.}"
        expected="${line#*=}"
        [[ "$table" =~ ^[A-Za-z0-9_]+$ && "$expected" =~ ^[0-9]+$ ]] || die 'Invalid count in manifest.'
        actual="$(table_count "$table")"
        [[ "$actual" == "$expected" ]] || die "Restored $table has $actual rows; expected $expected."
    done < "$manifest"
}

backup() {
    [[ $# -eq 1 ]] || { usage; exit 2; }
    require_tools
    mysql_running || die 'The mysql service must be running.'
    [[ -d volumes/app_data && ! -L volumes/app_data ]] || die 'volumes/app_data must be a real directory.'

    local parent="$1" parent_abs app_abs stamp temp final db version
    mkdir -p -- "$parent"
    parent_abs="$(cd "$parent" && pwd -P)"
    app_abs="$(cd volumes/app_data && pwd -P)"
    case "$parent_abs/" in
        "$app_abs/"*) die 'Backup destination cannot be inside app_data.' ;;
    esac
    case "$parent_abs/" in
        "$project_root/volumes/"*) die 'Backup destination cannot be inside volumes/.' ;;
    esac

    db="$(mysql_env)"
    [[ "$db" =~ ^[A-Za-z0-9_]+$ ]] || die 'Database name must contain only letters, digits, and underscores.'
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    temp="$(mktemp -d "$parent_abs/.nodeck-backup.XXXXXXXX")"
    final="$parent_abs/nodeck-$stamp-${temp##*.}"
    backup_temp="$temp"
    trap cleanup_backup_failure EXIT

    if backend_running; then
        restart_backend_on_failure=true
        docker compose stop backend >/dev/null
    fi
    version="$(starting_version)"
    [[ "$version" =~ ^[0-9]+$ ]] || die 'Invalid schema version.'

    docker compose exec -T mysql sh -c \
        'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysqldump -u root --single-transaction --quick --routines --triggers --events --hex-blob --set-gtid-purged=OFF --no-tablespaces "$MYSQL_DATABASE"' \
        > "$temp/database.sql"
    tar -C volumes -cf "$temp/app_data.tar" app_data

    {
        printf 'format=1\ncreated_utc=%s\ndatabase=%s\nstartingVersion=%s\n' "$stamp" "$db" "$version"
        printf 'database_sha256=%s\n' "$(sha256sum "$temp/database.sql" | awk '{print $1}')"
        printf 'app_data_sha256=%s\n' "$(sha256sum "$temp/app_data.tar" | awk '{print $1}')"
        printf 'app_file_count=%s\n' "$(find volumes/app_data -type f -printf '.' | wc -c | awk '{print $1}')"
    } > "$temp/manifest.txt"
    write_counts "$temp/manifest.txt"
    mv -- "$temp" "$final"
    backup_temp=''
    restart_backend_on_failure=false
    trap - EXIT
    printf 'Backup created: %s\nBackend remains stopped for migration.\n' "$final"
}

restore() {
    [[ $# -eq 2 && "$2" == --confirm-restore ]] || { usage; exit 2; }
    require_tools
    mysql_running || die 'The mysql service must be running.'
    backend_running && die 'Stop the backend before restoring.'

    local saved="$1" manifest db active_db expected actual stamp staged previous file_count
    saved="$(cd "$saved" && pwd -P)" || die 'Backup directory does not exist.'
    manifest="$saved/manifest.txt"
    [[ -f "$manifest" && -f "$saved/database.sql" && -f "$saved/app_data.tar" ]] || die 'Backup is incomplete.'
    [[ "$(manifest_value format "$manifest")" == 1 ]] || die 'Unsupported backup format.'
    [[ "$(manifest_value startingVersion "$manifest")" =~ ^[0-9]+$ ]] || die 'Invalid startingVersion in manifest.'
    db="$(manifest_value database "$manifest")"
    active_db="$(mysql_env)"
    [[ "$db" =~ ^[A-Za-z0-9_]+$ && "$db" == "$active_db" ]] || die 'Backup database differs from configured database.'
    for part in database app_data; do
        expected="$(manifest_value "${part}_sha256" "$manifest")"
        [[ "$expected" =~ ^[0-9a-f]{64}$ ]] || die "Invalid $part hash in manifest."
        if [[ "$part" == database ]]; then actual="$(sha256sum "$saved/database.sql" | awk '{print $1}')"
        else actual="$(sha256sum "$saved/app_data.tar" | awk '{print $1}')"; fi
        [[ "$actual" == "$expected" ]] || die "$part hash mismatch."
    done
    if tar -tf "$saved/app_data.tar" | awk '$0 !~ /^app_data\/?$/ && $0 !~ /^app_data\// { bad=1 } /\.\.\// { bad=1 } END { exit !bad }'; then
        die 'Archive contains paths outside app_data.'
    fi
    expected="$(manifest_value app_file_count "$manifest")"
    [[ "$expected" =~ ^[0-9]+$ ]] || die 'Invalid app_file_count in manifest.'
    [[ -d volumes/app_data ]] || die 'volumes/app_data does not exist.'
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    staged="$(mktemp -d "$project_root/volumes/.app_data_restore.XXXXXXXX")"
    restore_stage="$staged"
    trap cleanup_restore_failure EXIT
    tar -C "$staged" -xf "$saved/app_data.tar"
    [[ -d "$staged/app_data" ]] || die 'Archive does not contain app_data.'
    file_count="$(find "$staged/app_data" -type f -printf '.' | wc -c | awk '{print $1}')"
    [[ "$file_count" == "$expected" ]] || die 'Archive file count differs from manifest.'
    previous="$project_root/volumes/app_data.before-restore-$stamp"
    [[ ! -e "$previous" ]] || die "Previous directory already exists: $previous"

    restore_db_replaced=true
    printf 'DROP DATABASE IF EXISTS `%s`; CREATE DATABASE `%s`;\n' "$db" "$db" |
        docker compose exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -u root'
    docker compose exec -T mysql sh -c \
        'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -u root "$MYSQL_DATABASE"' < "$saved/database.sql"
    verify_counts "$manifest"
    restore_previous="$previous"
    mv -- volumes/app_data "$previous"
    mv -- "$staged/app_data" volumes/app_data
    rmdir -- "$staged"
    restore_stage=''
    restore_previous=''
    restore_db_replaced=false
    trap - EXIT
    printf 'Restore complete. Previous app files: %s\n' "$previous"
}

case "${1:-}" in
    backup) shift; backup "$@" ;;
    restore) shift; restore "$@" ;;
    -h|--help|help) usage ;;
    *) usage; exit 2 ;;
esac
