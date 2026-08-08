<#
.SYNOPSIS
    MSIX/AppX パッケージの manifest、リソース、アセットを静的検査する。

.DESCRIPTION
    makeappx.exe でパッケージを一時展開し、リポジトリの期待 manifest と
    Identity、Version、アーキテクチャ、言語、capability、参照画像を比較する。
    WACK は実行せず、PR の Windows runner でも再現できる検査だけを行う。

.PARAMETER PackagePath
    検査対象の .msix または .appx。

.PARAMETER ManifestPath
    期待値として比較する manifest。既定値は installer/msix/Package.appxmanifest。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$PackagePath,
    [string]$ManifestPath = "installer/msix/Package.appxmanifest"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepositoryFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Description
    )

    $fullPath = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
    }

    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Description が見つかりません: $fullPath"
    }

    return (Resolve-Path -LiteralPath $fullPath).Path
}

function Find-MakeAppx {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        throw "Program Files (x86) が見つからないため、Windows SDK の makeappx.exe を検索できません。"
    }

    $windowsKitsBin = Join-Path $programFilesX86 "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $windowsKitsBin -PathType Container)) {
        throw "Windows SDK の bin ディレクトリが見つかりません: $windowsKitsBin"
    }

    $makeAppx = Get-ChildItem -LiteralPath $windowsKitsBin -Recurse -File -Filter "makeappx.exe" |
        Where-Object { $_.FullName -match "\\x64\\makeappx\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $makeAppx) {
        throw "Windows SDK の x64 版 makeappx.exe が見つかりません。"
    }

    return $makeAppx.FullName
}

function Get-ManifestValue {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode]$Node,
        [string]$AttributeName = ""
    )

    if ([string]::IsNullOrWhiteSpace($AttributeName)) {
        return $Node.InnerText.Trim()
    }

    $attribute = $Node.Attributes[$AttributeName]
    if ($null -eq $attribute) {
        return ""
    }

    return $attribute.Value
}

function Get-ManifestImagePaths {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlDocument]$Manifest,
        [Parameter(Mandatory)]
        [System.Xml.XmlNamespaceManager]$Namespace
    )

    $properties = $Manifest.SelectSingleNode(
        "/foundation:Package/foundation:Properties",
        $Namespace)
    $visualElements = $Manifest.SelectSingleNode(
        "/foundation:Package/foundation:Applications/foundation:Application/uap:VisualElements",
        $Namespace)
    $defaultTile = $Manifest.SelectSingleNode(
        "/foundation:Package/foundation:Applications/foundation:Application/uap:VisualElements/uap:DefaultTile",
        $Namespace)
    if ($null -eq $properties -or $null -eq $visualElements -or $null -eq $defaultTile) {
        throw "manifest の Properties / VisualElements / DefaultTile が見つかりません。"
    }

    return @(
        (Get-ManifestValue $properties.SelectSingleNode("foundation:Logo", $Namespace) ""),
        (Get-ManifestValue $visualElements "Square150x150Logo"),
        (Get-ManifestValue $visualElements "Square44x44Logo"),
        (Get-ManifestValue $defaultTile "Wide310x150Logo")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Expected,
        [Parameter(Mandatory)]
        [string]$Actual
    )

    if ($Expected -ne $Actual) {
        throw "$Name が一致しません。expected='$Expected', actual='$Actual'"
    }
}

function Assert-SetEqual {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string[]]$Expected,
        [Parameter(Mandatory)]
        [string[]]$Actual
    )

    $expectedText = (@($Expected | Sort-Object -Unique) -join ",")
    $actualText = (@($Actual | Sort-Object -Unique) -join ",")
    Assert-Equal $Name $expectedText $actualText
}

$packageFullPath = Resolve-RepositoryFile $PackagePath "検査対象パッケージ"
$expectedManifestPath = Resolve-RepositoryFile $ManifestPath "期待 manifest"
$packageExtension = [System.IO.Path]::GetExtension($packageFullPath).ToLowerInvariant()
if (@(".msix", ".appx") -notcontains $packageExtension) {
    throw "検査対象には .msix または .appx を指定してください: $packageFullPath"
}

$makeAppx = Find-MakeAppx
$namespaceUri = "http://schemas.microsoft.com/appx/manifest/foundation/windows10"
$uapNamespaceUri = "http://schemas.microsoft.com/appx/manifest/uap/windows10"

[xml]$expectedManifest = Get-Content -LiteralPath $expectedManifestPath -Raw
$expectedNamespace = New-Object System.Xml.XmlNamespaceManager($expectedManifest.NameTable)
$expectedNamespace.AddNamespace("foundation", $namespaceUri)
$expectedNamespace.AddNamespace("uap", $uapNamespaceUri)
$expectedIdentity = $expectedManifest.SelectSingleNode(
    "/foundation:Package/foundation:Identity",
    $expectedNamespace)
$expectedProperties = $expectedManifest.SelectSingleNode(
    "/foundation:Package/foundation:Properties",
    $expectedNamespace)
$expectedApplication = $expectedManifest.SelectSingleNode(
    "/foundation:Package/foundation:Applications/foundation:Application",
    $expectedNamespace)
if ($null -eq $expectedIdentity -or $null -eq $expectedProperties -or $null -eq $expectedApplication) {
    throw "期待 manifest に Identity / Properties / Application が見つかりません。"
}

