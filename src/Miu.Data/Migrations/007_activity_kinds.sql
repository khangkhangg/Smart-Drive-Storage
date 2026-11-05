-- Activity also records what couldn't be cleaned, things removed without a Recycle Bin copy, and rule changes.
-- SQLite can't change a CHECK constraint in place: rebuild the table, keeping every row and id.
CREATE TABLE actions_log_new(id INTEGER PRIMARY KEY, ts TEXT NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('recycle','delete','move','backup','sync','map','removed','failed','rule-added','rule-changed')),
    items_json TEXT NOT NULL, bytes INTEGER NOT NULL DEFAULT 0, undo_token TEXT, origin TEXT NOT NULL, undone INTEGER NOT NULL DEFAULT 0);
INSERT INTO actions_log_new(id, ts, kind, items_json, bytes, undo_token, origin, undone)
    SELECT id, ts, kind, items_json, bytes, undo_token, origin, undone FROM actions_log;
DROP TABLE actions_log;
ALTER TABLE actions_log_new RENAME TO actions_log;
CREATE INDEX IF NOT EXISTS ix_actions_ts ON actions_log(ts);
