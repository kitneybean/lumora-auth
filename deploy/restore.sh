#!/usr/bin/env bash
# Restores a backup made by backup.sh. Stops the site while restoring.
#   deploy/restore.sh backups/lumora-db-20261107-120000.dump backups/lumora-data-20261107-120000.tar.gz
set -euo pipefail
cd "$(dirname "$0")"
DB_DUMP="${1:?path to lumora-db-*.dump}"
DATA_TAR="${2:?path to lumora-data-*.tar.gz}"

docker compose stop web
docker compose exec -T db pg_restore -U lumora -d lumora --clean --if-exists < "$DB_DUMP"
docker compose run --rm --no-deps -v "$(realpath "$(dirname "$DATA_TAR")")":/backup --entrypoint "" web \
  sh -c "rm -rf /data/* && tar xzf /backup/$(basename "$DATA_TAR") -C /data"
docker compose start web
echo "Restored."
