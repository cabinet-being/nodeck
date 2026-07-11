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
        relation_type IN ('tagged_with', 'sourced_from', 'contains', 'related_to', 'preview_for')
    ),
    CONSTRAINT chk_card_relations_properties_json CHECK (properties IS NULL OR JSON_VALID(properties)),
    CONSTRAINT chk_card_relations_metadata_json CHECK (JSON_VALID(metadata)),
    INDEX idx_card_relations_from_card_id (from_card_id),
    INDEX idx_card_relations_to_card_id (to_card_id),
    INDEX idx_card_relations_relation_type (relation_type)
);
