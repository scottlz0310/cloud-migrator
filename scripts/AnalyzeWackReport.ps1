<#
.SYNOPSIS
    WACK XML レポートを解析し、Required/Optional failure を要約する。

.PARAMETER ReportPath
    解析対象の WACK XML レポート。

.PARAMETER FailOnRequiredFailure
    Required failure または全体結果 FAIL を検出した場合に exit 1 を返す。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$ReportPath,
    [switch]$FailOnRequiredFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
    throw "WACK XML レポートが見つかりません: $ReportPath"
}

$resolvedReportPath = (Resolve-Path -LiteralPath $ReportPath).Path
$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
try {
    $xml.Load($resolvedReportPath)
} catch {
    throw "WACK XML レポートを解析できません: $resolvedReportPath`n$($_.Exception.Message)"
}

$report = $xml.SelectSingleNode("/REPORT")
if ($null -eq $report) {
    throw "WACK XML に REPORT 要素がありません: $resolvedReportPath"
}

$requirements = @($report.SelectNodes("./REQUIREMENTS/REQUIREMENT"))
$tests = @(
    $requirements | ForEach-Object {
        @($_.SelectNodes("./TEST"))
    }
)
if ($tests.Count -eq 0) {
    throw "WACK XML に TEST 要素がありません。実行が未完了の可能性があります: $resolvedReportPath"
}

function Get-TestAttribute {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlElement]$Test,
        [Parameter(Mandatory)]
        [string]$Name
    )

    $attribute = $Test.GetAttributeNode($Name)
    if ($null -eq $attribute) {
        return ""
    }

    return $attribute.Value
}

function Get-TestResult {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlElement]$Test
    )

    $result = $Test.SelectSingleNode("./RESULT")
    if ($null -eq $result) {
        return ""
    }

    return $result.InnerText.Trim().ToUpperInvariant()
}

$overallResult = $report.GetAttribute("OVERALL_RESULT").Trim().ToUpperInvariant()
$failedTests = @($tests | Where-Object { (Get-TestResult $_) -match "FAIL" })
$passedTests = @($tests | Where-Object { (Get-TestResult $_) -match "PASS" })
$requiredFailures = @(
    $failedTests | Where-Object { (Get-TestAttribute $_ "OPTIONAL").ToUpperInvariant() -ne "TRUE" }
)
$optionalFailures = @(
    $failedTests | Where-Object { (Get-TestAttribute $_ "OPTIONAL").ToUpperInvariant() -eq "TRUE" }
)

Write-Host ""
Write-Host "=== WACK テスト結果 ===" -ForegroundColor Cyan
Write-Host "全体結果: $overallResult" -ForegroundColor $(if ($overallResult -eq "PASS") { "Green" } else { "Red" })
Write-Host "アプリ: $($report.GetAttribute("APP_NAME")) $($report.GetAttribute("APP_VERSION"))" -ForegroundColor Gray
Write-Host "WACK: $($report.GetAttribute("VERSION")) / OS: $($report.GetAttribute("OS")) $($report.GetAttribute("OSVERSION"))" -ForegroundColor Gray
Write-Host "レポート: $resolvedReportPath" -ForegroundColor Gray
Write-Host "テスト総数: $($tests.Count) / 合格: $($passedTests.Count) / 失敗: $($failedTests.Count)" -ForegroundColor White
Write-Host "Required failure: $($requiredFailures.Count)" -ForegroundColor $(if ($requiredFailures.Count -eq 0) { "Green" } else { "Red" })
Write-Host "Optional failure: $($optionalFailures.Count)" -ForegroundColor $(if ($optionalFailures.Count -eq 0) { "Green" } else { "Yellow" })

if ($failedTests.Count -gt 0) {
    Write-Host ""
    Write-Host "=== 失敗項目 ===" -ForegroundColor Red

    foreach ($requirement in $requirements) {
        $requirementFailures = @(
            $requirement.SelectNodes("./TEST") |
                Where-Object { (Get-TestResult $_) -match "FAIL" }
        )
        if ($requirementFailures.Count -eq 0) {
            continue
        }

        Write-Host "[$($requirement.GetAttribute("NUMBER"))] $($requirement.GetAttribute("TITLE"))" -ForegroundColor Cyan
        foreach ($test in $requirementFailures) {
            $optionalLabel = if ((Get-TestAttribute $test "OPTIONAL").ToUpperInvariant() -eq "TRUE") { "Optional" } else { "Required" }
            Write-Host "  [$($test.GetAttribute("INDEX"))] [$optionalLabel] $($test.GetAttribute("NAME"))" -ForegroundColor Yellow
            Write-Host "    $($test.GetAttribute("DESCRIPTION"))" -ForegroundColor Gray

            $messages = @($test.SelectNodes("./MESSAGES/MESSAGE"))
            foreach ($message in $messages) {
                Write-Host "    - $($message.GetAttribute("TEXT"))" -ForegroundColor Gray
            }
        }
    }
}

if ($FailOnRequiredFailure -and ($requiredFailures.Count -gt 0 -or $overallResult -ne "PASS")) {
    exit 1
}
