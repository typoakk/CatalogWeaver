#!/usr/bin/env bash
# Real Docker backup/restore regression. Never targets the user's Compose project.
set -Eeuo pipefail
umask 077
image=$CATALOGWEAVER_IMAGE
[[ -n "$image" ]] || { echo 'Set CATALOGWEAVER_IMAGE to an already built runtime image.' >&2; exit 1; }
# Test processes must not inherit a real destination connection, source key or bootstrap secret.
unset IMPORTER_DB_PASSWORD IMPORTER_API_KEY CATALOG_DATABASE_CONNECTION STEAM_API_KEY COMPOSE_FILE COMPOSE_ENV_FILES COMPOSE_PROFILES
repo=$(cd -- "$(dirname -- "$0")/.." && pwd -P)
command -v docker python3 tar gzip sha256sum >/dev/null
docker image inspect "$image" >/dev/null
temp_base=$(python3 -c 'import os, tempfile; print(os.path.realpath(tempfile.gettempdir()))')
sandbox=$(mktemp -d "$temp_base/catalogweaver-operations.XXXXXXXX")
nonce=$(python3 -c 'import secrets; print(secrets.token_hex(6))')
source_project="cw-ops-source-$$-$nonce"
restore_project="cw-ops-restore-$$-$nonce"
source_dir="$sandbox/source"
restore_dir="$sandbox/restored"
backup_dir="$sandbox/backup"
source_port=0
restore_port=0

source_compose() {
    (cd -- "$source_dir" && COMPOSE_PROJECT_NAME="$source_project" IMPORTER_PORT="$source_port" \
        IMPORTER_WORKERS_ENABLED=false CATALOGWEAVER_IMAGE="$image" \
        docker compose -f compose.yaml -p "$source_project" "$@")
}
restore_compose() {
    # Re-read persisted restore settings, without inheriting source image, port or worker options.
    (cd -- "$restore_dir" && env -u CATALOGWEAVER_IMAGE -u IMPORTER_WORKERS_ENABLED -u IMPORTER_PORT \
        docker compose -f compose.yaml -p "$restore_project" "$@")
}
cleanup() {
    result=$?
    trap - EXIT
    cleanup_failed=0
    for role in source restored; do
        if [[ "$role" == source ]]; then directory=$source_dir; project=$source_project; else directory=$restore_dir; project=$restore_project; fi
        [[ "$project" == "cw-ops-source-$$-$nonce" || "$project" == "cw-ops-restore-$$-$nonce" ]] || { cleanup_failed=1; continue; }
        if [[ -f "$directory/compose.yaml" ]]; then
            (cd -- "$directory" && CATALOGWEAVER_IMAGE="$image" IMPORTER_PORT=0 IMPORTER_WORKERS_ENABLED=false \
                docker compose -f compose.yaml -p "$project" logs --no-color) > "$sandbox/$role.log" 2>&1 || true
            (cd -- "$directory" && CATALOGWEAVER_IMAGE="$image" IMPORTER_PORT=0 IMPORTER_WORKERS_ENABLED=false \
                docker compose -f compose.yaml -p "$project" down --volumes --remove-orphans) >> "$sandbox/cleanup.log" 2>&1 || cleanup_failed=1
        fi
    done
    if [[ "$result" == 0 && "$cleanup_failed" == 0 ]]; then
        case "$sandbox" in "$temp_base"/catalogweaver-operations.*) rm -rf -- "$sandbox";;
            *) echo 'Refused unsafe temporary-directory cleanup.' >&2; exit 1;; esac
    else
        printf 'Operations regression evidence retained privately at: %s\n' "$sandbox" >&2
    fi
    if [[ "$cleanup_failed" != 0 && "$result" == 0 ]]; then result=1; fi
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'printf "Operations regression failed near line %s.\n" "$LINENO" >&2' ERR
mkdir -- "$source_dir" "$restore_dir"
for directory in "$source_dir" "$restore_dir"; do
    cp -- "$repo/compose.yaml" "$directory/compose.yaml"
    cp -a -- "$repo/tools" "$directory/tools"
done
printf 'IMPORTER_WORKERS_ENABLED=false\nIMPORTER_MINIMUM_FREE_BYTES=0\n' > "$source_dir/.env"
read -r source_port restore_port < <(python3 - <<'PY'
import socket
with socket.socket() as first, socket.socket() as second:
    first.bind(("127.0.0.1", 0))
    second.bind(("127.0.0.1", 0))
    print(first.getsockname()[1], second.getsockname()[1])
PY
)
source_compose up --no-build -d --wait --wait-timeout 180
source_key=$(source_compose exec -T importer cat /data/config/api-key)
[[ -n "$source_key" ]]
source_compose exec -T importer curl -fsS -H "Authorization: Bearer $source_key" \
    http://127.0.0.1:8080/api/v1/importer/adapters/shikimori > "$sandbox/adapter.json"
