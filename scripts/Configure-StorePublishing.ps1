[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string]$EnvFile,
    [string]$Repository,
    [string]$EnvironmentName,
    [string]$ProductId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-DotEnv {
    param([Parameter(Mandatory)][string]$Path)

    $values = @{}
    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
            continue
        }

        if ($line -notmatch '^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*)$') {
            throw ".env の行を解釈できません: $rawLine"
        }

        $name = $Matches['name']
        if ($values.ContainsKey($name)) {
            throw ".env に同じキーが複数あります: $name"
        }

        $value = $Matches['value'].Trim()
        if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        } elseif ($value.StartsWith('"') -or $value.StartsWith("'")) {
            throw ".env の引用符が閉じていません: $name"
        }

        $values[$name] = $value
    }

    return $values
}

function Get-RequiredValue {
    param(
        [Parameter(Mandatory)][hashtable]$Values,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not $Values.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace([string]$Values[$Name])) {
        throw ".env に必須値がありません: $Name"
    }

    return [string]$Values[$Name]
}

function Invoke-Gh {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | ForEach-Object { [string]$_ }) -join ' '
        throw "gh コマンドに失敗しました: gh $($Arguments -join ' ') $detail"
    }

    return $output
}

function Invoke-GhJson {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Json
    )

    $output = @($Json | & gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | ForEach-Object { [string]$_ }) -join ' '
        throw "gh API 呼び出しに失敗しました: $detail"
    }

    return $output
}

if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $EnvFile = Join-Path $PSScriptRoot '..\.env'
}

$envFilePath = (Resolve-Path -LiteralPath $EnvFile -ErrorAction Stop).Path
$values = Read-DotEnv -Path $envFilePath

$secretNames = @(
    'AZURE_AD_APPLICATION_CLIENT_ID',
    'AZURE_AD_APPLICATION_SECRET',
    'AZURE_AD_TENANT_ID',
    'SELLER_ID'
)

foreach ($secretName in $secretNames) {
    [void](Get-RequiredValue -Values $values -Name $secretName)
}

if ([string]::IsNullOrWhiteSpace($Repository)) {
    if ($values.ContainsKey('GITHUB_REPOSITORY') -and -not [string]::IsNullOrWhiteSpace([string]$values['GITHUB_REPOSITORY'])) {
        $Repository = [string]$values['GITHUB_REPOSITORY']
    } else {
        $Repository = [string](Invoke-Gh -Arguments @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner') | Select-Object -First 1)
    }
}

if ($Repository -notmatch '^[^/]+/[^/]+$') {
    throw "Repository は owner/name 形式で指定してください: $Repository"
}

if ([string]::IsNullOrWhiteSpace($EnvironmentName)) {
    if ($values.ContainsKey('GITHUB_ENVIRONMENT') -and -not [string]::IsNullOrWhiteSpace([string]$values['GITHUB_ENVIRONMENT'])) {
        $EnvironmentName = [string]$values['GITHUB_ENVIRONMENT']
    } else {
        $EnvironmentName = 'store-production'
    }
}

if ($EnvironmentName -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') {
    throw "Environment 名に使用できない文字が含まれています: $EnvironmentName"
}

if ([string]::IsNullOrWhiteSpace($ProductId)) {
    if ($values.ContainsKey('STORE_PRODUCT_ID') -and -not [string]::IsNullOrWhiteSpace([string]$values['STORE_PRODUCT_ID'])) {
        $ProductId = [string]$values['STORE_PRODUCT_ID']
    } else {
        $ProductId = '9NG134LB022L'
    }
}

if ($ProductId -notmatch '^[A-Za-z0-9-]+$') {
    throw "Store Product ID が不正です: $ProductId"
}

[void](Invoke-Gh -Arguments @('auth', 'status'))
[void](Invoke-Gh -Arguments @('repo', 'view', $Repository, '--json', 'nameWithOwner', '--jq', '.nameWithOwner'))

$apiHeaders = @('--header', 'Accept: application/vnd.github+json', '--header', 'X-GitHub-Api-Version: 2026-03-10')
$environmentEndpoint = "repos/$Repository/environments/$EnvironmentName"
$environmentBody = [ordered]@{
    deployment_branch_policy = [ordered]@{
        protected_branches = $false
        custom_branch_policies = $true
    }
} | ConvertTo-Json -Depth 5

if ($PSCmdlet.ShouldProcess("GitHub Environment '$EnvironmentName'", '作成または更新')) {
    [void](Invoke-GhJson -Arguments (@('api', $environmentEndpoint, '--method', 'PUT') + $apiHeaders + @('--input', '-')) -Json $environmentBody)
}

$policiesEndpoint = "$environmentEndpoint/deployment-branch-policies"
if ($WhatIfPreference) {
    Write-Host "WhatIf: Environment '$EnvironmentName' の既存 tag policy は確認せず、'v*' の追加予定を表示します。"
} else {
    $existingPolicies = @(Invoke-Gh -Arguments (@('api', $policiesEndpoint) + $apiHeaders + @('--jq', '.branch_policies[] | select(.name == "v*" and .type == "tag") | .name')))
    if ($existingPolicies.Count -eq 0) {
        $policyBody = [ordered]@{ name = 'v*'; type = 'tag' } | ConvertTo-Json -Depth 3
        if ($PSCmdlet.ShouldProcess("GitHub Environment '$EnvironmentName'", "tag policy 'v*' を追加")) {
            [void](Invoke-GhJson -Arguments (@('api', $policiesEndpoint, '--method', 'POST') + $apiHeaders + @('--input', '-')) -Json $policyBody)
        }
    } else {
        Write-Host "tag policy 'v*' は設定済みです。"
    }
}

if ($PSCmdlet.ShouldProcess("GitHub Environment '$EnvironmentName' の variable 'STORE_PRODUCT_ID'", '値を設定')) {
    $output = @($ProductId | & gh variable set STORE_PRODUCT_ID --env $EnvironmentName --repo $Repository 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | ForEach-Object { [string]$_ }) -join ' '
        throw "variable 'STORE_PRODUCT_ID' の設定に失敗しました: $detail"
    }
}

