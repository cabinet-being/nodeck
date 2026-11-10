#!/usr/bin/env bash
# Builds nodeck-app:<VERSION> (linux/amd64) and assembles release/nodeck-<VERSION>-amd64.tar.gz.
# Nothing is pushed anywhere. The target host needs only docker.
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$project_root"
version="$(tr -d '[:space:]' < VERSION)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.]+)?$ ]] || { printf 'Bad VERSION: %s\n' "$version" >&2; exit 1; }
image="nodeck-app:$version"
name="nodeck-$version"
# The archive extracts to nodeck/ so a new version unpacks over an existing install.
# It carries no data: only empty placeholder dirs and .gitkeep files, never anything in
# volumes/mysql_data, and no .env, so unpacking never overwrites user data.
top="nodeck"
out="release/$top"

docker build --platform linux/amd64 --build-arg "NODECK_VERSION=$version" -t "$image" -f Dockerfile .

rm -rf -- "$out" "release/$name" "release/$name-amd64.tar.gz"
mkdir -p "$out/images" "$out/scripts" "$out/database"
# volumes/mysql_data is not shipped: Docker creates it on first start, and shipping it
# would make an in-place upgrade try to touch a directory owned by the MySQL user.
mkdir -p "$out/backups"
: > "$out/backups/.gitkeep"
for d in cache cards config uploads user-data models; do
    mkdir -p "$out/volumes/app_data/$d"
    : > "$out/volumes/app_data/$d/.gitkeep"
done

sed "s/__NODECK_VERSION__/$version/" deploy/docker-compose.release.yml > "$out/docker-compose.yml"
cp VERSION "$out/"
# 3306 is often taken on NAS hosts, so the bundle defaults to 33306.
sed 's/^MYSQL_HOST_PORT=3306$/MYSQL_HOST_PORT=33306/' .env.example > "$out/.env.example"
cp -R database/init "$out/database/init"
cp scripts/library-backup.sh scripts/library-backup-docker.sh scripts/export-db.sh scripts/install-wd-tagger.sh \
    scripts/install-whisper-models.sh scripts/install-models.sh "$out/scripts/"
chmod +x "$out"/scripts/*.sh

docker save -o "$out/images/nodeck-app-$version-amd64.tar" "$image"

(cd "$out" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)
tar -C release -czf "release/$name-amd64.tar.gz" "$top"

du -h "release/$name-amd64.tar.gz"
printf 'Release bundle: release/%s-amd64.tar.gz\n' "$name"
