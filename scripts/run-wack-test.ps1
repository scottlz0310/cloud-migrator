<#
.SYNOPSIS
    MSIX / AppX パッケージに対して Windows App Certification Kit (WACK) を実行する。

.DESCRIPTION
    WACK の実行に必要な環境を scripts/wack_work/ 以下へ隔離し、TAEF/AppCertKit の
    ログがユーザープロファイルへ混在することを防ぐ。WACK の XML レポートを指定先へ
    保存し、AppCertKit の作業領域から HTML/HTM レポートを回収する。

    実行後は AnalyzeWackReport.ps1 で XML を解析し、Required failure と Optional failure
    を表示する。WACK の終了コードだけでは合否を判定しない。

    PowerShell 7 (pwsh) のアクティブなユーザーセッションで実行する。非管理者で
    起動した場合は UAC で pwsh を昇格し、同じ引数を引き継いで再実行する。

.PARAMETER PackagePath
    検査対象の .msix / .msixbundle / .appx / .appxbundle のパス。
    省略時は installer/msix/AppPackages 以下の最終更新時刻が新しい対象を自動選択する。
    検証対象を固定する場合は明示的に指定する。

.PARAMETER ReportDirectory
    XML/HTML レポートの出力先。リポジトリルートからの相対パスを指定できる。

.PARAMETER AppCertPath
    appcert.exe のパス。省略時は WACK_APPCERT_PATH、Windows SDK の既定パス、PATH の順に検索する。

.EXAMPLE
    .\scripts\run-wack-test.ps1 -PackagePath .\installer\msix\AppPackages\CloudMigrator_0.7.1.0_x64.msix
#>
[CmdletBinding()]
param(
    [string]$PackagePath = "",
    [string]$ReportDirectory = "scripts/wack_reports",
    [string]$AppCertPath = ""
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

function Resolve-RepositoryPath {
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

    $resolvedPath = Resolve-RepositoryPath $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "$Description が見つかりません: $resolvedPath"
    }

    return (Resolve-Path -LiteralPath $resolvedPath).Path
}

function Resolve-WackPackage {
    param(
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolvedPackagePath = Resolve-ExistingFile $RequestedPath "検査対象パッケージ"
    } else {
        $packageDirectory = Resolve-RepositoryPath "installer/msix/AppPackages"
        if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
            throw "MSIX 出力ディレクトリが見つかりません: $packageDirectory`n先に .\installer\msix\Build-MsixPackage.ps1 を実行してください。"
        }

        $packageCandidates = @(
            Get-ChildItem -LiteralPath $packageDirectory -File -Recurse |
                Where-Object { @(".msix", ".msixbundle", ".appx", ".appxbundle") -contains $_.Extension.ToLowerInvariant() }
        )
        if ($packageCandidates.Count -eq 0) {
            throw "検査対象の MSIX/AppX が見つかりません: $packageDirectory`n先に .\installer\msix\Build-MsixPackage.ps1 を実行してください。"
        }

        $selectedPackage = $packageCandidates |
            Sort-Object `
                -Property @(
                    @{ Expression = "LastWriteTime"; Descending = $true },
                    @{ Expression = "FullName"; Descending = $false }
                ) |
            Select-Object -First 1
        $resolvedPackagePath = $selectedPackage.FullName
        Write-Host "検査対象を自動選択しました: $resolvedPackagePath" -ForegroundColor Cyan
    }

    $packageExtension = [System.IO.Path]::GetExtension($resolvedPackagePath).ToLowerInvariant()
    if (@(".msix", ".msixbundle", ".appx", ".appxbundle") -notcontains $packageExtension) {
        throw "WACK の検査対象には .msix/.msixbundle/.appx/.appxbundle を指定してください: $resolvedPackagePath"
    }

    return $resolvedPackagePath
}

function Resolve-AppCert {
    param(
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return Resolve-ExistingFile $RequestedPath "appcert.exe"
    }

    $environmentPath = [Environment]::GetEnvironmentVariable(
        "WACK_APPCERT_PATH",
        [EnvironmentVariableTarget]::Process
    )
    if (-not [string]::IsNullOrWhiteSpace($environmentPath)) {
        return Resolve-ExistingFile $environmentPath "WACK_APPCERT_PATH で指定された appcert.exe"
    }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\App Certification Kit\appcert.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles "Windows Kits\10\App Certification Kit\appcert.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command appcert.exe -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        return (Resolve-Path -LiteralPath $command.Source).Path
    }

    throw "appcert.exe が見つかりません。Windows SDK の Windows App Certification Kit をインストールするか、-AppCertPath で指定してください。"
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

    throw "UAC 昇格に使用する pwsh.exe が見つかりません。PowerShell 7 で実行してください。"
}

function Quote-ProcessArgument {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-ElevatedWack {
    $pwshPath = Get-PwshExecutable
    $argumentList = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        (Quote-ProcessArgument $PSCommandPath),
        "-ReportDirectory",
        (Quote-ProcessArgument $ReportDirectory)
    )

    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
        $argumentList += @(
            "-PackagePath",
            (Quote-ProcessArgument $PackagePath)
        )
    }
    $appCertPathToForward = $AppCertPath
    if ([string]::IsNullOrWhiteSpace($appCertPathToForward)) {
        $appCertPathToForward = [Environment]::GetEnvironmentVariable(
            "WACK_APPCERT_PATH",
            [EnvironmentVariableTarget]::Process
        )
    }
    if (-not [string]::IsNullOrWhiteSpace($appCertPathToForward)) {
        $argumentList += @(
            "-AppCertPath",
            (Quote-ProcessArgument $appCertPathToForward)
        )
    }

    try {
        $elevatedProcess = Start-Process `
            -FilePath $pwshPath `
            -ArgumentList ($argumentList -join " ") `
            -WorkingDirectory $repositoryRoot `
            -Verb RunAs `
            -Wait `
            -PassThru
    } catch {
        throw "WACK の管理者昇格に失敗しました。UAC で許可したうえで再実行してください。`n$($_.Exception.Message)"
    }

    if ($null -eq $elevatedProcess) {
        throw "WACK の管理者プロセスを起動できませんでした。UAC の許可状態を確認してください。"
    }

    return $elevatedProcess.ExitCode
}

