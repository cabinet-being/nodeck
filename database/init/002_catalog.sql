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
    origin VARCHAR(32) NOT NULL DEFAULT 'user',
    danbooru_id BIGINT NULL,
    UNIQUE KEY uq_tag_alias (alias),
    UNIQUE KEY uq_tag_alias_danbooru (danbooru_id),
    KEY idx_tag_alias_tag (tag_id),
    CONSTRAINT fk_tag_alias_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS danbooru_import_state (
    kind VARCHAR(16) NOT NULL PRIMARY KEY,
    cursor_before_id BIGINT NULL,
    completed BOOLEAN NOT NULL DEFAULT FALSE,
    processed BIGINT NOT NULL DEFAULT 0,
    conflicts BIGINT NOT NULL DEFAULT 0,
    updated_at_utc DATETIME(6) NOT NULL
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

CREATE TABLE IF NOT EXISTS card_tag_suggestions (
    card_id BIGINT NOT NULL,
    tag_id BIGINT NOT NULL,
    state VARCHAR(16) NOT NULL DEFAULT 'pending',
    confidence DECIMAL(6,5) NULL,
    model_key VARCHAR(128) NULL,
    suggestion_source VARCHAR(16) NOT NULL DEFAULT 'visual',
    created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (card_id, tag_id),
    KEY idx_card_tag_suggestions_card_state (card_id, state),
    CONSTRAINT chk_card_tag_suggestions_state CHECK (state IN ('pending', 'accepted', 'rejected')),
    CONSTRAINT chk_card_tag_suggestions_confidence CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
    CONSTRAINT chk_card_tag_suggestions_source CHECK (suggestion_source IN ('visual', 'speech', 'custom', 'learned')),
    CONSTRAINT fk_card_tag_suggestions_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE,
    CONSTRAINT fk_card_tag_suggestions_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS card_image_analysis_jobs (
    card_id BIGINT NOT NULL PRIMARY KEY,
    revision VARCHAR(36) NOT NULL,
    state VARCHAR(16) NOT NULL,
    attempts INT NOT NULL DEFAULT 0,
    available_at_utc DATETIME(6) NOT NULL,
    lease_token VARCHAR(36) NULL,
    lease_until_utc DATETIME(6) NULL,
    last_error VARCHAR(512) NULL,
    priority TINYINT NOT NULL DEFAULT 0,
    KEY idx_card_image_analysis_jobs_state_available (state, available_at_utc),
    CONSTRAINT chk_card_image_analysis_jobs_state CHECK (state IN ('pending', 'leased', 'done', 'failed')),
    CONSTRAINT chk_card_image_analysis_jobs_attempts CHECK (attempts >= 0),
    CONSTRAINT fk_card_image_analysis_jobs_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS card_speech_analysis_jobs (
    card_id BIGINT NOT NULL PRIMARY KEY,
    revision VARCHAR(36) NOT NULL,
    state VARCHAR(16) NOT NULL,
    attempts INT NOT NULL DEFAULT 0,
    available_at_utc DATETIME(6) NOT NULL,
    lease_token VARCHAR(36) NULL,
    lease_until_utc DATETIME(6) NULL,
    last_error VARCHAR(512) NULL,
    speech_detected BOOLEAN NULL,
    english_speech_detected BOOLEAN NULL,
    language VARCHAR(16) NULL,
    processed_seconds DECIMAL(12,3) NULL,
    truncated BOOLEAN NOT NULL DEFAULT FALSE,
    model_key VARCHAR(128) NULL,
    evidence_json JSON NULL,
    KEY idx_card_speech_analysis_jobs_state_available (state, available_at_utc),
    CONSTRAINT chk_card_speech_analysis_jobs_state CHECK (state IN ('pending', 'leased', 'done', 'failed')),
    CONSTRAINT chk_card_speech_analysis_jobs_attempts CHECK (attempts >= 0),
    CONSTRAINT chk_card_speech_analysis_jobs_speech CHECK (speech_detected IS NULL OR speech_detected IN (0, 1)),
    CONSTRAINT chk_card_speech_analysis_jobs_english CHECK (english_speech_detected IS NULL OR english_speech_detected IN (0, 1)),
    CONSTRAINT chk_card_speech_analysis_jobs_duration CHECK (processed_seconds IS NULL OR processed_seconds >= 0),
    CONSTRAINT chk_card_speech_analysis_jobs_evidence CHECK (evidence_json IS NULL OR JSON_STORAGE_SIZE(evidence_json) <= 16384),
    CONSTRAINT fk_card_speech_analysis_jobs_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE
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

CREATE TABLE IF NOT EXISTS card_tag_decisions (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    card_id BIGINT NOT NULL,
    suggested_tag_id BIGINT NULL,
    chosen_tag_id BIGINT NULL,
    decision VARCHAR(16) NOT NULL,
    suggestion_source VARCHAR(16) NULL,
    model_key VARCHAR(128) NULL,
    confidence DECIMAL(6,5) NULL,
    decided_at_utc DATETIME(6) NOT NULL,
    KEY idx_card_tag_decisions_card (card_id),
    KEY idx_card_tag_decisions_suggested (suggested_tag_id, decision),
    CONSTRAINT chk_card_tag_decisions_decision CHECK (decision IN ('accepted', 'rejected', 'corrected')),
    CONSTRAINT chk_card_tag_decisions_source CHECK (suggestion_source IS NULL OR suggestion_source IN ('visual', 'speech', 'custom', 'learned')),
    CONSTRAINT chk_card_tag_decisions_confidence CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
    CONSTRAINT fk_card_tag_decisions_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE,
    CONSTRAINT fk_card_tag_decisions_suggested FOREIGN KEY (suggested_tag_id) REFERENCES tags(id) ON DELETE SET NULL,
    CONSTRAINT fk_card_tag_decisions_chosen FOREIGN KEY (chosen_tag_id) REFERENCES tags(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS card_visual_features (
    card_id BIGINT NOT NULL PRIMARY KEY,
    revision VARCHAR(36) NOT NULL,
    model_key VARCHAR(128) NOT NULL,
    dimension INT NOT NULL,
    features MEDIUMBLOB NOT NULL,
    scored_generation BIGINT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    KEY idx_card_visual_features_model_scored (model_key, scored_generation),
    CONSTRAINT chk_card_visual_features_dimension CHECK (dimension > 0),
    CONSTRAINT fk_card_visual_features_card FOREIGN KEY (card_id) REFERENCES cards(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS learned_tag_models (
    tag_id BIGINT NOT NULL PRIMARY KEY,
    feature_model_key VARCHAR(128) NOT NULL,
    dimension INT NOT NULL,
    weights MEDIUMBLOB NOT NULL,
    bias DOUBLE NOT NULL,
    threshold DECIMAL(6,5) NULL,
    positives INT NOT NULL,
    negatives INT NOT NULL,
    hard_negatives INT NOT NULL,
    cv_precision DECIMAL(6,5) NULL,
    cv_recall DECIMAL(6,5) NULL,
    generation BIGINT NOT NULL,
    trained_at_utc DATETIME(6) NOT NULL,
    CONSTRAINT chk_learned_tag_models_threshold CHECK (threshold IS NULL OR threshold BETWEEN 0 AND 1),
    CONSTRAINT fk_learned_tag_models_tag FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS learned_tag_state (
    id TINYINT NOT NULL PRIMARY KEY,
    generation BIGINT NOT NULL,
    feature_model_key VARCHAR(128) NOT NULL,
    fingerprint CHAR(64) NOT NULL,
    trained_at_utc DATETIME(6) NOT NULL,
    CONSTRAINT chk_learned_tag_state_singleton CHECK (id = 1)
);
