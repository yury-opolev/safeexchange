#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Deploy SafeExchange infrastructure to Azure via the ARM template,
    supplying the operator's public IP as a runtime CLI override
    loaded from a gitignored .env file.

.DESCRIPTION
    Wrapper around `az deployment group create` that:

    1. Reads DEPLOYER_IP_ADDRESSES from deployment/.env (gitignored).
    2. Fetches the operator's current public IP from ipinfo.io.
    3. Compares it against the value in .env. If different, prompts
       the operator to update .env (never the parameters file) with
       the current /32.
    4. Runs the deployment, passing deployer_ip_addresses as a CLI
       parameter override via a temporary JSON file. The committed
       services-parameters-*.arm.json files always have
       deployer_ip_addresses.value = [], so the operator's public IP
       never lands in git.

    The deployer_ip_addresses parameter opens only the two resources
    that the deploy flow from a laptop actually needs: Key Vault (for
    ARM-provisioned `Microsoft.KeyVault/vaults/secrets` resources,
    which ARM writes through the vault's data plane) and WebJobs
    Storage (for `func azure functionapp publish` zip uploads).
    Cosmos DB and Data Storage remain fully private behind their
    VNet-integrated private endpoints regardless of this value.

.PARAMETER Environment
    Which environment to deploy. 'test' maps to
    services-parameters-test.arm.json, 'prd' to
    services-parameters-prd.arm.json.

.PARAMETER ResourceGroup
    Target resource group. Defaults to 'safeexchange-staging' for test
    and 'safeexchange-backend' for prd.

.PARAMETER WhatIf
    Run `az deployment group what-if` instead of actually deploying.
    Shows the resource-level changes that would be made.

.PARAMETER SkipIpCheck
    Skip the public-IP check and deploy with whatever is currently in
    .env (or an empty list if .env is absent). Use when running from
    inside a VNet (jump box, self-hosted runner, VPN with a known
    public egress) where the operator's ipinfo.io-reported IP doesn't
    correspond to what Azure sees.

.PARAMETER EnvFile
    Override the path to the .env file. Defaults to
    deployment/.env alongside this script.

.PARAMETER SkipOrphanCheck
    Skip the orphaned-role-assignment scan. By default the script
    looks for RBAC + Cosmos SQL role assignments in the target
    resource group whose principal no longer exists in AAD (i.e.
    leftovers from a deleted function app or managed identity)
    and prompts to delete them, because Azure refuses to update
    an existing role assignment's principalId on the next deploy.

.EXAMPLE
    ./deployment/deploy.ps1 -Environment test

    Deploys the staging environment. Checks current public IP against
    .env and prompts to update .env if different.

.EXAMPLE
    ./deployment/deploy.ps1 -Environment prd -WhatIf

    Previews what would change in prd without actually deploying.

.EXAMPLE
    ./deployment/deploy.ps1 -Environment prd -SkipIpCheck

    Deploys prd using whatever DEPLOYER_IP_ADDRESSES contains in
    .env (including empty) without fetching current public IP.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('test', 'prd')]
    [string]$Environment,

    [Parameter()]
    [string]$ResourceGroup,

    [Parameter()]
    [switch]$WhatIf,

    [Parameter()]
    [switch]$SkipIpCheck,

    [Parameter()]
    [string]$EnvFile,

    [Parameter()]
    [switch]$SkipOrphanCheck
)

# Disable Git Bash MSYS path translation for az CLI calls — without this,
# the leading '/' on Azure resource IDs gets rewritten as 'C:/Program Files/Git/'
# when `az` is launched from inside a bash shell on Windows.
$env:MSYS_NO_PATHCONV = '1'

$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

# ──────────────────────────────────────────────────────────────────
# Paths
# ──────────────────────────────────────────────────────────────────
$here         = $PSScriptRoot
$armDir       = Join-Path $here 'current/arm'
$templatePath = Join-Path $armDir 'services-template.arm.json'
$paramsPath   = Join-Path $armDir "services-parameters-$Environment.arm.json"
if (-not $EnvFile) {
    $EnvFile = Join-Path $here '.env'
}