read -r adapter_hash adapter_base64 < <(python3 - "$sandbox/adapter.json" <<'PY'
import base64, hashlib, pathlib, sys
content = pathlib.Path(sys.argv[1]).read_bytes()
print(hashlib.sha256(content).hexdigest(), base64.b64encode(content).decode("ascii"))
PY
)
read -r image_hash image_bytes < <(python3 - "$sandbox/fixture.png" <<'PY'
import hashlib, pathlib, struct, sys, zlib
def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
image = b"\x89PNG\r\n\x1a\n"
image += chunk(b"IHDR", struct.pack(">IIBBBBB", 2, 1, 8, 2, 0, 0, 0))
image += chunk(b"IDAT", zlib.compress(b"\0\x20\x60\xe0\x20\x60\xe0"))
image += chunk(b"IEND", b"")
pathlib.Path(sys.argv[1]).write_bytes(image)
print(hashlib.sha256(image).hexdigest(), len(image))
PY
)
source_compose exec -T importer mkdir -p /data/media/shikimori
source_compose exec -T importer sh -c 'cat > "$1"' sh "/data/media/shikimori/$image_hash" < "$sandbox/fixture.png"
source_sql() {
    source_compose exec -T state sh -ec 'export PGPASSWORD="$(cat /run/catalogweaver/database-password)"; exec psql -X -At -v ON_ERROR_STOP=1 -U media_importer -d media_importer'
}
restore_sql() {
    restore_compose exec -T state sh -ec 'export PGPASSWORD="$(cat /run/catalogweaver/database-password)"; exec psql -X -At -v ON_ERROR_STOP=1 -U media_importer -d media_importer'
}
source_sql <<SQL
BEGIN;
INSERT INTO "ImporterItems" ("Provider","ExternalId","MediaId","AdapterVersion","MetadataJson","AssetStatusJson","WarningsJson","UpdatedUtc")
VALUES ('shikimori',1,'shikimori-1','$adapter_hash',
    '{"provider":"shikimori","externalId":1,"kind":"anime","title":"Backup smoke fixture","originalTitle":"Backup smoke fixture","description":"Restored description","sourceUrl":"https://example.org/1","assetStatus":{"poster":"complete"},"fieldStatus":{"title":"complete","description":"complete"},"tags":["backup"]}'::jsonb,
    '{"poster":"complete"}'::jsonb,'[]'::jsonb,now());
INSERT INTO "ImporterAssets" ("Provider","ExternalId","Role","Position","AssetJson","Hash","RelativePath")
VALUES ('shikimori',1,'poster',0,
    jsonb_build_object('role','poster','position',0,'sourceUrl','https://example.org/fixture.png','storage','local',
        'state','stored','hash','$image_hash','relativePath','shikimori/$image_hash','contentType','image/png',
        'bytes',$image_bytes,'requestedProfile','original','actualProfile','original'),
    '$image_hash','shikimori/$image_hash');
INSERT INTO "ImporterFiles" ("Hash","RelativePath","ContentType","Bytes","CreatedUtc")
VALUES ('$image_hash','shikimori/$image_hash','image/png',$image_bytes,now());
INSERT INTO "ImporterJobs" ("Id","Provider","ExternalId","AdapterVersion","AdapterJson","State","Stage","DueUtc","LeaseToken","LeaseUntilUtc")
VALUES ('b1a7cb77-441e-4e49-886e-19edc1f33418','shikimori',1,'$adapter_hash',convert_from(decode('$adapter_base64','base64'),'UTF8'),
    'importing','assets:poster',now(),'9aa2faab-d22e-4464-9d94-f08ed07d939a',now()+interval '1 day');
INSERT INTO "ImporterDiscoveryRuns" ("Id","Provider","AdapterVersion","AdapterJson","Cursor","Discovered","Pages","State","DueUtc","LeaseToken","LeaseUntilUtc","PendingItems","PendingCursor")
VALUES ('d517d910-4e27-48f8-965b-aad63c984276','shikimori','$adapter_hash',convert_from(decode('$adapter_base64','base64'),'UTF8'),
    '2',2,1,'discovering',now(),'f82be671-181d-483a-87d2-425f48691a04',now()+interval '1 day',
    '[{"externalId":3,"name":"Buffered fixture"}]'::jsonb,'3');
COMMIT;
SQL
(cd -- "$source_dir" && COMPOSE_PROJECT_NAME="$source_project" IMPORTER_PORT="$source_port" \
    IMPORTER_WORKERS_ENABLED=false CATALOGWEAVER_IMAGE="$image" bash tools/backup.sh "$backup_dir")
[[ -f "$backup_dir/COMPLETE" ]]
(cd -- "$restore_dir" && COMPOSE_PROJECT_NAME="$restore_project" IMPORTER_PORT="$restore_port" \
    bash tools/restore.sh "$backup_dir")
