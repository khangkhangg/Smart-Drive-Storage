ALTER TABLE rule_suggestions ADD COLUMN key TEXT;
CREATE INDEX IF NOT EXISTS ix_suggestions_key ON rule_suggestions(key);