function Set-WackEnvironment {
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

if ($env:OS -ne "Windows_NT") {
    throw "WACK は Windows 上でのみ実行できます。"
}

if (-not [Environment]::UserInteractive) {
    throw "WACK はアクティブなユーザーセッションで実行してください。サービスや Session 0 からは実行できません。"
}

$PackagePath = Resolve-WackPackage $PackagePath

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "WACK は管理者権限が必要です。UAC の許可を求めて昇格します..." -ForegroundColor Yellow
    $elevationStartedAt = Get-Date
    $elevatedExitCode = Invoke-ElevatedWack
    Write-Host "昇格した WACK プロセスが終了しました: exit=$elevatedExitCode" -ForegroundColor $(if ($elevatedExitCode -eq 0) { "Green" } else { "Red" })
    $elevatedReportDirectory = Resolve-RepositoryPath $ReportDirectory
    if (Test-Path -LiteralPath $elevatedReportDirectory -PathType Container) {
        $elevatedReports = @(
            Get-ChildItem -LiteralPath $elevatedReportDirectory -File |
                Where-Object {
                    $_.LastWriteTime -ge $elevationStartedAt -and
                    @(".xml", ".htm", ".html") -contains $_.Extension.ToLowerInvariant()
                } |
                Sort-Object LastWriteTime
        )
        foreach ($report in $elevatedReports) {
            Write-Host "レポート: $($report.FullName)" -ForegroundColor Yellow
        }
    }
    exit $elevatedExitCode
}

$packageFullPath = $PackagePath

$appCert = Resolve-AppCert $AppCertPath
$reportDirectoryFullPath = Resolve-RepositoryPath $ReportDirectory
if (Test-Path -LiteralPath $reportDirectoryFullPath -PathType Leaf) {
    throw "レポート出力先がファイルです: $reportDirectoryFullPath"
}
New-Item -ItemType Directory -Force -Path $reportDirectoryFullPath | Out-Null

$packageName = [System.IO.Path]::GetFileNameWithoutExtension($packageFullPath)
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmssfff")
$reportPath = Join-Path $reportDirectoryFullPath "$packageName-$timestamp.xml"
$reportBaseName = [System.IO.Path]::GetFileNameWithoutExtension($reportPath)
$packageHash = (Get-FileHash -LiteralPath $packageFullPath -Algorithm SHA256).Hash