if (-not (Test-Path $templatePath)) {
    throw "ARM template not found: $templatePath"
}
if (-not (Test-Path $paramsPath)) {
    throw "Parameters file not found: $paramsPath"
}

# ──────────────────────────────────────────────────────────────────
# Resolve resource group (default per environment)
# ──────────────────────────────────────────────────────────────────
if (-not $ResourceGroup) {
    $ResourceGroup = switch ($Environment) {
        'prd'  { 'safeexchange-backend' }
        'test' { 'safeexchange-staging' }
    }
    Write-Host "Using default resource group for ${Environment}: $ResourceGroup" -ForegroundColor DarkGray
}

# ──────────────────────────────────────────────────────────────────
# Sanity-check az CLI + login state before doing anything destructive
# ──────────────────────────────────────────────────────────────────
try {
    $null = & az --version 2>&1
} catch {
    throw "Azure CLI ('az') is not installed or not on PATH. Install from https://aka.ms/azcli and try again."
}

$accountJson = az account show 2>$null
if (-not $accountJson) {
    throw "Not signed in to Azure. Run 'az login' first."
}
$account = $accountJson | ConvertFrom-Json
Write-Host "Signed in as $($account.user.name), subscription $($account.name) ($($account.id))" -ForegroundColor DarkGray

# ──────────────────────────────────────────────────────────────────
# Read .env — extract DEPLOYER_IP_ADDRESSES
# ──────────────────────────────────────────────────────────────────
# Returns an array of trimmed, non-empty IPs/CIDRs parsed from the
# DEPLOYER_IP_ADDRESSES line in a simple KEY=value .env file.
function Read-DeployerIpsFromEnv {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return @()
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -match '^DEPLOYER_IP_ADDRESSES\s*=\s*(.*)$') {
            $raw = $matches[1].Trim().Trim('"').Trim("'")
            if (-not $raw) { return @() }
            return @($raw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    }
    return @()
}

$deployerIps = Read-DeployerIpsFromEnv -Path $EnvFile
$deployerDisplay = if ($deployerIps.Count -gt 0) { $deployerIps -join ', ' } else { '(empty)' }
Write-Host "deployer_ip_addresses from ${EnvFile}: $deployerDisplay"

# ──────────────────────────────────────────────────────────────────
# Read .env — extract ADMIN_USERS_<ENV>
# ──────────────────────────────────────────────────────────────────
# Returns the raw value (comma-separated oids) or empty string if the
# variable is missing / blank. Passed through verbatim to ARM because
# settings-adminConfiguration-adminUsers is typed as string.
function Read-EnvValue {
    param(
        [string]$Path,
        [string]$Name
    )

    if (-not (Test-Path $Path)) {
        return ''
    }

    $pattern = '^' + [regex]::Escape($Name) + '\s*=\s*(.*)$'
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -match $pattern) {
            return $matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return ''
}

$adminUsersVar = "ADMIN_USERS_$($Environment.ToUpperInvariant())"
$adminUsers    = Read-EnvValue -Path $EnvFile -Name $adminUsersVar
$adminDisplay  = if ($adminUsers) { $adminUsers } else { '(empty — template default will apply)' }
Write-Host "${adminUsersVar} from ${EnvFile}: $adminDisplay"

