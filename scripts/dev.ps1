# Builds the image and runs the API locally in dry-run mode (nothing is created in Google/Todoist).
# http://localhost:5080, API key "dev-key". Pass -Live to use the real integrations from .env instead.
param([switch]$Live)

$repo = (Resolve-Path "$PSScriptRoot\..").Path
docker build -t life-organizer-api:dev $repo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$envArgs = if ($Live) { @("--env-file", "$repo\.env") } else { @("-e", "App__ApiKey=dev-key", "-e", "Integrations__DryRun=true") }

Write-Host "API on http://localhost:5080 (Ctrl+C to stop)"
docker run --rm -it -p 127.0.0.1:5080:8080 @envArgs life-organizer-api:dev
