#!/usr/bin/env bash
# =============================================================================
# FellowCore PostgreSQL Restore Script
# =============================================================================
# Restores a database from a pg_dump custom-format backup file.
#
# Usage:
#   ./scripts/backup-restore/restore.sh <backup_file> [options]
#
# Options:
#   --dry-run         Show what would be restored without executing
#   --drop-existing   Drop and recreate the target database (requires confirmation)
#
# Environment variables (with defaults for local dev):
#   PGHOST       (default: localhost)
#   PGPORT       (default: 5454)
#   PGUSER       (default: admin)
#   PGPASSWORD   (default: changeme)
#   PGDATABASE   (default: fellowcore)
# =============================================================================

set -euo pipefail

# ── Configuration ────────────────────────────────────────────────────────────

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5454}"
PGUSER="${PGUSER:-admin}"
PGPASSWORD="${PGPASSWORD:-changeme}"
PGDATABASE="${PGDATABASE:-fellowcore}"

export PGPASSWORD

DRY_RUN=false
DROP_EXISTING=false
BACKUP_FILE=""

# ── Parse arguments ──────────────────────────────────────────────────────────

usage() {
  echo "Usage: $0 <backup_file> [--dry-run] [--drop-existing]"
  echo ""
  echo "Arguments:"
  echo "  backup_file      Path to the .dump backup file"
  echo ""
  echo "Options:"
  echo "  --dry-run        Show what would be restored without executing"
  echo "  --drop-existing  Drop and recreate the target database (requires confirmation)"
  echo ""
  echo "Environment variables:"
  echo "  PGHOST       (default: localhost)"
  echo "  PGPORT       (default: 5454)"
  echo "  PGUSER       (default: admin)"
  echo "  PGPASSWORD   (default: changeme)"
  echo "  PGDATABASE   (default: fellowcore)"
  exit 1
}

for arg in "$@"; do
  case "$arg" in
    --dry-run)
      DRY_RUN=true
      ;;
    --drop-existing)
      DROP_EXISTING=true
      ;;
    --help|-h)
      usage
      ;;
    *)
      if [ -z "$BACKUP_FILE" ]; then
        BACKUP_FILE="$arg"
      else
        echo "ERROR: Unexpected argument: $arg"
        usage
      fi
      ;;
  esac
done

if [ -z "$BACKUP_FILE" ]; then
  echo "ERROR: Backup file path is required."
  echo ""
  usage
fi

# ── Pre-checks ───────────────────────────────────────────────────────────────

command -v pg_restore >/dev/null 2>&1 || {
  echo "ERROR: pg_restore not found. Install PostgreSQL client tools."
  exit 1
}

if [ ! -f "$BACKUP_FILE" ]; then
  echo "ERROR: Backup file not found: $BACKUP_FILE"
  exit 1
fi

BACKUP_SIZE=$(du -h "$BACKUP_FILE" | cut -f1)

echo "============================================================"
echo "  FellowCore Database Restore"
echo "============================================================"
echo "  Host:       $PGHOST:$PGPORT"
echo "  Database:   $PGDATABASE"
echo "  User:       $PGUSER"
echo "  Backup:     $BACKUP_FILE ($BACKUP_SIZE)"
echo "  Dry run:    $DRY_RUN"
echo "  Drop first: $DROP_EXISTING"
echo "============================================================"

# ── Dry run mode ─────────────────────────────────────────────────────────────

if [ "$DRY_RUN" = true ]; then
  echo ""
  echo "--- Dry Run: Listing backup contents ---"
  echo ""
  pg_restore \
    --list \
    "$BACKUP_FILE"
  echo ""
  echo "Dry run complete. No changes were made."
  exit 0
fi

# ── Drop existing database ───────────────────────────────────────────────────

if [ "$DROP_EXISTING" = true ]; then
  echo ""
  echo "WARNING: This will DROP the database '$PGDATABASE' and all its data."
  echo ""
  read -r -p "Type the database name to confirm: " CONFIRM_DB
  if [ "$CONFIRM_DB" != "$PGDATABASE" ]; then
    echo "ERROR: Confirmation failed. Expected '$PGDATABASE', got '$CONFIRM_DB'."
    exit 1
  fi

  echo ""
  echo "Dropping database '$PGDATABASE'..."

  # Terminate existing connections
  psql \
    -h "$PGHOST" \
    -p "$PGPORT" \
    -U "$PGUSER" \
    -d postgres \
    -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$PGDATABASE' AND pid <> pg_backend_pid();" \
    > /dev/null 2>&1 || true

  psql \
    -h "$PGHOST" \
    -p "$PGPORT" \
    -U "$PGUSER" \
    -d postgres \
    -c "DROP DATABASE IF EXISTS \"$PGDATABASE\";"

  echo "Creating fresh database '$PGDATABASE'..."
  psql \
    -h "$PGHOST" \
    -p "$PGPORT" \
    -U "$PGUSER" \
    -d postgres \
    -c "CREATE DATABASE \"$PGDATABASE\" OWNER \"$PGUSER\";"

  echo "Database recreated."
fi

# ── Restore ──────────────────────────────────────────────────────────────────

echo ""
echo "Restoring from backup..."

pg_restore \
  -h "$PGHOST" \
  -p "$PGPORT" \
  -U "$PGUSER" \
  -d "$PGDATABASE" \
  --no-owner \
  --no-privileges \
  --clean \
  --if-exists \
  "$BACKUP_FILE"

echo ""
echo "Restore completed successfully."
echo ""
echo "Done."
