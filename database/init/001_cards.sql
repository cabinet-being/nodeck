CREATE TABLE IF NOT EXISTS cards (
    id BIGINT NOT NULL AUTO_INCREMENT,
    type VARCHAR(32) NOT NULL,
    title VARCHAR(255) NULL,
    preview VARCHAR(512) NULL,
    properties JSON NULL,
    metadata JSON NOT NULL,
    created_at DATETIME(6)
        GENERATED ALWAYS AS (
            STR_TO_DATE(JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.created_at')), '%Y-%m-%dT%H:%i:%sZ')
        ) STORED,
    PRIMARY KEY (id),
    CONSTRAINT chk_cards_type CHECK (type IN ('media', 'comic', 'set', 'tag', 'source')),
    CONSTRAINT chk_cards_properties_json CHECK (properties IS NULL OR JSON_VALID(properties)),
    CONSTRAINT chk_cards_metadata_json CHECK (JSON_VALID(metadata)),
    INDEX idx_cards_type (type),
    INDEX idx_cards_created_at (created_at)
);

CREATE TABLE IF NOT EXISTS card_relations (
    id BIGINT NOT NULL AUTO_INCREMENT,
    from_card_id BIGINT NOT NULL,
    to_card_id BIGINT NOT NULL,
    relation_type VARCHAR(32) NOT NULL,
    properties JSON NULL,
    metadata JSON NOT NULL,
    PRIMARY KEY (id),
    CONSTRAINT fk_card_relations_from_card
        FOREIGN KEY (from_card_id) REFERENCES cards(id)
        ON DELETE CASCADE,
    CONSTRAINT fk_card_relations_to_card
        FOREIGN KEY (to_card_id) REFERENCES cards(id)
        ON DELETE CASCADE,
    CONSTRAINT chk_card_relations_type CHECK (
        relation_type IN ('tagged_with', 'sourced_from', 'contains', 'related_to', 'preview_for', 'next_in_sequence')
    ),
    CONSTRAINT chk_card_relations_properties_json CHECK (properties IS NULL OR JSON_VALID(properties)),
    CONSTRAINT chk_card_relations_metadata_json CHECK (JSON_VALID(metadata)),
    INDEX idx_card_relations_from_card_id (from_card_id),
    INDEX idx_card_relations_to_card_id (to_card_id),
    INDEX idx_card_relations_relation_type (relation_type)
);

CREATE TABLE IF NOT EXISTS decks (
    id BIGINT NOT NULL AUTO_INCREMENT,
    title VARCHAR(255) NOT NULL,
    properties JSON NULL,
    metadata JSON NOT NULL,
    system_key VARCHAR(128) NULL,
    created_at DATETIME(6)
        GENERATED ALWAYS AS (
            STR_TO_DATE(JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.created_at')), '%Y-%m-%dT%H:%i:%sZ')
        ) STORED,
    PRIMARY KEY (id),
    CONSTRAINT chk_decks_properties_json CHECK (properties IS NULL OR JSON_VALID(properties)),
    CONSTRAINT chk_decks_metadata_json CHECK (JSON_VALID(metadata)),
    UNIQUE INDEX uq_decks_system_key (system_key),
    INDEX idx_decks_created_at (created_at)
);

CREATE TABLE IF NOT EXISTS deck_cards (
    deck_id BIGINT NOT NULL,
    card_id BIGINT NOT NULL,
    position INT NOT NULL,
    properties JSON NULL,
    metadata JSON NOT NULL,
    PRIMARY KEY (deck_id, card_id),
    CONSTRAINT fk_deck_cards_deck
        FOREIGN KEY (deck_id) REFERENCES decks(id)
        ON DELETE CASCADE,
    CONSTRAINT fk_deck_cards_card
        FOREIGN KEY (card_id) REFERENCES cards(id)
        ON DELETE CASCADE,
    CONSTRAINT chk_deck_cards_position CHECK (position >= 0),
    CONSTRAINT chk_deck_cards_properties_json CHECK (properties IS NULL OR JSON_VALID(properties)),
    CONSTRAINT chk_deck_cards_metadata_json CHECK (JSON_VALID(metadata)),
    UNIQUE INDEX uq_deck_cards_position (deck_id, position),
    INDEX idx_deck_cards_deck_id (deck_id),
    INDEX idx_deck_cards_card_id (card_id)
);
