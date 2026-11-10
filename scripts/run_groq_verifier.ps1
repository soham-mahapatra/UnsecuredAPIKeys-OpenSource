# Runs the verifier in Groq-only mode.
$ErrorActionPreference = "Stop"
$env:UNSECURED_APIKEYS_PROVIDER_FILTER = "Groq"
Set-Location "$PSScriptRoot\..\UnsecuredAPIKeys.Bots.Verifier"
dotnet run
