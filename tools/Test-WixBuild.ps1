<#
.SYNOPSIS
    MSI インストーラービルドをローカルで検証するスクリプト。
    CI の release.yml "msi" ジョブと同等の手順を実行する。

.DESCRIPTION
    1. CLI / Dashboard を win-x64 向けにパブリッシュ
    2. DepsDir（*.exe / *.pdb を除いた依存ファイル専用ディレクトリ）を作成
    3. wix build で MSI を生成

    事前条件:
      - dotnet tool install wix --version 5.0.2 --global
      - .NET 10 SDK インストール済み

.PARAMETER Version
    MSI に埋め込むバージョン文字列（既定: 0.0.1-test）

.PARAMETER SkipPublish
    publish 済みバイナリが publish/win-x64 に存在する場合、パブリッシュをスキップする

.EXAMPLE
    # 初回検証
    .\tools\Test-WixBuild.ps1

    # バイナリ再利用（2 回目以降）
    .\tools\Test-WixBuild.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$Version     = "0.0.1-test",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root    = (Split-Path $PSScriptRoot)
$binDir  = "$root\publish\win-x64"
$depsDir = "$root\publish\win-x64-deps"
$outMsi  = "$root\publish\cloud-migrator-test.msi"

# ─── 前提チェック ────────────────────────────────────────────────────────────
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Error "wix コマンドが見つかりません。以下でインストールしてください:`n  dotnet tool install wix --version 5.0.2 --global"
}

# ─── 0. WiX 拡張のグローバル登録（既登録なら no-op） ─────────────────────────
# UI    : ようこそ/ライセンス/インストール先/進捗/完了 ダイアログ
# Util  : ExitDialog 起動用 WixShellExec カスタムアクション + InternetShortcut
# 注意: バージョン未指定だと最新（v7 系）が取得されて WiX 5 と不整合になる。
#       wix 本体のバージョンに合わせて 5.0.2 にピン留めする。
$wixExtVersion = "5.0.2"
Write-Host "▶ WiX 拡張を確認/登録中（v$wixExtVersion）..." -ForegroundColor Cyan
foreach ($ext in @("WixToolset.UI.wixext", "WixToolset.Util.wixext")) {
    & wix extension add -g "$ext/$wixExtVersion" 2>&1 | Out-Null
}

# ─── 1. パブリッシュ ─────────────────────────────────────────────────────────
if (-not $SkipPublish) {
    Write-Host "▶ CLI をパブリッシュ中..." -ForegroundColor Cyan
    dotnet publish "$root\src\CloudMigrator.Cli\CloudMigrator.Cli.csproj" `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        --output "$binDir"

    Write-Host "▶ Dashboard をパブリッシュ中..." -ForegroundColor Cyan
    dotnet publish "$root\src\CloudMigrator.Dashboard\CloudMigrator.Dashboard.csproj" `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        -p:Version=$Version `
        --output "$binDir"
} else {
    Write-Host "▶ パブリッシュをスキップ（SkipPublish 指定）" -ForegroundColor Yellow
    if (-not (Test-Path $binDir)) {
        Write-Error "publish\win-x64 が存在しません。-SkipPublish なしで実行してください。"
    }
}

# ─── 2. DepsDir 作成（*.exe / *.pdb を除外）────────────────────────────────
Write-Host "▶ DepsDir を作成中..." -ForegroundColor Cyan
if (Test-Path $depsDir) { Remove-Item $depsDir -Recurse -Force }
Copy-Item -Path $binDir -Destination $depsDir -Recurse -Force
Get-ChildItem $depsDir -Recurse -Filter "*.exe"   | Remove-Item -Force
Get-ChildItem $depsDir -Recurse -Filter "*.pdb"   | Remove-Item -Force

# ─── 3. wix build ────────────────────────────────────────────────────────────
Write-Host "▶ MSI をビルド中..." -ForegroundColor Cyan
wix build "$root\installer\wix\Product.wxs" `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -d Version=$Version `
    -d "BinDir=$binDir" `
    -d "DepsDir=$depsDir" `
    -o "$outMsi"

Write-Host ""
Write-Host "✓ 完了: $outMsi" -ForegroundColor Green
