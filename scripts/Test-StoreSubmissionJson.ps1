<#
.SYNOPSIS
    Store submission JSON の許容的な解析を検証する。
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'StoreSubmissionJson.psm1') -Force

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [object]$Expected,
        [Parameter(Mandatory)]
        [object]$Actual,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($Expected -ne $Actual) {
        throw "テスト '$Name' が失敗しました。期待値: '$Expected'、実際: '$Actual'"
    }
}

function Invoke-ParserTest {
    param(
        [Parameter(Mandatory)]
        [string]$Json,
        [Parameter(Mandatory)]
        [int]$ExpectedRepairCount,
        [Parameter(Mandatory)]
        [string]$Name
    )

    $temporaryFile = New-TemporaryFile
    try {
        Set-Content -LiteralPath $temporaryFile.FullName -Value $Json -Encoding utf8
        $metadata = Read-StoreSubmissionMetadata -JsonPath $temporaryFile.FullName

        Assert-Equal -Expected 'CommitStarted' -Actual $metadata.Status -Name "$Name / Status"
        Assert-Equal -Expected $ExpectedRepairCount -Actual $metadata.RepairedInvalidEscapeCount -Name "$Name / 補正件数"
        if ('CloudMigrator_0.7.2.0_x64.msix' -notin @($metadata.PackageNames)) {
            throw "テスト '$Name' が失敗しました。期待する package 名を取得できません。"
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryFile.FullName -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryFile.FullName -Force
        }
    }
}

Invoke-ParserTest -Name '不正な Unicode escape' -ExpectedRepairCount 1 -Json @'
{
  "Status": "CommitStarted",
  "Listings": {
    "ja-jp": {
      "BaseListing": {
        "Description": "literal \u 30"
      }
    }
  },
  "ApplicationPackages": [
    { "FileName": "CloudMigrator_0.7.2.0_x64.msix" }
  ]
}
'@

Invoke-ParserTest -Name '文字列中の literal escape' -ExpectedRepairCount 0 -Json @'
{
  "Status": "CommitStarted",
  "Listings": {
    "ja-jp": {
      "BaseListing": {
        "Description": "literal \\u 30"
      }
    }
  },
  "ApplicationPackages": [
    { "FileName": "CloudMigrator_0.7.2.0_x64.msix" }
  ]
}
'@

Invoke-ParserTest -Name '正しい Unicode escape' -ExpectedRepairCount 0 -Json @'
{
  "Status": "CommitStarted",
  "Listings": {
    "ja-jp": {
      "BaseListing": {
        "Description": "\u3042"
      }
    }
  },
  "ApplicationPackages": [
    { "FileName": "CloudMigrator_0.7.2.0_x64.msix" }
  ]
}
'@

Invoke-ParserTest -Name 'その他の不正な escape' -ExpectedRepairCount 3 -Json @'
{
  "Status": "CommitStarted",
  "Listings": {
    "ja-jp": {
      "BaseListing": {
        "Description": "C:\store\app path\ file"
      }
    }
  },
  "ApplicationPackages": [
    { "FileName": "CloudMigrator_0.7.2.0_x64.msix" }
  ]
}
'@

Write-Host 'Store submission JSON の解析テストが成功しました。'
