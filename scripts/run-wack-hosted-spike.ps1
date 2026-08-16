<#
.SYNOPSIS
    GitHub-hosted runner 上で WACK 本体の実行可否を検証する Spike 専用 wrapper。

.DESCRIPTION
    本番用 run-wack-test.ps1 の UserInteractive / 管理者権限ガードや UAC 昇格を
    変更せず、appcert.exe の直接実行と既存の AnalyzeWackReport.ps1 による判定だけを
    検証する。実験用の環境変数と AppCertKit 作業領域は指定した WorkDirectory に隔離する。

    本番の release.yml / wack.yml からは呼び出さない。Microsoft Store への提出も行わない。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [Parameter(Mandatory)]
    [string]$AppCertPath,
    [Parameter(Mandatory)]
    [string]$ReportDirectory,
    [Parameter(Mandatory)]
    [string]$WorkDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$environmentNames = @(
    "TEMP",
    "TMP",
    "USERPROFILE",
    "LOCALAPPDATA",
    "APPDATA",
    "HOMEDRIVE",
    "HOMEPATH",
    "TAEF_LOG_DIR",
    "TAEF_LOG_ROOT",
    "TAEF_LOG_PATH"
)
$originalEnvironment = @{}
$environmentIsolationApplied = $false

function Resolve-InputPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Description
    )

    $resolvedPath = Resolve-InputPath $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "$Description が見つかりません: $resolvedPath"
    }

    return (Resolve-Path -LiteralPath $resolvedPath).Path
}

function Get-PwshExecutable {
    $currentPwsh = Join-Path $PSHOME "pwsh.exe"
    if (Test-Path -LiteralPath $currentPwsh -PathType Leaf) {
        return (Resolve-Path -LiteralPath $currentPwsh).Path
    }

    $pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($null -ne $pwshCommand -and -not [string]::IsNullOrWhiteSpace($pwshCommand.Source)) {
        return (Resolve-Path -LiteralPath $pwshCommand.Source).Path
    }

    throw "WACK のレポート解析に使用する pwsh.exe が見つかりません。"
}

function Set-IsolatedEnvironment {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Values,
        [Parameter(Mandatory)]
        [string[]]$Directories
    )

    foreach ($directory in $Directories) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    foreach ($name in $environmentNames) {
        $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable(
            $name,
            [EnvironmentVariableTarget]::Process
        )
        [Environment]::SetEnvironmentVariable(
            $name,
            [string]$Values[$name],
            [EnvironmentVariableTarget]::Process
        )
    }

    $script:environmentIsolationApplied = $true
}

function Restore-Environment {
    if (-not $script:environmentIsolationApplied) {
        return
    }

    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $originalEnvironment[$name],
            [EnvironmentVariableTarget]::Process
        )
    }

    $script:environmentIsolationApplied = $false
}

function Invoke-AppCert {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& $appCertFullPath @Arguments 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = @($output | ForEach-Object { $_.ToString() })
    }
}

function Write-CommandLog {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [int]$ExitCode,
        [AllowEmptyCollection()]
        [string[]]$Output = @()
    )

    $lines = @(
        "[$([DateTime]::UtcNow.ToString('O'))] $Name exit=$ExitCode"
    ) + $Output
    Add-Content -LiteralPath $commandLogPath -Value $lines
}

$packageFullPath = Resolve-ExistingFile $PackagePath "検証対象パッケージ"
$appCertFullPath = Resolve-ExistingFile $AppCertPath "appcert.exe"
$reportDirectoryFullPath = Resolve-InputPath $ReportDirectory
$workDirectoryFullPath = Resolve-InputPath $WorkDirectory

if ([System.IO.Path]::GetExtension($packageFullPath).ToLowerInvariant() -ne ".msix") {
    throw "Hosted runner Spike の検証対象は .msix である必要があります: $packageFullPath"
}

New-Item -ItemType Directory -Force -Path $reportDirectoryFullPath | Out-Null
New-Item -ItemType Directory -Force -Path $workDirectoryFullPath | Out-Null
$commandLogPath = Join-Path $reportDirectoryFullPath "appcert-command-output.log"
$packageName = [System.IO.Path]::GetFileNameWithoutExtension($packageFullPath)
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmssfff")
$reportPath = Join-Path $reportDirectoryFullPath "$packageName-$timestamp.xml"
$reportBaseName = [System.IO.Path]::GetFileNameWithoutExtension($reportPath)
$packageHash = (Get-FileHash -LiteralPath $packageFullPath -Algorithm SHA256).Hash.ToLowerInvariant()

$wackProfile = Join-Path $workDirectoryFullPath "profile"
$wackLocalAppData = Join-Path $wackProfile "AppData\Local"
$wackRoamingAppData = Join-Path $wackProfile "AppData\Roaming"
$wackTemp = Join-Path $wackLocalAppData "Temp"
$wackLogs = Join-Path $workDirectoryFullPath "logs"
$wackAppCertDirectory = Join-Path $wackLocalAppData "Microsoft\AppCertKit"
$appVerifierLogs = Join-Path $wackLocalAppData "AppVerifierLogs"

$wackEnvironment = @{
    TEMP = $wackTemp
    TMP = $wackTemp
    USERPROFILE = $wackProfile
    LOCALAPPDATA = $wackLocalAppData
    APPDATA = $wackRoamingAppData
    HOMEDRIVE = Split-Path $wackProfile -Qualifier
    HOMEPATH = $wackProfile.Substring((Split-Path $wackProfile -Qualifier).Length)
    TAEF_LOG_DIR = $wackLogs
    TAEF_LOG_ROOT = $wackLogs
    TAEF_LOG_PATH = $wackLogs
}

