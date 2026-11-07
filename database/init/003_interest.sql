CREATE TABLE IF NOT EXISTS card_view_events (
    id BIGINT NOT NULL AUTO_INCREMENT,
    event_id CHAR(36) NOT NULL,
    card_id BIGINT NOT NULL,
    viewed_at_utc DATETIME(6) NOT NULL,
    duration_ms INT NOT NULL,
    PRIMARY KEY (id),
    UNIQUE INDEX uq_card_view_events_event_id (event_id),
    CONSTRAINT fk_card_view_events_card
        FOREIGN KEY (card_id) REFERENCES cards(id)
        ON DELETE CASCADE,
    CONSTRAINT chk_card_view_events_duration_ms CHECK (duration_ms >= 2000 AND duration_ms <= 600000),
    INDEX idx_card_view_events_recent (viewed_at_utc DESC, id DESC),
    INDEX idx_card_view_events_card_id (card_id)
);

CREATE TABLE IF NOT EXISTS card_daily_votes (
    card_id BIGINT NOT NULL,
    vote_date DATE NOT NULL,
    vote TINYINT NOT NULL,
    zone_id VARCHAR(64) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (card_id, vote_date),
    CONSTRAINT fk_card_daily_votes_card
        FOREIGN KEY (card_id) REFERENCES cards(id)
        ON DELETE CASCADE,
    CONSTRAINT chk_card_daily_votes_vote CHECK (vote IN (-1, 0, 1)),
    INDEX idx_card_daily_votes_date (vote_date)
);
