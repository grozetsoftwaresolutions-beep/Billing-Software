# ADR-010: SQLite Safe Online Backup API

## Status
Accepted (Locked)

## Context
Copying an active SQLite database file directly while write transactions or WAL checkpoints are occurring can yield a corrupted or inconsistent database file.

## Decision
Use SQLite's native online backup mechanism (\VACUUM INTO @BackupFilePath\ or the SQLite Online Backup API) to generate consistent point-in-time snapshots while billing continues uninterrupted.

## Consequences
- Zero downtime for cashiers during backup operations.
- Backups are validated using \PRAGMA integrity_check\ before archiving.
