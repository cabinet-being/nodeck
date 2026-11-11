# Changelog

All notable changes to nodeck are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed

- Uploads larger than about 30 MB (typical videos) were rejected with HTTP 413 because the server kept the framework's default request limit. The limit is now 8 GB and can be changed with `NODECK_MAX_UPLOAD_MB`.

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
