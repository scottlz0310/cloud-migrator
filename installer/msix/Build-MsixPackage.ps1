[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$Version = "0.7.1.0",
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
$manifestPath = Join-Path $publishDir "AppxManifest.xml"
Copy-Item (Join-Path $PSScriptRoot "Package.appxmanifest") $manifestPath -Force
Copy-Item (Join-Path $PSScriptRoot "Assets") $publishDir -Recurse -Force

Write-Host "==> 3. Validating package manifest..." -ForegroundColor Cyan
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifestNamespace = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$manifestNamespace.AddNamespace("foundation", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$manifestNamespace.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")

$publisherDisplayName = $manifest.SelectSingleNode(
    "/foundation:Package/foundation:Properties/foundation:PublisherDisplayName",
    $manifestNamespace)
if (-not $publisherDisplayName -or [string]::IsNullOrWhiteSpace($publisherDisplayName.InnerText)) {
    throw "AppxManifest.xml の PublisherDisplayName が空です。"
}

$languageNodes = @($manifest.SelectNodes(
    "/foundation:Package/foundation:Resources/foundation:Resource",
    $manifestNamespace))
$languages = @($languageNodes | ForEach-Object { $_.GetAttribute("Language") } | Where-Object { $_ })
if ($languages.Count -eq 0) {
    throw "AppxManifest.xml に少なくとも 1 つの Resource Language が必要です。"
}

$referencedImagePaths = @(
    $manifest.SelectSingleNode(
        "/foundation:Package/foundation:Properties/foundation:Logo",
        $manifestNamespace).InnerText
    $manifest.SelectSingleNode(
        "/foundation:Package/foundation:Applications/foundation:Application/uap:VisualElements",
        $manifestNamespace).Square150x150Logo
    $manifest.SelectSingleNode(
        "/foundation:Package/foundation:Applications/foundation:Application/uap:VisualElements",
        $manifestNamespace).Square44x44Logo
    $manifest.SelectSingleNode(
        "/foundation:Package/foundation:Applications/foundation:Application/uap:VisualElements/uap:DefaultTile",
        $manifestNamespace).Wide310x150Logo
) | Where-Object { $_ }

foreach ($imagePath in $referencedImagePaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $imagePath))) {
        throw "AppxManifest.xml が参照する画像が見つかりません: $imagePath"
    }
}

Write-Host "PublisherDisplayName: $($publisherDisplayName.InnerText)" -ForegroundColor Green
Write-Host "Supported languages: $($languages -join ', ')" -ForegroundColor Green

Write-Host "==> 4. Finding Windows SDK packaging tools..." -ForegroundColor Cyan
$makeAppx = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
if (-not (Test-Path $makeAppx)) {
    $makeAppx = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -File -Include makeappx.exe | 
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $makeAppx -or -not (Test-Path $makeAppx)) {
    Write-Error "makeappx.exe not found in Windows Kits."
    exit 1
}

Write-Host "Found makeappx.exe at: $makeAppx" -ForegroundColor Green

$makePri = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe"
if (-not (Test-Path $makePri)) {
    $makePri = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -File -Include makepri.exe |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $makePri -or -not (Test-Path $makePri)) {
    Write-Error "makepri.exe not found in Windows Kits."
    exit 1
}

Write-Host "Found makepri.exe at: $makePri" -ForegroundColor Green

Write-Host "==> 5. Generating package resource index (resources.pri)..." -ForegroundColor Cyan
$priConfigPath = Join-Path ([System.IO.Path]::GetTempPath()) "CloudMigrator-priconfig-$([guid]::NewGuid().ToString('N')).xml"
$resourcesPriPath = Join-Path $publishDir "resources.pri"
try {
    & $makePri createconfig /cf $priConfigPath /dq ($languages -join "_") /o
    if ($LASTEXITCODE -ne 0) {
        throw "makepri createconfig failed."
    }

    & $makePri new /pr $publishDir /cf $priConfigPath /mn $manifestPath /of $resourcesPriPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "makepri new failed."
    }
}
finally {
    if (Test-Path -LiteralPath $priConfigPath) {
        Remove-Item -LiteralPath $priConfigPath -Force
    }
}

if (-not (Test-Path -LiteralPath $resourcesPriPath)) {
    throw "resources.pri の生成に失敗しました。"
}

$outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "AppPackages"))
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$msixPath = Join-Path $outDir "CloudMigrator_${Version}_x64.msix"
$msixUploadPath = Join-Path $outDir "CloudMigrator_${Version}_x64_bundle.msixupload"

if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
if (Test-Path $msixUploadPath) { Remove-Item $msixUploadPath -Force }

Write-Host "==> 6. Packing MSIX package..." -ForegroundColor Cyan
& $makeAppx pack /d $publishDir /p $msixPath /l

if ($LASTEXITCODE -ne 0) {
    Write-Error "makeappx pack failed."
    exit $LASTEXITCODE
}

# Create msixupload (ZIP containing msix for Partner Center)
Write-Host "==> 7. Creating Store upload package (.msixupload)..." -ForegroundColor Cyan
Compress-Archive -Path $msixPath -DestinationPath $msixUploadPath -Force

Write-Host "`n==> MSIX Packaging Completed Successfully!" -ForegroundColor Green
Write-Host "MSIX Package: $msixPath" -ForegroundColor Yellow
Write-Host "Store Upload Package: $msixUploadPath" -ForegroundColor Yellow
