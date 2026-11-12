#!/usr/bin/env bash
# Restores only into a NEW Compose project; it never overwrites existing volumes.
set -Eeuo pipefail
umask 077
[[ -z "${COMPOSE_FILE:-}" ]] || { echo 'Restore supports only the standard compose.yaml with managed named volumes.' >&2; exit 1; }
backup=${1:?Usage: COMPOSE_PROJECT_NAME=NEW_NAME tools/restore.sh BACKUP_DIRECTORY}
: "${COMPOSE_PROJECT_NAME:?Set a fresh COMPOSE_PROJECT_NAME explicitly}"
: "${IMPORTER_PORT:?Set a free API port for the restored project explicitly}"
[[ "$IMPORTER_PORT" =~ ^[0-9]+$ && "$IMPORTER_PORT" -ge 1 && "$IMPORTER_PORT" -le 65535 ]] || { echo 'Invalid restored API port.' >&2; exit 1; }
[[ "$COMPOSE_PROJECT_NAME" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || { echo 'Invalid Compose project name.' >&2; exit 1; }
backup=$(cd -- "$backup" && pwd -P)
[[ -f "$backup/COMPLETE" && "$(cat "$backup/backup-format")" == 1 ]] || { echo 'Incomplete or unsupported backup.' >&2; exit 1; }
(cd -- "$backup" && sha256sum -c checksums.sha256)
[[ -z "$(docker volume ls --filter "label=com.docker.compose.project=$COMPOSE_PROJECT_NAME" -q)" ]] || { echo 'Restore requires a project without existing volumes.' >&2; exit 1; }
[[ -z "$(docker ps -aq --filter "label=com.docker.compose.project=$COMPOSE_PROJECT_NAME")" ]] || { echo 'Restore requires a project without existing containers.' >&2; exit 1; }
expected=$(cat "$backup/source-commit")
actual=$(git rev-parse HEAD 2>/dev/null || printf unknown)
if [[ "$expected" != unknown && "$actual" != "$expected" ]]; then
    echo 'Checkout differs from the backed-up runtime; the archived runtime image will be used.' >&2
fi
if [[ -f "$backup/environment.env" ]]; then
    if [[ -f .env ]]; then cmp -s .env "$backup/environment.env" || { echo 'Existing .env differs; restore it in a separate checkout.' >&2; exit 1; }
    else cp -- "$backup/environment.env" .env; fi
fi
# Reject traversal, symlinks, hardlinks and special files before any extraction.
python3 tools/check-backup-archive.py "$backup/volumes.tar.gz"
image_id=$(cat "$backup/image-id")
[[ "$image_id" =~ ^sha256:[a-f0-9]{64}$ ]] || { echo 'Invalid archived image identity.' >&2; exit 1; }
gzip -dc "$backup/runtime-image.tar.gz" | docker image load
docker image inspect "$image_id" >/dev/null
[[ "$(docker image inspect --format '{{index .Config.Labels "io.catalogweaver.backup-format"}}' "$image_id")" == 1 ]] || { echo 'Unsupported runtime backup format.' >&2; exit 1; }
restore_image="catalogweaver:restore-${image_id:7:16}"
docker image tag "$image_id" "$restore_image"
export CATALOGWEAVER_IMAGE="$restore_image"
compose() { docker compose -f compose.yaml -p "$COMPOSE_PROJECT_NAME" "$@"; }
compose config --format json | python3 tools/check-backup-layout.py
for volume in configuration state adapters encoders media; do
    if docker volume inspect "${COMPOSE_PROJECT_NAME}_$volume" >/dev/null 2>&1; then
        echo 'Restore requires completely new named volumes.' >&2; exit 1
    fi
done
compose run --rm --no-deps -T --entrypoint tar bootstrap --no-same-owner -xf - -C / data/config < <(gzip -dc "$backup/volumes.tar.gz")
compose run --rm --no-deps -T --entrypoint tar importer --no-same-owner --exclude=data/config --exclude='data/config/*' -xf - -C / < <(gzip -dc "$backup/volumes.tar.gz")
compose up -d --wait --wait-timeout 90 state
compose exec -T state sh -ec 'export PGPASSWORD="$(cat /run/catalogweaver/database-password)"; exec pg_restore -U media_importer -d media_importer --no-owner --no-privileges --exit-on-error' < "$backup/state.dump"
compose run --rm -T migrate
# No importer has been started in this fresh project. Old leases cannot represent live workers.
compose exec -T state sh -ec 'export PGPASSWORD="$(cat /run/catalogweaver/database-password)"; exec psql -X -v ON_ERROR_STOP=1 -U media_importer -d media_importer' <<'SQL'
BEGIN;
UPDATE "ImporterJobs" SET "State"='pending',"LeaseToken"=NULL,"LeaseUntilUtc"=NULL,"DueUtc"=now(),"Stage"='queued' WHERE "State"='importing';
UPDATE "ImporterDiscoveryRuns" SET "State"='pending',"LeaseToken"=NULL,"LeaseUntilUtc"=NULL,"DueUtc"=now() WHERE "State"='discovering';
COMMIT;
SQL
# A restored copy must not immediately write to the original application's destination.
if [[ -f .env ]]; then awk '!/^IMPORTER_WORKERS_ENABLED=/ && !/^CATALOGWEAVER_IMAGE=/ && !/^IMPORTER_PORT=/' .env > .env.restore.tmp; else : > .env.restore.tmp; fi
printf '\nIMPORTER_WORKERS_ENABLED=false\n' >> .env.restore.tmp
printf 'CATALOGWEAVER_IMAGE=%s\n' "$restore_image" >> .env.restore.tmp
printf 'IMPORTER_PORT=%s\n' "$IMPORTER_PORT" >> .env.restore.tmp
mv -- .env.restore.tmp .env
export IMPORTER_WORKERS_ENABLED=false
compose up -d --wait --wait-timeout 90 importer
compose exec -T importer curl -fsS http://127.0.0.1:8080/health/ready
printf '\nRestored into fresh project %s. Workers are paused; review connections and adapters before setting IMPORTER_WORKERS_ENABLED=true.\n' "$COMPOSE_PROJECT_NAME"
