Param ($Version = "1.0.0-pre")
$ErrorActionPreference = "Stop"
pushd $PSScriptRoot

# Cleanup up previous test
if (Test-path test.exe) {rm test.exe}

# Build bootstrapper
.\0install.ps1 run --batch 0bootstrap-dotnet-$Version.xml https://apps.0install.net/utils/jq.xml test.exe
if (!(Test-path test.exe)) {throw "Failed to generate bootstrapper"}

# Run bootstrapper
if ($(echo '"test"' | .\test.exe .) -ne '"test"') {throw "Failed to run target"}

popd