$expectedResources = @(
    $expectedManifest.SelectNodes(
        "/foundation:Package/foundation:Resources/foundation:Resource",
        $expectedNamespace) |
        ForEach-Object { Get-ManifestValue $_ "Language" }
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$expectedCapabilities = @(
    $expectedManifest.SelectNodes(
        "/foundation:Package/foundation:Capabilities/*",
        $expectedNamespace) |
        ForEach-Object { Get-ManifestValue $_ "Name" }
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$expectedImagePaths = Get-ManifestImagePaths $expectedManifest $expectedNamespace

$extractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
    "cloud-migrator-msix-validate-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $extractDirectory | Out-Null
try {
    Write-Host "MSIX を展開しています: $packageFullPath" -ForegroundColor Cyan
    & $makeAppx unpack /p $packageFullPath /d $extractDirectory /l
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx unpack に失敗しました（exit=$LASTEXITCODE）。"
    }

    $packageManifestPath = Join-Path $extractDirectory "AppxManifest.xml"
    if (-not (Test-Path -LiteralPath $packageManifestPath -PathType Leaf)) {
        throw "パッケージ内に AppxManifest.xml がありません。"
    }
    [xml]$packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw
    $packageNamespace = New-Object System.Xml.XmlNamespaceManager($packageManifest.NameTable)
    $packageNamespace.AddNamespace("foundation", $namespaceUri)
    $packageNamespace.AddNamespace("uap", $uapNamespaceUri)
    $packageIdentity = $packageManifest.SelectSingleNode(
        "/foundation:Package/foundation:Identity",
        $packageNamespace)
    $packageProperties = $packageManifest.SelectSingleNode(
        "/foundation:Package/foundation:Properties",
        $packageNamespace)
    $packageApplication = $packageManifest.SelectSingleNode(
        "/foundation:Package/foundation:Applications/foundation:Application",
        $packageNamespace)
    if ($null -eq $packageIdentity -or $null -eq $packageProperties -or $null -eq $packageApplication) {
        throw "パッケージ内 manifest に Identity / Properties / Application が見つかりません。"
    }

    foreach ($attributeName in @("Name", "Publisher", "Version", "ProcessorArchitecture")) {
        Assert-Equal `
            "Identity.$attributeName" `
            (Get-ManifestValue $expectedIdentity $attributeName) `
            (Get-ManifestValue $packageIdentity $attributeName)
    }
    Assert-Equal `
        "Properties.DisplayName" `
        (Get-ManifestValue $expectedProperties.SelectSingleNode("foundation:DisplayName", $expectedNamespace) "") `
        (Get-ManifestValue $packageProperties.SelectSingleNode("foundation:DisplayName", $packageNamespace) "")
    Assert-Equal `
        "Properties.PublisherDisplayName" `
        (Get-ManifestValue $expectedProperties.SelectSingleNode("foundation:PublisherDisplayName", $expectedNamespace) "") `
        (Get-ManifestValue $packageProperties.SelectSingleNode("foundation:PublisherDisplayName", $packageNamespace) "")

    $packageResources = @(
        $packageManifest.SelectNodes(
            "/foundation:Package/foundation:Resources/foundation:Resource",
            $packageNamespace) |
            ForEach-Object { Get-ManifestValue $_ "Language" }
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    Assert-SetEqual "Resources.Language" $expectedResources $packageResources

    $packageCapabilities = @(
        $packageManifest.SelectNodes(
            "/foundation:Package/foundation:Capabilities/*",
            $packageNamespace) |
            ForEach-Object { Get-ManifestValue $_ "Name" }
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    Assert-SetEqual "Capabilities" $expectedCapabilities $packageCapabilities

    Assert-Equal `
        "Application.Executable" `
        (Get-ManifestValue $expectedApplication "Executable") `
        (Get-ManifestValue $packageApplication "Executable")

    $imagePaths = Get-ManifestImagePaths $packageManifest $packageNamespace
    if ($imagePaths.Count -ne 4) {
        throw "manifest が参照する画像数が 4 ではありません: $($imagePaths.Count)"
    }
    Assert-SetEqual "Manifest image paths" $expectedImagePaths $imagePaths
    foreach ($imagePath in $imagePaths) {
        $packageImagePath = Join-Path $extractDirectory ($imagePath -replace "[/]", "\")
        if (-not (Test-Path -LiteralPath $packageImagePath -PathType Leaf)) {
            throw "manifest が参照する画像がパッケージ内にありません: $imagePath"
        }
    }

    $resourcesPriPath = Join-Path $extractDirectory "resources.pri"
    if (-not (Test-Path -LiteralPath $resourcesPriPath -PathType Leaf)) {
        throw "パッケージ内に resources.pri がありません。"
    }

    $packageHash = (Get-FileHash -LiteralPath $packageFullPath -Algorithm SHA256).Hash
    Write-Host "MSIX 静的検査: PASS" -ForegroundColor Green
    Write-Host "Identity: $($packageIdentity.Name)" -ForegroundColor Gray
    Write-Host "Version: $($packageIdentity.Version) / Architecture: $($packageIdentity.ProcessorArchitecture)" -ForegroundColor Gray
    Write-Host "Languages: $($packageResources -join ', ')" -ForegroundColor Gray
    Write-Host "Assets: $($imagePaths -join ', ')" -ForegroundColor Gray
    Write-Host "SHA-256: $packageHash" -ForegroundColor Gray

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        @(
            "## MSIX 静的検査",
            "",
            "- 結果: PASS",
            ("- Identity: " + $packageIdentity.Name),
            ("- Version: " + $packageIdentity.Version),
            ("- Architecture: " + $packageIdentity.ProcessorArchitecture),
            ("- Languages: " + ($packageResources -join ", ")),
            ("- SHA-256: " + $packageHash)
        ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
} finally {
    if (Test-Path -LiteralPath $extractDirectory) {
        Remove-Item -LiteralPath $extractDirectory -Recurse -Force
    }
}