$resultPath = Join-Path $reportDirectoryFullPath "hosted-wack-result.json"
$startedAt = Get-Date
$wackStage = "not-started"
$reportGenerated = $false
$analysisAttempted = $false
$analysisExitCode = $null
try {
    $wackStage = "environment-isolation"
    Set-IsolatedEnvironment -Values $wackEnvironment -Directories @(
        $wackTemp,
        $wackLogs,
        $wackLocalAppData,
        $wackRoamingAppData,
        $appVerifierLogs
    )

    $wackStage = "reset"
    $reset = Invoke-AppCert -Arguments @("reset")
    Write-CommandLog -Name "appcert reset" -ExitCode $reset.ExitCode -Output $reset.Output
    if ($reset.ExitCode -ne 0) {
        throw "WACK の reset に失敗しました（exit=$($reset.ExitCode)）。"
    }

    $wackStage = "test"
    $test = Invoke-AppCert -Arguments @(
        "test",
        "-appxpackagepath",
        $packageFullPath,
        "-reportoutputpath",
        $reportPath
    )
    Write-CommandLog -Name "appcert test" -ExitCode $test.ExitCode -Output $test.Output

    if ($test.ExitCode -eq 1) {
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "WACK が終了コード 1 を返しましたが、最終化対象の XML レポートがありません。"
        }

        $finalize = Invoke-AppCert -Arguments @(
            "finalizereport",
            "-reportfilepath",
            $reportPath
        )
        Write-CommandLog -Name "appcert finalizereport" -ExitCode $finalize.ExitCode -Output $finalize.Output
        if ($finalize.ExitCode -ne 0) {
            throw "WACK レポートの最終化に失敗しました（exit=$($finalize.ExitCode)）。"
        }
    } elseif ($test.ExitCode -ne 0) {
        throw "WACK の実行に失敗しました（exit=$($test.ExitCode)）。"
    }

    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "WACK の XML レポートが生成されませんでした: $reportPath"
    }
    $reportGenerated = $true

    $wackStage = "html-report"
    $htmlCandidates = @()
    if (Test-Path -LiteralPath $wackAppCertDirectory -PathType Container) {
        $htmlCandidates = @(
            Get-ChildItem -LiteralPath $wackAppCertDirectory -File -Recurse |
                Where-Object {
                    @(".htm", ".html") -contains $_.Extension.ToLowerInvariant() -and
                    ($_.BaseName -eq $reportBaseName -or $_.LastWriteTime -ge $startedAt)
                }
        )
    }
    $htmlSource = $htmlCandidates |
        Where-Object { $_.BaseName -eq $reportBaseName } |
        Select-Object -First 1
    if ($null -eq $htmlSource) {
        $htmlSource = $htmlCandidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if ($null -eq $htmlSource) {
        throw "WACK XML は生成されましたが、HTML/HTM レポートが見つかりません: $wackAppCertDirectory"
    }

    $htmlReportPath = Join-Path $reportDirectoryFullPath "$reportBaseName$($htmlSource.Extension)"
    Copy-Item -LiteralPath $htmlSource.FullName -Destination $htmlReportPath -Force

    $wackStage = "analyze"
    $analyzerPath = Join-Path $PSScriptRoot "AnalyzeWackReport.ps1"
    $pwshPath = Get-PwshExecutable
    $analysisAttempted = $true
    $analysisOutput = @(
        & $pwshPath -NoProfile -ExecutionPolicy Bypass -File $analyzerPath `
            -ReportPath $reportPath `
            -FailOnRequiredFailure `
            2>&1
    )
    $analysisExitCode = $LASTEXITCODE
    $analysisText = @($analysisOutput | ForEach-Object { $_.ToString() })
    $analysisLogLines = @(
        "[$([DateTime]::UtcNow.ToString('O'))] AnalyzeWackReport exit=$analysisExitCode"
    ) + $analysisText
    Add-Content -LiteralPath $commandLogPath -Value $analysisLogLines
    $analysisText | ForEach-Object { Write-Host $_ }
    if ($analysisExitCode -ne 0) {
        throw "WACK XML の判定で Required failure またはレポート異常が検出されました（exit=$analysisExitCode）。"
    }

    $result = [ordered]@{
        schemaVersion = 1
        classification = "hosted-candidate"
        package = $packageName
        packageSha256 = $packageHash
        xmlReport = [System.IO.Path]::GetFileName($reportPath)
        htmlReport = [System.IO.Path]::GetFileName($htmlReportPath)
        analyzerExitCode = $analysisExitCode
        analysisAttempted = $analysisAttempted
        completedAtUtc = [DateTime]::UtcNow.ToString("O")
    }
    [IO.File]::WriteAllText(
        $resultPath,
        ($result | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    Write-Host "Hosted runner 上の WACK と Required failure 判定が完了しました。" -ForegroundColor Green
} catch {
    $reportExists = $reportGenerated -or (Test-Path -LiteralPath $reportPath -PathType Leaf)
    $failureClassification = if ($analysisAttempted -and $reportExists) {
        "package-failure"
    } else {
        "wack-execution-failed"
    }
    $failure = [ordered]@{
        schemaVersion = 1
        classification = $failureClassification
        package = $packageName
        packageSha256 = $packageHash
        wackStage = $wackStage
        reportGenerated = $reportExists
        analysisAttempted = $analysisAttempted
        analyzerExitCode = $analysisExitCode
        error = $_.Exception.Message
        failedAtUtc = [DateTime]::UtcNow.ToString("O")
    }
    [IO.File]::WriteAllText(
        $resultPath,
        ($failure | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    throw
} finally {
    Restore-Environment
}
