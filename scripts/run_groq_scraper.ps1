# Runs the scraper in Groq-only mode.
# Requires: GitHub PAT already seeded via scripts/set_github_token.py
$ErrorActionPreference = "Stop"
$env:UNSECURED_APIKEYS_PROVIDER_FILTER = "Groq"
Set-Location "$PSScriptRoot\..\UnsecuredAPIKeys.Bots.Scraper"
dotnet run
