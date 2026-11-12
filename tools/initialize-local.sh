#!/bin/sh
set -eu

importer_project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
importer_environment_path="$importer_project_root/.env"
if [ -e "$importer_environment_path" ]; then
    printf '%s\n' 'Existing .env preserved.'
    exit 0
fi

umask 077
importer_database_password=$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')
importer_management_key=$(od -An -N48 -tx1 /dev/urandom | tr -d ' \n')
# Noclobber prevents a second initialization from replacing an existing private file.
set -C
{
    printf 'IMPORTER_DB_PASSWORD=%s\n' "$importer_database_password"
    printf 'IMPORTER_API_KEY=%s\n' "$importer_management_key"
    printf 'STEAM_API_KEY=\nCATALOG_DATABASE_CONNECTION=\n'
} > "$importer_environment_path"
printf '%s\n' 'Created private .env; existing values were not printed.'
printf '%s\n' 'Start with: docker compose up --build -d'
