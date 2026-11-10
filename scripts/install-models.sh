#!/usr/bin/env bash
# Installs the pinned WD tagger and Whisper models using only Docker (no host
# curl/sha256sum needed). Usage: scripts/install-models.sh [wd|whisper|all]
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$project_root"
what="${1:-all}"
alpine_image="alpine:3.20"

case "$what" in
    wd) steps='bash scripts/install-wd-tagger.sh' ;;
    whisper) steps='bash scripts/install-whisper-models.sh' ;;
    all) steps='bash scripts/install-wd-tagger.sh && bash scripts/install-whisper-models.sh' ;;
    *) printf 'Usage: %s [wd|whisper|all]\n' "$0" >&2; exit 2 ;;
esac

command -v docker >/dev/null || { printf 'docker is required\n' >&2; exit 1; }
mkdir -p volumes/app_data/models

docker run --rm -v "$project_root":/work -w /work "$alpine_image" \
    sh -c "apk add --no-cache bash curl coreutils ca-certificates >/dev/null && $steps"

cat <<MSG

Models installed under volumes/app_data/models/.
No backend restart is needed: the analysis workers re-check for the model
files on each cycle and load them on demand.
MSG
if [[ "$what" != whisper ]]; then
    cat <<'MSG'
WD tagger: the backend seeds its tag vocabulary automatically (about 10 s) the
first time it sees the model files; no manual step is needed. To seed by hand:
  docker compose run --rm backend seed-wd-tags
MSG
fi
