[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ConfigPath,

    [Parameter(Mandatory = $true)]
    [string] $ConnectionString,

    [Parameter(Mandatory = $true)]
    [string] $ApplicationEnvironmentJson
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-RestrictedSecretAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [switch] $RequireProtected
    )

    try {
        $acl = Get-Acl -LiteralPath $Path
    } catch {
        throw "Cannot verify the Windows ACL for secret path '$Path'."
    }

    if ($RequireProtected -and -not $acl.AreAccessRulesProtected) {
        throw "Secret directory '$Path' must be protected from inherited ACLs."
    }

    $broadSids = @(
        'S-1-1-0',       # Everyone
        'S-1-5-11',      # Authenticated Users
        'S-1-5-32-545',  # BUILTIN\Users
        'S-1-5-32-546'   # BUILTIN\Guests
    )
    $rules = $acl.GetAccessRules(
        $true,
        $true,
        [System.Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq
                [System.Security.AccessControl.AccessControlType]::Allow -and
            $rule.IdentityReference.Value -in $broadSids) {
            throw "Secret path '$Path' grants access to broad principal '$($rule.IdentityReference.Value)'."
        }
    }
}

$connectionString = $ConnectionString.TrimStart([char] 0xFEFF)
if ($connectionString -notmatch '^(Data Source|Server)\s*=') {
    throw 'LIVE_CONNECTION_STRING does not begin with a supported SQL Server key.'
}

try {
    $providerConfig = $ApplicationEnvironmentJson.TrimStart([char] 0xFEFF) |
        ConvertFrom-Json
} catch {
    throw 'LIVE_APPLICATION_ENVIRONMENT_JSON is not valid JSON.'
}

if ($null -eq $providerConfig -or $providerConfig -is [Array] -or
    $providerConfig -is [string] -or @($providerConfig.PSObject.Properties).Count -eq 0) {
    throw 'LIVE_APPLICATION_ENVIRONMENT_JSON must be a non-empty JSON object.'
}

$configDirectory = Split-Path -Parent $ConfigPath
if ([string]::IsNullOrWhiteSpace($configDirectory) -or
    -not (Test-Path -LiteralPath $configDirectory -PathType Container)) {
    throw 'The secret directory must be provisioned with restricted Windows ACLs before deployment.'
}
Assert-RestrictedSecretAcl -Path $configDirectory -RequireProtected

# This file can also contain server-local safety and canary settings. Preserve
# every existing setting, then overlay the complete credential bundle and the
# separately managed database connection string.
$config = [ordered]@{}
if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
    Assert-RestrictedSecretAcl -Path $ConfigPath
    try {
        $existingConfig = Get-Content -LiteralPath $ConfigPath -Raw |
            ConvertFrom-Json
    } catch {
        throw 'The existing application environment file is not valid JSON; it was not changed.'
    }

    if ($null -eq $existingConfig -or $existingConfig -is [Array] -or
        $existingConfig -is [string]) {
        throw 'The existing application environment file must contain a JSON object; it was not changed.'
    }

    # Credential destinations are managed too: a stale endpoint override must
    # never receive a newly rotated live token. Stripe and GHL use their
    # checked-in official defaults; Meta's versioned endpoint is supplied and
    # validated by the complete bundle below.
    $managedKey = '^(?:Stripe__(?:ApiKey|BaseUrl)|Meta__(?:AccessToken|AdAccountId|BaseUrl)|Ghl__(?:BaseUrl|Locations__[0-9]+__(?:LocationId|Token|Name))|Slack__IncomingWebhookUrl)\z'
    foreach ($property in $existingConfig.PSObject.Properties) {
        if ($property.Name -notmatch $managedKey) {
            $config[$property.Name] = $property.Value
        }
    }
}

$allowedKey = '^(?:Stripe__(?:ApiKey|Webhook__SigningSecret)|Meta__(?:AccessToken|AdAccountId|BaseUrl)|Ghl__Locations__[0-2]__(?:LocationId|Token|Name)|Slack__(?:IncomingWebhookUrl|SigningSecret))\z'
$bundleKeys = @{}

foreach ($property in $providerConfig.PSObject.Properties) {
    if ($property.Name -eq 'ConnectionStrings__RocketDetailers') {
        throw 'LIVE_APPLICATION_ENVIRONMENT_JSON must not contain the database connection string.'
    }
    if ($property.Name -notmatch $allowedKey) {
        throw "LIVE_APPLICATION_ENVIRONMENT_JSON contains unsupported key '$($property.Name)'."
    }
    if ($property.Value -isnot [string] -or [string]::IsNullOrWhiteSpace([string] $property.Value)) {
        throw "LIVE_APPLICATION_ENVIRONMENT_JSON key '$($property.Name)' must be a non-empty string."
    }
    $value = ([string] $property.Value).TrimStart([char] 0xFEFF)
    if ($value -ne $value.Trim()) {
        throw "LIVE_APPLICATION_ENVIRONMENT_JSON key '$($property.Name)' must not contain leading or trailing whitespace."
    }
    $bundleKeys[$property.Name] = $true
    $config[$property.Name] = $value
}

