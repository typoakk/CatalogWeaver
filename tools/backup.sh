#!/usr/bin/env bash
# Run from the repository root. Backups contain credentials: keep them private.
set -Eeuo pipefail
umask 077
[[ -z "${COMPOSE_FILE:-}" ]] || { echo 'Use the standard compose.yaml with managed named volumes.' >&2; exit 1; }
destination=${1:?Usage: tools/backup.sh NEW_BACKUP_DIRECTORY}
[[ ! -e "$destination" ]] || { echo 'Backup destination already exists.' >&2; exit 1; }
mkdir -m 700 -- "$destination"
destination=$(cd -- "$destination" && pwd -P)
compose() { docker compose -f compose.yaml "$@"; }
project=$(compose config --format json | python3 tools/check-backup-layout.py --print-project)
container_ids=$(compose ps -aq)
[[ -n "$container_ids" ]] || { echo 'Create the importer and state services before backing up.' >&2; exit 1; }
mapfile -t deployed_containers <<< "$container_ids"
docker inspect "${deployed_containers[@]}" | python3 tools/check-backup-containers.py "$project"
container=$(compose ps -aq importer | head -n 1)
[[ -n "$container" ]] || { echo 'Create the importer service before backing it up.' >&2; exit 1; }
image_id=$(docker inspect --format '{{.Image}}' "$container")
[[ "$(docker image inspect --format '{{index .Config.Labels "io.catalogweaver.backup-format"}}' "$image_id")" == 1 ]] || { echo 'This image does not support the safe backup/restore format.' >&2; exit 1; }
running=$(compose ps --status running -q importer)
restart_importer() { if [[ -n "$running" ]]; then compose start importer >/dev/null; fi; }
trap restart_importer EXIT
if [[ -n "$running" ]]; then compose stop -t 45 importer >/dev/null; fi
# Workers are stopped before both database and file snapshots are taken.
compose exec -T state sh -ec 'export PGPASSWORD="$(cat /run/catalogweaver/database-password)"; exec pg_dump -U media_importer -d media_importer --format=custom --no-owner --no-privileges' > "$destination/state.dump"
compose run --rm --no-deps -T --entrypoint tar importer -cf - -C / data/config app/adapters app/encoders data/media | gzip -1 > "$destination/volumes.tar.gz"
python3 tools/check-backup-archive.py "$destination/volumes.tar.gz"
docker image save "$image_id" | gzip -1 > "$destination/runtime-image.tar.gz"
printf '%s\n' "$image_id" > "$destination/image-id"
if [[ -f .env ]]; then cp -- .env "$destination/environment.env"; fi
docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image_id" > "$destination/source-commit"
git rev-parse HEAD > "$destination/checkout-commit" 2>/dev/null || printf 'unknown\n' > "$destination/checkout-commit"
printf '1\n' > "$destination/backup-format"
(
    cd -- "$destination"
    sha256sum state.dump volumes.tar.gz runtime-image.tar.gz image-id source-commit checkout-commit backup-format > checksums.sha256
    if [[ -f environment.env ]]; then sha256sum environment.env >> checksums.sha256; fi
)
touch "$destination/COMPLETE"
printf 'Backup completed: %s\n' "$destination"