# ──────────────────────────────────────────────────────────────────
# Public-IP check
# ──────────────────────────────────────────────────────────────────
if ($SkipIpCheck) {
    Write-Host "Skipping public-IP check (-SkipIpCheck)." -ForegroundColor DarkGray
} else {
    Write-Host "Fetching current public IP from ipinfo.io..." -ForegroundColor DarkGray
    try {
        $currentIp = (Invoke-RestMethod -Uri 'https://ipinfo.io/ip' -TimeoutSec 10).Trim()
    } catch {
        throw "Failed to fetch public IP from ipinfo.io: $($_.Exception.Message)"
    }

    if ($currentIp -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$') {
        throw "ipinfo.io returned an unexpected value: '$currentIp'"
    }

    $currentIpCidr = "$currentIp/32"
    Write-Host "Current public IP: $currentIp (allowlist entry: $currentIpCidr)"

    # Match either '1.2.3.4' or '1.2.3.4/32' forms against the .env list.
    # Doesn't do CIDR containment on purpose — the operator either has their
    # exact /32 listed, or they don't.
    $ipMatches = $deployerIps | Where-Object {
        $_ -eq $currentIpCidr -or $_ -eq $currentIp
    }

    if ($ipMatches) {
        Write-Host "Current IP matches .env. Proceeding." -ForegroundColor Green
    } else {
        Write-Host ""
        if ($deployerIps.Count -eq 0 -and -not (Test-Path $EnvFile)) {
            Write-Warning "$EnvFile does not exist."
        } else {
            Write-Warning "Your current public IP ($currentIp) is NOT in DEPLOYER_IP_ADDRESSES."
            Write-Warning "  .env:    $deployerDisplay"
            Write-Warning "  Current: $currentIpCidr"
        }
        Write-Host ""
        Write-Host "If you proceed, $EnvFile will be written:"
        Write-Host "  DEPLOYER_IP_ADDRESSES=$currentIpCidr"
        Write-Host ""
        Write-Host "The deployment will then run with this single /32 as a CLI override."
        Write-Host "The committed services-parameters-$Environment.arm.json is not modified."
        Write-Host ""
        $response = Read-Host 'Update .env and deploy? [y/N]'
        if ([string]::IsNullOrWhiteSpace($response) -or $response.Trim().ToLower() -ne 'y') {
            Write-Host "Cancelled. No files were modified." -ForegroundColor Yellow
            exit 0
        }

        # Rewrite (or create) the DEPLOYER_IP_ADDRESSES line. Preserves
        # any other lines in the .env file.
        $newLine = "DEPLOYER_IP_ADDRESSES=$currentIpCidr"
        if (Test-Path $EnvFile) {
            $lines = @(Get-Content -LiteralPath $EnvFile)
            $replaced = $false
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\s*DEPLOYER_IP_ADDRESSES\s*=') {
                    $lines[$i] = $newLine
                    $replaced = $true
                    break
                }
            }
            if (-not $replaced) {
                $lines += $newLine
            }
            Set-Content -LiteralPath $EnvFile -Value $lines
        } else {
            Set-Content -LiteralPath $EnvFile -Value @($newLine)
        }
        Write-Host "Updated $EnvFile." -ForegroundColor Green
        $deployerIps = @($currentIpCidr)
        $deployerDisplay = $currentIpCidr
    }
}

# ──────────────────────────────────────────────────────────────────
# Orphaned role assignment cleanup
# ──────────────────────────────────────────────────────────────────
# When a function app (or any managed identity) is deleted and recreated,
# its principalId changes. Existing role assignments still reference the
# old principalId, and Azure refuses to update them in place — the next
# ARM deployment fails with "Tenant ID, application ID, principal ID, and
# scope are not allowed to be updated".
#
# The role assignment names in services-template.arm.json are a static
# guid() of (rg, siteName, role) and do NOT include the principalId, so
# the only way to recover is to delete the orphans before redeploying.
# ARM rejects reference() inside resource name expressions, so this can't
# be solved purely in the template.
#
# This function scans every role assignment scoped inside the resource
# group, checks whether each principal still exists in AAD, and prompts
# the operator to delete the ones that don't.