foreach ($secretName in $secretNames) {
    if ($PSCmdlet.ShouldProcess("GitHub Environment '$EnvironmentName' の secret '$secretName'", '値を設定')) {
        $secretValue = [string]$values[$secretName]
        $output = @($secretValue | & gh secret set $secretName --env $EnvironmentName --repo $Repository 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $detail = ($output | ForEach-Object { [string]$_ }) -join ' '
            throw "secret '$secretName' の設定に失敗しました: $detail"
        }
    }
}

if (-not $WhatIfPreference) {
    $configuredProductId = [string](Invoke-Gh -Arguments @('variable', 'list', '--env', $EnvironmentName, '--repo', $Repository, '--json', 'name,value', '--jq', '.[] | select(.name == "STORE_PRODUCT_ID") | .value') | Select-Object -First 1)
    if ($configuredProductId -ne $ProductId) {
        throw "Environment '$EnvironmentName' の STORE_PRODUCT_ID が期待値と一致しません。"
    }

    $configuredSecretNames = @(Invoke-Gh -Arguments @('secret', 'list', '--env', $EnvironmentName, '--repo', $Repository, '--json', 'name', '--jq', '.[].name'))
    $missingSecretNames = @($secretNames | Where-Object { $_ -notin $configuredSecretNames })
    if ($missingSecretNames.Count -gt 0) {
        throw "Environment '$EnvironmentName' に未設定の secret があります: $($missingSecretNames -join ', ')"
    }
    Write-Host "Store 自動公開用の GitHub Environment 設定を確認しました。"
} else {
    Write-Host "WhatIf: GitHub Environment と secret の設定予定を確認しました。外部変更はありません。"
}

Write-Host "Repository: $Repository"
Write-Host "Environment: $EnvironmentName"
Write-Host "Product ID: $ProductId"
$summaryVerb = if ($WhatIfPreference) { '設定予定の' } else { '設定した' }
Write-Host "$summaryVerb Environment variable: STORE_PRODUCT_ID"
Write-Host "$summaryVerb secret: $($secretNames -join ', ')"
