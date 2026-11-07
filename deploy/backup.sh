#!/usr/bin/env bash
# Backs up the database and the data volume (signing key, skins, build files).
#   deploy/backup.sh [target-dir]        (default: deploy/backups)
# Keep the backups off this server too. The signing key in the data volume matters:
# without it game servers stop trusting skins until they are restarted with the new key.
set -euo pipefail
cd "$(dirname "$0")"
TARGET="${1:-./backups}"
STAMP="$(date +%Y%m%d-%H%M%S)"
mkdir -p "$TARGET"

docker compose exec -T db pg_dump -U lumora -d lumora --format=custom > "$TARGET/lumora-db-$STAMP.dump"
docker compose run --rm --no-deps -v "$(realpath "$TARGET")":/backup --entrypoint "" web \
  tar czf "/backup/lumora-data-$STAMP.tar.gz" -C /data .

# Keep the last 14 backups of each kind.
ls -1t "$TARGET"/lumora-db-*.dump | tail -n +15 | xargs -r rm --
ls -1t "$TARGET"/lumora-data-*.tar.gz | tail -n +15 | xargs -r rm --
echo "Backup written to $TARGET ($STAMP)"