$wackWorkDirectory = Resolve-RepositoryPath "scripts/wack_work"
$wackProfile = Join-Path $wackWorkDirectory "profile"
$wackLocalAppData = Join-Path $wackProfile "AppData\Local"
$wackRoamingAppData = Join-Path $wackProfile "AppData\Roaming"
$wackTemp = Join-Path $wackLocalAppData "Temp"
$wackLogs = Join-Path $wackWorkDirectory "logs"
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

$startedAt = Get-Date
try {
    Write-Host "WACK の実行環境を隔離しています: $wackWorkDirectory" -ForegroundColor Gray
    Set-WackEnvironment -Values $wackEnvironment -Directories @(
        $wackTemp,
        $wackLogs,
        $wackLocalAppData,
        $wackRoamingAppData,
        $appVerifierLogs
    )

    Write-Host "WACK を初期化しています: $appCert" -ForegroundColor Cyan
    $resetOutput = @(& $appCert reset 2>&1)
    $resetExitCode = $LASTEXITCODE
    if ($resetExitCode -ne 0) {
        throw "WACK の reset に失敗しました（exit=$resetExitCode）。`n$($resetOutput -join [Environment]::NewLine)"
    }

    Write-Host "WACK を実行しています: $packageFullPath" -ForegroundColor Cyan
    Write-Host "パッケージ SHA-256: $packageHash" -ForegroundColor Gray
    $testOutput = @(
        & $appCert test `
            -appxpackagepath $packageFullPath `
            -reportoutputpath $reportPath `
            2>&1
    )
    $testExitCode = $LASTEXITCODE

    if ($testExitCode -eq 1) {
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "WACK が終了コード 1 を返し、最終化対象の XML レポートも生成されませんでした。`n$($testOutput -join [Environment]::NewLine)"
        }

        Write-Host "WACK がレポートの最終化を要求しました。最終化しています..." -ForegroundColor Yellow
        $finalizeOutput = @(& $appCert finalizereport -reportfilepath $reportPath 2>&1)
        $finalizeExitCode = $LASTEXITCODE
        if ($finalizeExitCode -ne 0) {
            throw "WACK レポートの最終化に失敗しました（exit=$finalizeExitCode）。`n$($finalizeOutput -join [Environment]::NewLine)"
        }
    } elseif ($testExitCode -ne 0) {
        throw "WACK の実行に失敗しました（exit=$testExitCode）。`n$($testOutput -join [Environment]::NewLine)"
    }

    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "WACK の終了コードは $testExitCode でしたが、XML レポートが生成されませんでした。`n出力先: $reportPath`nWACK 出力:`n$($testOutput -join [Environment]::NewLine)"
    }

    $htmlCandidates = @()
    if (Test-Path -LiteralPath $wackAppCertDirectory -PathType Container) {
        $htmlCandidates = @(
            Get-ChildItem -LiteralPath $wackAppCertDirectory -File |
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
        throw "WACK の XML は生成されましたが、HTML/HTM レポートを AppCertKit の作業領域で確認できませんでした。`n検索先: $wackAppCertDirectory`n管理者権限、アクティブなユーザーセッション、WACK のインストール状態を確認してください。"
    }

    $htmlReportPath = Join-Path $reportDirectoryFullPath "$reportBaseName$($htmlSource.Extension)"
    Copy-Item -LiteralPath $htmlSource.FullName -Destination $htmlReportPath -Force

    $analyzerPath = Join-Path $PSScriptRoot "AnalyzeWackReport.ps1"
    $pwshPath = Get-PwshExecutable

    Write-Host "WACK XML を解析しています..." -ForegroundColor Cyan
    $analysisOutput = @(
        & $pwshPath -NoProfile -ExecutionPolicy Bypass -File $analyzerPath `
            -ReportPath $reportPath `
            -FailOnRequiredFailure `
            2>&1
    )
    $analysisExitCode = $LASTEXITCODE
    $analysisOutput | ForEach-Object { Write-Host $_ }
    if ($analysisExitCode -ne 0) {
        throw "WACK XML の判定で Required failure またはレポート異常が検出されました（exit=$analysisExitCode）。"
    }

    Write-Host "WACK の実行とレポート生成が完了しました。" -ForegroundColor Green
    Write-Host "XML:  $reportPath" -ForegroundColor Yellow
    Write-Host "HTML: $htmlReportPath" -ForegroundColor Yellow
    Write-Host "WACK 作業ログ: $wackWorkDirectory" -ForegroundColor Gray
    Write-Host "合否の最終記録は docs/wack-validation-checklist.md に残してください。" -ForegroundColor Yellow
} finally {
    Restore-Environment
}
