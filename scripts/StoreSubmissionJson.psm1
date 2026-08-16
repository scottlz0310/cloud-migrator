<#
.SYNOPSIS
    Microsoft Store submission JSON から公開判定に必要なメタデータを取得する。

.DESCRIPTION
    Store listing の説明文に不正な `\u` 断片が含まれる場合でも、JSON 全体を
    解析できる形へ限定的に補正して Status と package 名を取得する。補正対象は
    4 桁の hexadecimal ではない Unicode escape だけで、正しい escape と、
    JSON 上の文字列としての `\\u` は変更しない。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-StoreSubmissionMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$JsonPath
    )

    if (-not (Test-Path -LiteralPath $JsonPath -PathType Leaf)) {
        throw "既存 submission の JSON 出力が見つかりません: $JsonPath"
    }

    try {
        $jsonText = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $JsonPath -ErrorAction Stop).Path)
    } catch {
        throw "既存 submission の JSON 出力を読み込めませんでした。パス: $JsonPath。$($_.Exception.Message)"
    }

    $jsonText = [regex]::Replace($jsonText, '\x1b\[[0-?]*[ -/]*[@-~]', '')
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        throw "既存 submission の JSON を CLI 出力から取得できませんでした。パス: $JsonPath"
    }

    $invalidUnicodePattern = '(?<prefix>(?<!\\)(?:\\\\)*)\\u(?![0-9A-Fa-f]{4})'
    $repairedInvalidUnicodeEscapeCount = [regex]::Matches($jsonText, $invalidUnicodePattern).Count
    if ($repairedInvalidUnicodeEscapeCount -gt 0) {
        $jsonText = [regex]::Replace($jsonText, $invalidUnicodePattern, '${prefix}\\u')
    }

    try {
        $submission = $jsonText | ConvertFrom-Json
    } catch {
        throw "既存 submission の JSON を解析できませんでした。パス: $JsonPath。$($_.Exception.Message)"
    }

    $statusProperty = $submission.PSObject.Properties['Status']
    if ($null -eq $statusProperty -or [string]::IsNullOrWhiteSpace([string]$statusProperty.Value)) {
        throw "既存 submission の JSON に Status がありません。パス: $JsonPath"
    }

    $packagesProperty = $submission.PSObject.Properties['ApplicationPackages']
    if ($null -eq $packagesProperty) {
        throw "既存 submission の JSON に ApplicationPackages がありません。パス: $JsonPath"
    }

    $packageNames = @(
        $packagesProperty.Value |
            Where-Object { $null -ne $_ } |
            ForEach-Object {
                $fileNameProperty = $_.PSObject.Properties['FileName']
                if ($null -ne $fileNameProperty -and -not [string]::IsNullOrWhiteSpace([string]$fileNameProperty.Value)) {
                    [string]$fileNameProperty.Value
                }
            }
    )

    return [pscustomobject]@{
        Status                                  = [string]$statusProperty.Value
        PackageNames                            = $packageNames
        RepairedInvalidUnicodeEscapeCount      = $repairedInvalidUnicodeEscapeCount
    }
}

Export-ModuleMember -Function Read-StoreSubmissionMetadata
