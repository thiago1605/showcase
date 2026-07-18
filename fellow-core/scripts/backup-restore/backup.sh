#!/usr/bin/env bash
# =============================================================================
# FellowCore PostgreSQL Backup Script
# =============================================================================
# Creates a compressed custom-format backup using pg_dump.
#
# Usage:
#   ./scripts/backup-restore/backup.sh
#
# Environment variables (with defaults for local dev):
#   PGHOST       (default: localhost)
#   PGPORT       (default: 5454)
#   PGUSER       (default: admin)
#   PGPASSWORD   (default: changeme)
#   PGDATABASE   (default: fellowcore)
#   BACKUP_DIR   (default: ./backups)
#   KEEP_BACKUPS  (default: 7) - number of recent backups to retain
# =============================================================================

set -euo pipefail

# ── Configuration ────────────────────────────────────────────────────────────

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5454}"
PGUSER="${PGUSER:-admin}"
PGPASSWORD="${PGPASSWORD:-changeme}"
PGDATABASE="${PGDATABASE:-fellowcore}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
BACKUP_DIR="${BACKUP_DIR:-$PROJECT_DIR/backups}"
KEEP_BACKUPS="${KEEP_BACKUPS:-7}"

export PGPASSWORD

# ── Pre-checks ───────────────────────────────────────────────────────────────

command -v pg_dump >/dev/null 2>&1 || {
  echo "ERROR: pg_dump not found. Install PostgreSQL client tools."
  exit 1
}

# ── Create backup directory ──────────────────────────────────────────────────

mkdir -p "$BACKUP_DIR"

# ── Run backup ───────────────────────────────────────────────────────────────

TIMESTAMP="$(date +%Y%m%d_%H%M%S)"
BACKUP_FILE="$BACKUP_DIR/${PGDATABASE}_${TIMESTAMP}.dump"

echo "============================================================"
echo "  FellowCore Database Backup"
echo "============================================================"
echo "  Host:     $PGHOST:$PGPORT"
echo "  Database: $PGDATABASE"
echo "  User:     $PGUSER"
echo "  Output:   $BACKUP_FILE"
echo "============================================================"

echo ""
echo "Starting backup..."

pg_dump \
  -h "$PGHOST" \
  -p "$PGPORT" \
  -U "$PGUSER" \
  -d "$PGDATABASE" \
  -Fc \
  -f "$BACKUP_FILE"

BACKUP_SIZE=$(du -h "$BACKUP_FILE" | cut -f1)
echo "Backup completed: $BACKUP_FILE ($BACKUP_SIZE)"

# ── Prune old backups ────────────────────────────────────────────────────────

echo ""
echo "Pruning old backups (keeping last $KEEP_BACKUPS)..."

BACKUP_COUNT=$(find "$BACKUP_DIR" -maxdepth 1 -name "${PGDATABASE}_*.dump" -type f | wc -l | tr -d ' ')

if [ "$BACKUP_COUNT" -gt "$KEEP_BACKUPS" ]; then
  REMOVE_COUNT=$((BACKUP_COUNT - KEEP_BACKUPS))
  # Sort by name (timestamp-based), remove oldest
  find "$BACKUP_DIR" -maxdepth 1 -name "${PGDATABASE}_*.dump" -type f \
    | sort \
    | head -n "$REMOVE_COUNT" \
    | while read -r old_backup; do
        echo "  Removing: $(basename "$old_backup")"
        rm -f "$old_backup"
      done
  echo "Removed $REMOVE_COUNT old backup(s)."
else
  echo "No pruning needed ($BACKUP_COUNT backup(s) found, limit is $KEEP_BACKUPS)."
fi

echo ""
echo "Done."
