CREATE TABLE IF NOT EXISTS tags (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    canonical_key VARCHAR(320) NOT NULL,
    category VARCHAR(32) NOT NULL DEFAULT 'general',
    origin VARCHAR(32) NOT NULL DEFAULT 'user',
    danbooru_id BIGINT NULL,
    legacy_card_id BIGINT NULL,
    name_conflict BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE KEY uq_tags_key (canonical_key),
    UNIQUE KEY uq_tags_danbooru (danbooru_id),
    UNIQUE KEY uq_tags_legacy (legacy_card_id),
    KEY idx_tags_name (name)
);

CREATE TABLE IF NOT EXISTS tag_aliases (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    tag_id BIGINT NOT NULL,
    alias VARCHAR(255) NOT NULL,
    UNIQUE KEY uq_tag_alias (alias),
    KEY idx_tag_alias_tag (tag_id),
    CONSTRAINT fk_tag_alias_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS sources (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    canonical_key VARCHAR(320) NOT NULL,
    legacy_card_id BIGINT NULL,
    name_conflict BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE KEY uq_sources_key (canonical_key),
    UNIQUE KEY uq_sources_legacy (legacy_card_id),
    KEY idx_sources_name (name)
);

CREATE TABLE IF NOT EXISTS card_tags (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    card_id BIGINT NOT NULL,
    tag_id BIGINT NOT NULL,
    assignment_source VARCHAR(32) NOT NULL DEFAULT 'manual',
    confidence DECIMAL(6,5) NULL,
    legacy_relation_id BIGINT NULL,
    UNIQUE KEY uq_card_tags_legacy (legacy_relation_id),
    KEY idx_card_tags_card (card_id),
    KEY idx_card_tags_tag (tag_id),
    CONSTRAINT fk_card_tags_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE,
    CONSTRAINT fk_card_tags_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS card_sources (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    card_id BIGINT NOT NULL,
    source_id BIGINT NOT NULL,
    original_url TEXT NULL,
    legacy_relation_id BIGINT NULL,
    UNIQUE KEY uq_card_sources_legacy (legacy_relation_id),
    KEY idx_card_sources_card (card_id),
    KEY idx_card_sources_source (source_id),
    CONSTRAINT fk_card_sources_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE,
    CONSTRAINT fk_card_sources_source FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);