# Returns true if the principal is alive in AAD (as a service principal
# or user). Cached per-call by Find-OrphanedRoleAssignments.
function Test-PrincipalExists {
    param([string]$PrincipalId)

    $found = az ad sp show --id $PrincipalId --query "id" -o tsv 2>$null
    if ($found) { return $true }
    $found = az ad user show --id $PrincipalId --query "id" -o tsv 2>$null
    if ($found) { return $true }
    return $false
}

function Find-OrphanedRoleAssignments {
    param([string]$ResourceGroup)

    $allJson = az role assignment list --all -o json 2>$null
    if (-not $allJson) { return @() }
    $all = $allJson | ConvertFrom-Json

    # Match assignments whose scope is inside this RG. Azure returns the
    # scope with a lowercase 'resourcegroups' segment, but be defensive.
    $inRg = $all | Where-Object {
        $_.scope -like "*/resourceGroups/$ResourceGroup/*" -or
        $_.scope -like "*/resourcegroups/$ResourceGroup/*"
    }

    $aliveCache = @{}
    $orphans = @()
    foreach ($a in $inRg) {
        if (-not $aliveCache.ContainsKey($a.principalId)) {
            $aliveCache[$a.principalId] = Test-PrincipalExists -PrincipalId $a.principalId
        }
        if (-not $aliveCache[$a.principalId]) {
            $orphans += $a
        }
    }
    return $orphans
}

function Find-OrphanedCosmosSqlRoleAssignments {
    param([string]$ResourceGroup)

    $accountsRaw = az cosmosdb list -g $ResourceGroup --query "[].name" -o tsv 2>$null
    if (-not $accountsRaw) { return @() }

    $orphans = @()
    foreach ($accountName in ($accountsRaw -split "`r?`n" | Where-Object { $_ })) {
        $listJson = az cosmosdb sql role assignment list --account-name $accountName -g $ResourceGroup -o json 2>$null
        if (-not $listJson) { continue }
        $list = $listJson | ConvertFrom-Json
        foreach ($a in $list) {
            if (-not (Test-PrincipalExists -PrincipalId $a.principalId)) {
                $orphans += [pscustomobject]@{
                    AccountName = $accountName
                    Name        = $a.name
                    PrincipalId = $a.principalId
                    Scope       = $a.scope
                }
            }
        }
    }
    return $orphans
}

if ($SkipOrphanCheck) {
    Write-Host "Skipping orphaned-role-assignment scan (-SkipOrphanCheck)." -ForegroundColor DarkGray
} else {
    Write-Host "Scanning $ResourceGroup for orphaned role assignments..." -ForegroundColor DarkGray
    $rbacOrphans = @(Find-OrphanedRoleAssignments -ResourceGroup $ResourceGroup)
    $cosmosOrphans = @(Find-OrphanedCosmosSqlRoleAssignments -ResourceGroup $ResourceGroup)

    $totalOrphans = $rbacOrphans.Count + $cosmosOrphans.Count
    if ($totalOrphans -eq 0) {
        Write-Host "  No orphaned role assignments found." -ForegroundColor DarkGray
    } else {
        Write-Host ""
        Write-Warning "Found $totalOrphans orphaned role assignment(s) referencing principals that no longer exist in AAD."
        Write-Host ""
        if ($rbacOrphans.Count -gt 0) {
            Write-Host "RBAC ($($rbacOrphans.Count)):"
            foreach ($o in $rbacOrphans) {
                $scopeLeaf = ($o.scope -split '/')[-1]
                Write-Host "  - $($o.roleDefinitionName) on $scopeLeaf  [principal: $($o.principalId)]"
            }
        }
        if ($cosmosOrphans.Count -gt 0) {
            Write-Host "Cosmos SQL ($($cosmosOrphans.Count)):"
            foreach ($o in $cosmosOrphans) {
                Write-Host "  - $($o.AccountName) / $($o.Name)  [principal: $($o.PrincipalId)]"
            }
        }
        Write-Host ""
        Write-Host "These will block the deployment because Azure refuses to update an"
        Write-Host "existing role assignment's principalId. They typically appear after"
        Write-Host "a function app or managed identity is deleted and recreated."
        Write-Host ""
        $response = Read-Host 'Delete orphaned role assignments and continue? [y/N]'
        if ([string]::IsNullOrWhiteSpace($response) -or $response.Trim().ToLower() -ne 'y') {
            Write-Host "Cancelled. No assignments were deleted." -ForegroundColor Yellow
            exit 0
        }

        foreach ($o in $rbacOrphans) {
            Write-Host "  Deleting RBAC: $($o.roleDefinitionName) on $(($o.scope -split '/')[-1])" -ForegroundColor DarkGray
            az role assignment delete --ids $o.id 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "  Failed to delete $($o.id)"
            }
        }
        foreach ($o in $cosmosOrphans) {
            Write-Host "  Deleting Cosmos SQL: $($o.AccountName)/$($o.Name)" -ForegroundColor DarkGray
            az cosmosdb sql role assignment delete --account-name $o.AccountName -g $ResourceGroup --role-assignment-id $o.Name --yes 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "  Failed to delete $($o.Name)"
            }
        }
        Write-Host "Cleanup complete." -ForegroundColor Green
    }
}

