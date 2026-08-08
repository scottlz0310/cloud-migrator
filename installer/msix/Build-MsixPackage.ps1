[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$Version = "0.8.0.0",
    [string]$OutputDirectory = "installer/msix/AppPackages"
)

$ErrorActionPreference = "Stop"

Write-Host "==> 1. Publishing CloudMigrator.Dashboard..." -ForegroundColor Cyan
$publishDir = Join-Path $PSScriptRoot "publish_staging"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish src/CloudMigrator.Dashboard/CloudMigrator.Dashboard.csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit $LASTEXITCODE
}

Write-Host "==> 2. Copying Package.appxmanifest and Assets..." -ForegroundColor Cyan
Copy-Item (Join-Path $PSScriptRoot "Package.appxmanifest") (Join-Path $publishDir "AppxManifest.xml") -Force
Copy-Item (Join-Path $PSScriptRoot "Assets") $publishDir -Recurse -Force

Write-Host "==> 3. Finding makeappx.exe..." -ForegroundColor Cyan
$makeAppx = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
if (-not (Test-Path $makeAppx)) {
    $makeAppx = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -File -Include makeappx.exe | 
        Where-Object { $_.FullName -match 'x64' } | 
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $makeAppx -or -not (Test-Path $makeAppx)) {
    Write-Error "makeappx.exe not found in Windows Kits."
    exit 1
}

Write-Host "Found makeappx.exe at: $makeAppx" -ForegroundColor Green

$outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "AppPackages"))
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$msixPath = Join-Path $outDir "CloudMigrator_${Version}_x64.msix"
$msixUploadPath = Join-Path $outDir "CloudMigrator_${Version}_x64_bundle.msixupload"

if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
if (Test-Path $msixUploadPath) { Remove-Item $msixUploadPath -Force }

Write-Host "==> 4. Packing MSIX package..." -ForegroundColor Cyan
& $makeAppx pack /d $publishDir /p $msixPath /l

if ($LASTEXITCODE -ne 0) {
    Write-Error "makeappx pack failed."
    exit $LASTEXITCODE
}

# Create msixupload (ZIP containing msix for Partner Center)
Write-Host "==> 5. Creating Store upload package (.msixupload)..." -ForegroundColor Cyan
Compress-Archive -Path $msixPath -DestinationPath $msixUploadPath -Force

Write-Host "`n==> MSIX Packaging Completed Successfully!" -ForegroundColor Green
Write-Host "MSIX Package: $msixPath" -ForegroundColor Yellow
Write-Host "Store Upload Package: $msixUploadPath" -ForegroundColor Yellow
