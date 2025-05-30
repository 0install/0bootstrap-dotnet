Param ($Version = "1.0.0-pre")
$ErrorActionPreference = "Stop"
pushd $PSScriptRoot

function Find-MSBuild {
    if (Test-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe") {
        $vsDir = . "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products * -property installationPath -format value -version 17.13
        if ($vsDir) { return "$vsDir\MSBuild\Current\Bin\amd64\MSBuild.exe" }
    }
}

function Run-MSBuild {
    $msbuild = Find-Msbuild
    if (!$msbuild) { throw "You need Visual Studio 2022 v17.13 or newer to build this project" }
    . $msbuild @args
    if ($LASTEXITCODE -ne 0) {throw "Exit Code: $LASTEXITCODE"}
}

# Build
if ($env:CI) { $ci = "/p:ContinuousIntegrationBuild=True" }
Run-MSBuild /v:Quiet /t:Restore /t:Build $ci /p:Configuration=Release /p:Version=$Version ZeroInstall.Bootstrap.Builder.slnx
Remove-Item ..\artifacts\Release\net472 -Include *.xml,*.pdb

popd
