ALTER TABLE backup_manifest ADD COLUMN remote_path TEXT;
ALTER TABLE backup_manifest ADD COLUMN hash_kind TEXT;
ALTER TABLE backup_manifest ADD COLUMN freed INTEGER NOT NULL DEFAULT 0;
CREATE TABLE IF NOT EXISTS backup_versions(id INTEGER PRIMARY KEY AUTOINCREMENT, plan_id INTEGER NOT NULL, local_path TEXT NOT NULL, remote_path TEXT NOT NULL, created TEXT NOT NULL, reason TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_backup_versions ON backup_versions(plan_id, local_path);
