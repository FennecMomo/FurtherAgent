$ErrorActionPreference = 'Stop'
# This interactive local test server stops with Ctrl+C. Production uses HTTPS/WSS.
dotnet (Join-Path $PSScriptRoot 'Server/FurtherAgent.Server.dll') --urls http://127.0.0.1:47320