# ──────────────────────────────────────────────────────────────────
# Build the CLI parameter override for deployer_ip_addresses.
# ──────────────────────────────────────────────────────────────────
# Azure CLI accepts `--parameters @file.json` multiple times and merges
# them in order; later entries override earlier ones. Writing the
# override into a temp JSON file avoids PowerShell / az quoting hell
# for array values on Windows.
$overrideObject = @{
    '$schema'      = 'https://schema.management.azure.com/schemas/2015-01-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = @{
        deployer_ip_addresses = @{
            value = @($deployerIps)
        }
    }
}
# Only override settings-adminConfiguration-adminUsers when .env supplies
# a value. When absent we leave the template's placeholder default in
# place — matching the pre-.env-wiring behavior and avoiding silently
# clearing an operator-set Key Vault secret.
if ($adminUsers) {
    $overrideObject.parameters['settings-adminConfiguration-adminUsers'] = @{
        value = $adminUsers
    }
}
$overrideJson = $overrideObject | ConvertTo-Json -Depth 10
$overrideFile = Join-Path ([System.IO.Path]::GetTempPath()) ("safeexchange-deploy-override-" + [System.Guid]::NewGuid().ToString('N') + ".json")
Set-Content -LiteralPath $overrideFile -Value $overrideJson -NoNewline

# ──────────────────────────────────────────────────────────────────
# Run the deployment
# ──────────────────────────────────────────────────────────────────
$operation = if ($WhatIf) { 'what-if' } else { 'create' }

$azArgs = @(
    'deployment', 'group', $operation
    '--resource-group',  $ResourceGroup
    '--template-file',   $templatePath
    '--parameters',      "@$paramsPath"
    '--parameters',      "@$overrideFile"
)
if ($operation -eq 'create') {
    $azArgs += '--mode'
    $azArgs += 'Incremental'
}

try {
    Write-Host ""
    Write-Host "Running: az $($azArgs -join ' ')" -ForegroundColor DarkGray
    Write-Host "  (deployer_ip_addresses override: $deployerDisplay)" -ForegroundColor DarkGray
    Write-Host "  (adminConfiguration-adminUsers override: $adminDisplay)" -ForegroundColor DarkGray
    Write-Host ""
    & az @azArgs

    if ($LASTEXITCODE -ne 0) {
        throw "az deployment failed with exit code $LASTEXITCODE"
    }
} finally {
    Remove-Item -LiteralPath $overrideFile -ErrorAction SilentlyContinue
}

Write-Host ""
if ($WhatIf) {
    Write-Host "What-if complete. No changes were made." -ForegroundColor Green
} else {
    Write-Host "Deployment to $Environment complete." -ForegroundColor Green
}