restored_key=$(restore_compose exec -T importer cat /data/config/api-key)
[[ "$source_key" == "$restored_key" ]]
source_password_hash=$(source_compose exec -T importer sha256sum /data/config/database-password)
restored_password_hash=$(restore_compose exec -T importer sha256sum /data/config/database-password)
[[ "$source_password_hash" == "$restored_password_hash" ]]
read -r actual_hash ignored_path < <(restore_compose exec -T importer sha256sum "/data/media/shikimori/$image_hash")
[[ "$actual_hash" == "$image_hash" ]]
restore_compose exec -T importer curl -fsS -H "Authorization: Bearer $restored_key" \
    http://127.0.0.1:8080/api/v1/importer/items/shikimori-1 > "$sandbox/restored-card.json"
python3 - "$sandbox/restored-card.json" "$image_hash" "$image_bytes" <<'PY'
import json, pathlib, sys
card = json.loads(pathlib.Path(sys.argv[1]).read_text())
assert card["item"]["title"] == "Backup smoke fixture"
assert card["item"]["description"] == "Restored description"
assert card["item"]["tags"] == ["backup"]
assert card["status"] == "completed"
assert len(card["assets"]) == 1
asset = card["assets"][0]
assert asset["hash"] == sys.argv[2]
assert asset["bytes"] == int(sys.argv[3])
assert asset["relativePath"] == "shikimori/" + sys.argv[2]
assert asset["contentType"] == "image/png"
PY
[[ "$(restore_sql <<'SQL'
SELECT "State"='pending' AND "Stage"='queued' AND "LeaseToken" IS NULL AND "LeaseUntilUtc" IS NULL
FROM "ImporterJobs" WHERE "Provider"='shikimori' AND "ExternalId"=1;
SQL
)" == t ]]
[[ "$(restore_sql <<'SQL'
SELECT "State"='pending' AND "LeaseToken" IS NULL AND "LeaseUntilUtc" IS NULL AND "Cursor"='2'
    AND "PendingCursor"='3' AND ("PendingItems"->0->>'externalId')='3' AND "Discovered"=2 AND "Pages"=1
FROM "ImporterDiscoveryRuns" WHERE "Provider"='shikimori';
SQL
)" == t ]]
[[ "$(restore_sql <<SQL
SELECT "Bytes"=$image_bytes AND "ContentType"='image/png' FROM "ImporterFiles"
WHERE "Hash"='$image_hash' AND "RelativePath"='shikimori/$image_hash';
SQL
)" == t ]]
restore_compose exec -T importer curl -fsS -H "Authorization: Bearer $restored_key" \
    http://127.0.0.1:8080/api/v1/importer/status > "$sandbox/restored-status.json"
python3 - "$sandbox/restored-status.json" <<'PY'
import json, pathlib, sys
assert json.loads(pathlib.Path(sys.argv[1]).read_text())["workersEnabled"] is False
PY
# Force a new container from the saved .env: restart alone would retain old environment values.
restore_compose up --no-deps --no-build --force-recreate -d --wait --wait-timeout 120 importer
restore_compose exec -T importer curl -fsS -H "Authorization: Bearer $restored_key" \
    http://127.0.0.1:8080/api/v1/importer/status > "$sandbox/recreated-status.json"
python3 - "$sandbox/recreated-status.json" <<'PY'
import json, pathlib, sys
assert json.loads(pathlib.Path(sys.argv[1]).read_text())["workersEnabled"] is False
PY
if (cd -- "$restore_dir" && COMPOSE_PROJECT_NAME="$restore_project" IMPORTER_PORT="$restore_port" \
    bash tools/restore.sh "$backup_dir") > "$sandbox/refused-second-restore.log" 2>&1; then
    echo 'A second restore unexpectedly accepted existing data.' >&2
    exit 1
fi
python3 - "$sandbox/refused-second-restore.log" <<'PY'
import pathlib, sys
assert "without existing volumes" in pathlib.Path(sys.argv[1]).read_text()
PY
# Backup and restore must leave the independently running source project unchanged.
[[ "$(source_sql <<'SQL'
SELECT "State"='importing' AND "LeaseToken" IS NOT NULL AND "LeaseUntilUtc">now()
FROM "ImporterJobs" WHERE "Provider"='shikimori' AND "ExternalId"=1;
SQL
)" == t ]]
[[ "$(source_sql <<'SQL'
SELECT "State"='discovering' AND "Cursor"='2' AND "PendingCursor"='3'
    AND "LeaseToken" IS NOT NULL AND ("PendingItems"->0->>'externalId')='3'
FROM "ImporterDiscoveryRuns" WHERE "Provider"='shikimori';
SQL
)" == t ]]
unchanged_source_key=$(source_compose exec -T importer cat /data/config/api-key)
[[ "$source_key" == "$unchanged_source_key" ]]
printf 'Docker backup/restore regression passed: catalog, image, secrets, leases, checkpoints, paused recreation and overwrite refusal.\n'
