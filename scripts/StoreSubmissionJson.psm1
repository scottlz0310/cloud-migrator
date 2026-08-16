<#
.SYNOPSIS
    Microsoft Store submission JSON から公開判定に必要なメタデータを取得する。

.DESCRIPTION
    Store listing の説明文に JSON 仕様外の escape が含まれる場合でも、JSON 全体を
    解析できる形へ限定的に補正して Status と package 名を取得する。正規の JSON
    escape と、JSON 上の文字列としての `\\u` は変更しない。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Repair-InvalidJsonEscapes {
    param(
        [Parameter(Mandatory)]
        [string]$JsonText
    )

    $builder = [System.Text.StringBuilder]::new($JsonText.Length)
    $insideString = $false
    $repairedInvalidEscapeCount = 0
    $backslash = [char]0x5c

    for ($index = 0; $index -lt $JsonText.Length; $index++) {
        $current = $JsonText[$index]
        if (-not $insideString) {
            [void]$builder.Append($current)
            if ($current -eq [char]0x22) {
                $insideString = $true
            }
            continue
        }

        if ($current -eq [char]0x22) {
            [void]$builder.Append($current)
            $insideString = $false
            continue
        }

        if ($current -ne $backslash) {
            [void]$builder.Append($current)
            continue
        }

        $nextIndex = $index + 1
        if ($nextIndex -ge $JsonText.Length) {
            [void]$builder.Append($backslash)
            [void]$builder.Append($backslash)
            $repairedInvalidEscapeCount++
            continue
        }

        $next = $JsonText[$nextIndex]
        if ([string]$next -in @('"', '\', '/', 'b', 'f', 'n', 'r', 't')) {
            [void]$builder.Append($current)
            [void]$builder.Append($next)
            $index = $nextIndex
            continue
        }

        if ($next -eq 'u' -and $nextIndex + 4 -lt $JsonText.Length) {
            $unicodeDigits = $JsonText.Substring($nextIndex + 1, 4)
            if ($unicodeDigits -match '^[0-9A-Fa-f]{4}$') {
                [void]$builder.Append($current)
                [void]$builder.Append($JsonText.Substring($nextIndex, 5))
                $index = $nextIndex + 4
                continue
            }
        }

        [void]$builder.Append($backslash)
        [void]$builder.Append($backslash)
        $repairedInvalidEscapeCount++
    }

    return [pscustomobject]@{
        JsonText                  = $builder.ToString()
        RepairedInvalidEscapeCount = $repairedInvalidEscapeCount
    }
}

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

    $repairedJson = Repair-InvalidJsonEscapes -JsonText $jsonText
    $jsonText = $repairedJson.JsonText

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
        RepairedInvalidEscapeCount              = $repairedJson.RepairedInvalidEscapeCount
    }
}

Export-ModuleMember -Function Read-StoreSubmissionMetadata
