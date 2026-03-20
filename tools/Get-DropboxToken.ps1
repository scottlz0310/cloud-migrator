#Requires -Version 5.1
<#
.SYNOPSIS
    Dropbox OAuth2 リフレッシュトークン取得ウィザード

.DESCRIPTION
    Dropbox App Console で作成したアプリの App Key / App Secret を入力するだけで、
    ブラウザ認証 → 認可コード取得 → トークン交換 → .env への書き込みを一括で行います。

.EXAMPLE
    .\tools\Get-DropboxToken.ps1
    .\tools\Get-DropboxToken.ps1 -EnvPath .env
#>
param(
    # 書き込み先の .env ファイルパス（省略時はリポジトリルートの .env）
    [string]$EnvPath = (Join-Path (Split-Path $PSScriptRoot) ".env")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---- ヘルパー ----
function Write-Step([string]$msg) {
    Write-Host "`n$msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg) {
    Write-Host "  OK  $msg" -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host "  !   $msg" -ForegroundColor Yellow
}

function Read-Secret([string]$prompt) {
    $ss = Read-Host $prompt -AsSecureString
    [System.Net.NetworkCredential]::new("", $ss).Password
}

# ---- Step 1: App Key / Secret 入力 ----
Write-Host ""
Write-Host "=== Dropbox リフレッシュトークン取得ウィザード ===" -ForegroundColor White
Write-Host "Dropbox App Console (https://www.dropbox.com/developers/apps) で"
Write-Host "作成済みのアプリの情報を入力してください。"

Write-Step "Step 1/3: App Key と App Secret を入力"
$appKey    = (Read-Host "  App Key    (MIGRATOR__DROPBOX__CLIENTID)").Trim()
$appSecret = Read-Secret "  App Secret (MIGRATOR__DROPBOX__CLIENTSECRET)"

if ([string]::IsNullOrWhiteSpace($appKey) -or [string]::IsNullOrWhiteSpace($appSecret)) {
    Write-Error "App Key と App Secret は必須です。"
    exit 1
}

# ---- Step 2: ブラウザで認証 ----
Write-Step "Step 2/3: ブラウザで Dropbox を認証する"

$authUrl = "https://www.dropbox.com/oauth2/authorize" +
           "?client_id=$([Uri]::EscapeDataString($appKey))" +
           "&response_type=code" +
           "&token_access_type=offline"

Write-Host ""
Write-Host "  以下の URL をブラウザで開いて「許可」をクリックしてください:"
Write-Host "  $authUrl" -ForegroundColor DarkYellow
Write-Host ""

$openBrowser = Read-Host "  今すぐブラウザで開きますか？ [Y/n]"
if ($openBrowser -notmatch "^[nN]") {
    Start-Process $authUrl
}

Write-Host ""
Write-Warn "許可後、リダイレクト先 URL の ?code=XXXX... の値をコピーしてください。"
Write-Warn "（リダイレクト URI 未設定の場合はエラーページになりますが、URL バーに code= が残ります）"
Write-Host ""

$authCode = (Read-Host "  code を貼り付けてください").Trim()

if ([string]::IsNullOrWhiteSpace($authCode)) {
    Write-Error "認可コードが空です。"
    exit 1
}

# ---- Step 3: トークン交換 ----
Write-Step "Step 3/3: 認可コードをトークンに交換中..."

try {
    $body = @{
        code          = $authCode
        grant_type    = "authorization_code"
        client_id     = $appKey
        client_secret = $appSecret
    }
    $resp = Invoke-RestMethod -Method Post `
        -Uri "https://api.dropboxapi.com/oauth2/token" `
        -Body $body
} catch {
    Write-Error "トークン交換に失敗しました: $_"
    exit 1
}

$accessToken  = $resp.access_token
$refreshToken = $resp.refresh_token

if ([string]::IsNullOrWhiteSpace($refreshToken)) {
    Write-Error "refresh_token が取得できませんでした。認可 URL に token_access_type=offline が含まれているか確認してください。"
    exit 1
}

Write-Ok "access_token  取得完了（有効期限 約4時間）"
Write-Ok "refresh_token 取得完了（無期限）"

# ---- .env 書き込み ----
Write-Host ""
Write-Host "取得したトークンを $EnvPath に書き込みます..." -ForegroundColor White

# 既存の .env を読み込んで該当行を上書き、なければ追記
$lines = @()
if (Test-Path $EnvPath) {
    $lines = Get-Content $EnvPath
}

function Set-EnvLine([string[]]$lines, [string]$key, [string]$value) {
    $found = $false
    $result = foreach ($line in $lines) {
        if ($line -match "^#?\s*$([regex]::Escape($key))\s*=") {
            "$key=$value"
            $found = $true
        } else {
            $line
        }
    }
    if (-not $found) {
        $result = @($result) + "$key=$value"
    }
    return $result
}

$lines = Set-EnvLine $lines "MIGRATOR__DROPBOX__ACCESSTOKEN"  $accessToken
$lines = Set-EnvLine $lines "MIGRATOR__DROPBOX__REFRESHTOKEN" $refreshToken
$lines = Set-EnvLine $lines "MIGRATOR__DROPBOX__CLIENTID"     $appKey
$lines = Set-EnvLine $lines "MIGRATOR__DROPBOX__CLIENTSECRET" $appSecret

# BOM なし UTF-8 で .env を書き込み（PowerShell 5.1 の Set-Content -Encoding UTF8 は BOM 付きのため非使用）
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($EnvPath, $lines, $utf8NoBom)

Write-Ok "$EnvPath を更新しました。"
Write-Host ""
Write-Host "次のステップ:" -ForegroundColor White
Write-Host "  dotnet run --project src/CloudMigrator.Setup.Cli -- doctor"
Write-Host "  dotnet run --project src/CloudMigrator.Setup.Cli -- verify --skip-sharepoint"
Write-Host "  dotnet run --project src/CloudMigrator.Cli -- transfer"
Write-Host ""
