param()
$ErrorActionPreference = 'Stop'
$importerProjectRoot = Split-Path -Parent $PSScriptRoot
$importerEnvironmentPath = Join-Path $importerProjectRoot '.env'
if (Test-Path -LiteralPath $importerEnvironmentPath) {
    Write-Output 'Existing .env preserved.'
    exit 0
}
$importerDatabasePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$importerManagementKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)).ToLowerInvariant()
$importerEnvironment = "IMPORTER_DB_PASSWORD=$importerDatabasePassword`nIMPORTER_API_KEY=$importerManagementKey`nSTEAM_API_KEY=`nCATALOG_DATABASE_CONNECTION=`n"
[IO.File]::WriteAllText($importerEnvironmentPath, $importerEnvironment, [Text.UTF8Encoding]::new($false))
Write-Output "Created private settings: $importerEnvironmentPath"
Write-Output 'Start with: docker compose up --build -d'