$requiredKeys = @(
    'Stripe__ApiKey',
    'Meta__AccessToken',
    'Meta__AdAccountId',
    'Meta__BaseUrl',
    'Ghl__Locations__0__LocationId',
    'Ghl__Locations__0__Token',
    'Ghl__Locations__1__LocationId',
    'Ghl__Locations__1__Token',
    'Ghl__Locations__2__LocationId',
    'Ghl__Locations__2__Token',
    'Slack__IncomingWebhookUrl'
)
foreach ($key in $requiredKeys) {
    if (-not $bundleKeys.ContainsKey($key)) {
        throw "LIVE_APPLICATION_ENVIRONMENT_JSON is missing required key '$key'."
    }
}

# Force credential-bearing gateways onto their official endpoints. Writing
# these values explicitly also overrides any inherited machine-level
# environment variable when the launcher imports this file.
$config['Stripe__BaseUrl'] = 'https://api.stripe.com'
$config['Ghl__BaseUrl'] = 'https://services.leadconnectorhq.com'

if ($config['Stripe__ApiKey'] -notmatch '^rk_live_[A-Za-z0-9_]+\z') {
    throw 'Stripe__ApiKey must be a live restricted Stripe key.'
}
if ($config['Meta__AdAccountId'] -notmatch '^(?:act_)?[0-9]+\z') {
    throw 'Meta__AdAccountId must be a numeric Meta ad-account identifier.'
}
if ($config['Meta__BaseUrl'] -notmatch '^https://graph\.facebook\.com/v[0-9]+\.[0-9]+\z') {
    throw 'Meta__BaseUrl must pin an HTTPS Meta Graph API version.'
}
if ($config['Slack__IncomingWebhookUrl'] -notmatch '^https://hooks\.slack\.com/services/[A-Z0-9]+/[A-Z0-9]+/[A-Za-z0-9]+\z') {
    throw 'Slack__IncomingWebhookUrl is not a valid Slack incoming-webhook URL.'
}
if (-not $config.Contains('Stripe__Webhook__SigningSecret') -or
    [string]::IsNullOrWhiteSpace([string] $config['Stripe__Webhook__SigningSecret'])) {
    Write-Warning 'Stripe API access will work, but inbound Stripe webhooks remain disabled until a signing secret is configured.'
}
if (-not $config.Contains('Slack__SigningSecret') -or
    [string]::IsNullOrWhiteSpace([string] $config['Slack__SigningSecret'])) {
    Write-Warning 'Slack notifications can be sent, but interactive callbacks remain disabled until a signing secret is configured.'
}

$locationIds = @()
foreach ($index in 0..2) {
    $locationKey = "Ghl__Locations__${index}__LocationId"
    $tokenKey = "Ghl__Locations__${index}__Token"
    if ($config[$locationKey] -notmatch '^[A-Za-z0-9_-]{3,128}\z') {
        throw "$locationKey is not a valid HighLevel location identifier."
    }
    if ($config[$tokenKey] -notmatch '^pit-[A-Za-z0-9-]{16,}\z') {
        throw "$tokenKey is not a valid HighLevel private integration token."
    }
    $locationIds += $config[$locationKey]
}
if (($locationIds | Select-Object -Unique).Count -ne $locationIds.Count) {
    throw 'HighLevel location identifiers must be unique.'
}

$config['ConnectionStrings__RocketDetailers'] = $connectionString
$configTemp = "$ConfigPath.new"
$configBackup = "$ConfigPath.previous"
$configJson = $config | ConvertTo-Json
$configExists = Test-Path -LiteralPath $ConfigPath -PathType Leaf
if (Test-Path -LiteralPath $configBackup) {
    throw "A pending configuration backup requires recovery before another write: $configBackup"
}
try {
    if (Test-Path -LiteralPath $configTemp) {
        Remove-Item -LiteralPath $configTemp -Force
    }

    [IO.File]::WriteAllText(
        $configTemp,
        $configJson,
        [Text.UTF8Encoding]::new($false))
    Assert-RestrictedSecretAcl -Path $configTemp

    if ($configExists) {
        [IO.File]::Replace($configTemp, $ConfigPath, $configBackup, $true)
        Assert-RestrictedSecretAcl -Path $configBackup
    } else {
        [IO.File]::Move($configTemp, $ConfigPath)
    }
    Assert-RestrictedSecretAcl -Path $ConfigPath
} finally {
    if (Test-Path -LiteralPath $configTemp) {
        Remove-Item -LiteralPath $configTemp -Force -ErrorAction SilentlyContinue
    }
}

$credentialSettingCount = @($providerConfig.PSObject.Properties).Count
Write-Host "External application configuration written with $credentialSettingCount supplied credential settings."
