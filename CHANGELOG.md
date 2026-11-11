# Changelog

All notable changes to nodeck are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Learned custom tags.** nodeck learns each of your custom tags from the cards that already carry it (at least 5, added by hand or accepted) and proposes it on other cards. The visual analysis now keeps every card's WD label probabilities as features; a per-tag classifier trained on them proposes a tag only where cross-validation on your examples shows at least 80 % estimated precision. Proposals use the source `learned` and go through the usual review: Accept, Reject, Correct, and Accept all work as before, and rejected or corrected proposals teach the model as negative examples. Models retrain in the background when examples change. `GET /api/learned-tags` shows each tag's examples, threshold, precision, and recall.
- **Background backfill for older cards.** Media analysed before this version, or never analysed, is re-analysed with low priority so it can serve as examples and receive learned tags; new uploads always go first. `NODECK_LEARNED_TAGS_BACKFILL=false` stops it, and `NODECK_LEARNED_TAGS=false` turns learned tags off.

### Changed

- Schema migration 13 (`learned-custom-tags-v1`): analysis job priority, the `learned` suggestion source, and tables for features and models. Like every migration on a populated library, it needs a current backup first (see README, Database upgrades and recovery); the backup script is unchanged.

## [0.1.2] — 2026-11-11

### Changed

- **Images are tagged first.** The automatic tagging queue now analyses still images before videos and GIFs. An image takes about a second, a video minutes (many frames), so after a bulk import images get their tags within the hour instead of waiting behind every earlier video.

### Fixed

- **ZimaOS import.** ZimaOS put the imported containers on Docker's default `bridge` network, where nodeck could not find `nodeck-mysql` and restarted forever. `docker-compose.zima.yml` now declares its own network. It also no longer contains `$` (ZimaOS substitutes variables when it stores the file, which emptied the MySQL `change-me` check; nodeck itself still refuses the placeholder).
- README troubleshooting for the ZimaOS install covers the two failures above. The install is now tested on ZimaOS 1.6.1.

## [0.1.1] — 2026-11-11

### Added

- **Install on ZimaOS from the dashboard.** `docker-compose.zima.yml` is imported through **Install a customized app → Import**; it pulls `ghcr.io/cabinet-being/nodeck:latest` and MySQL, keeps data in `/DATA/AppData/nodeck`, and shows nodeck as a dashboard tile. GitHub Actions publish the image to GHCR (`:main` for every push to `main`, `:X.Y.Z` and `:latest` for release tags).
- **Models download themselves.** On start the backend downloads the pinned WD tagger and Whisper models when they are missing (SHA-256 verified, retried while offline). `NODECK_MODEL_AUTO_DOWNLOAD=false` turns this off.
- **Empty database setup without init scripts.** The backend creates the base schema in an empty database itself, so MySQL no longer needs `database/init` mounted next to the compose file.
- The backend builds its connection string from `MYSQL_HOST`, `MYSQL_DATABASE`, `MYSQL_USER` and `MYSQL_PASSWORD` when `ConnectionStrings__Default` is not set.

### Fixed

- Uploads larger than about 30 MB (typical videos) were rejected with HTTP 413 because the server kept the framework's default request limit. The limit is now 8 GB and can be changed with `NODECK_MAX_UPLOAD_MB`.

### Known limitations

- The dashboard install has not yet been tested on a real ZimaOS device.
- Schema-changing upgrades still need a verified backup made with the backup scripts from the repository or the release bundle.

## [0.1.0] — 2026-11-11

First release.

### Added

- **Library and import.** Cards for media, comics and sets with ordered child pages. Uploads recognize JPEG, PNG, WebP, GIF, MP4, MOV and WebM from content; the original is kept and incompatible video gets an H.264/AAC playback copy. GIF and video get an animated preview and a poster (start time adjustable). Bulk import with a resumable local queue, content-hash duplicate detection, and idempotent creation keys.
- **Tagging.** Local WD ViT v3 image tagger (images, GIF and sampled video frames); speech recognition with Whisper and Silero VAD, matching English phrases to existing tags; system meta tags derived from file properties; Danbooru tag vocabulary and categories (general, artist, character, copyright, meta, custom); reviewable automatic tags with accept, reject and correct, and a decision log.
- **Cards, collections and decks.** Reusable tags and sources, decks that reference cards, favorites, daily thumbs votes and view tracking, gallery search and filters with a Collections/Pages switch.
- **Recommendations.** "For you", "Today's picks" and per-card "Recommended" rails driven by views, votes, tags and freshness.
- **Redesigned UI.** Card-and-node look, command palette (Ctrl/Cmd+K), dark and light themes, mobile bottom navigation, full-height carousel edges with chevrons, whole-stage tap zones for comics and sets, and a Back button that restores tabs, search, filters, comic page, scroll and carousel position.
- **Backups and migrations.** Versioned schema migrations that refuse to upgrade a populated library without a verified backup; `library-backup.sh` and a Docker-only variant `library-backup-docker.sh`; `export-db.sh` for a readable SQL dump; models excluded from backups and kept on restore.
- **Release packaging.** One Docker image serving the UI and API (`/health` reports the version); `scripts/build-release.sh` builds an amd64 bundle with a compose file, `.env.example`, helper scripts and checksums; `scripts/install-models.sh` installs models using only Docker; WD tag vocabulary is seeded automatically; placeholder (`change-me`) passwords are refused; MySQL LAN access is opt-in (`MYSQL_BIND_ADDRESS`). Installation guide for ZimaOS in the README.

### Known limitations

- The release image is amd64 only.
- Tag quality thresholds are not calibrated; this needs a labelled sample of real files.
- Visual similarity (S2.4) is not implemented; similarity is based on tags.
- iOS Safari has not been verified.
- Tag and source lists are capped at 200 entries without paging.
- There are no automated frontend tests.
- The ZimaOS installation has not been tested on a real ZimaOS device.
