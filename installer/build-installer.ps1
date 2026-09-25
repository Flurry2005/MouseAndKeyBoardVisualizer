# Builds the self-contained app (incl. the native virtual camera DLL) and the MSI.
# Requirements: .NET 9 SDK, Visual Studio 2022 with "Desktop development with C++" + Windows 11 SDK.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish\win-x64'

dotnet publish (Join-Path $root 'src\MouseSwipeVisualizer') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

dotnet build (Join-Path $PSScriptRoot 'MouseSwipeVisualizer.Installer.wixproj') -c Release "-p:PublishDir=$publish\"
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

Get-ChildItem (Join-Path $PSScriptRoot 'bin') -Recurse -Filter *.msi | Select-Object FullName, Length
