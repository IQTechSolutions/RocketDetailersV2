$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$writer = Join-Path $PSScriptRoot 'Write-RocketDetailerEnvironment.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("rocket-detailer-config-" + [Guid]::NewGuid().ToString('N'))
$configPath = Join-Path $tempRoot 'dotnet.application.environment.json'

$providerConfig = [ordered]@{
    Stripe__ApiKey = 'rk_live_contract_test_only'
    Meta__AccessToken = 'meta-contract-test-token'
    Meta__AdAccountId = 'act_123456789012345'
    Meta__BaseUrl = 'https://graph.facebook.com/v25.0'
    Ghl__Locations__0__LocationId = 'location-contract-000'
    Ghl__Locations__0__Token = 'pit-00000000-0000-4000-8000-000000000000'
    Ghl__Locations__0__Name = 'Rocket Detailer'
    Ghl__Locations__1__LocationId = 'location-contract-001'
    Ghl__Locations__1__Token = 'pit-00000000-0000-4000-8000-000000000001'
    Ghl__Locations__1__Name = 'Detail Launch'
    Ghl__Locations__2__LocationId = 'location-contract-002'
    Ghl__Locations__2__Token = 'pit-00000000-0000-4000-8000-000000000002'
    Ghl__Locations__2__Name = 'Automations'
    Slack__IncomingWebhookUrl = 'https://hooks.slack.com/services/T00000000/B00000000/contracttest'
}
$providerJson = $providerConfig | ConvertTo-Json -Compress
$connectionString = 'Server=(local);Database=RocketDetailerContract;Integrated Security=true'
$existingConfig = [ordered]@{
    ConnectionStrings__RocketDetailers = 'Server=old;Database=Old'
    Stripe__ApiKey = 'rk_live_old_contract_test_only'
    Stripe__BaseUrl = 'https://stripe.example.invalid'
    Stripe__Webhook__SigningSecret = 'whsec_preserve_contract_test_only'
    Slack__SigningSecret = 'slack-signing-secret-preserve-contract-test-only'
    Safety__TestContactId = 'contact-preserve-contract-test-only'
    Safety__TestContactLocationId = 'location-preserve-contract-test-only'
    Slack__UserMap__0__SlackUserId = 'U-CONTRACT-TEST'
    Slack__UserMap__0__Email = 'operator@example.invalid'
    Ghl__BaseUrl = 'https://ghl.example.invalid'
    Ghl__Locations__3__LocationId = 'stale-location-contract-003'
    Ghl__Locations__3__Token = 'pit-00000000-0000-4000-8000-000000000003'
    Ghl__Locations__3__Name = 'Stale location'
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $directoryAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $directoryAcl.SetAccessRuleProtection($true, $false)
    $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $identities = @(
        [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')) |
        Select-Object -Unique
    foreach ($identity in $identities) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $identity,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            $propagation,
            $allow)
        [void] $directoryAcl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $tempRoot -AclObject $directoryAcl

    [IO.File]::WriteAllText(
        $configPath,
        ($existingConfig | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))

    # A protected ACL differs from the inherited ACL on the temporary writer
    # file, so this detects a delete-and-move implementation that weakens it.
    $protectedAcl = Get-Acl -LiteralPath $configPath
    $protectedAcl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $configPath -AclObject $protectedAcl
    $aclBefore = (Get-Acl -LiteralPath $configPath).Sddl

    & $writer `
        -ConfigPath $configPath `
        -ConnectionString $connectionString `
        -ApplicationEnvironmentJson $providerJson

    $written = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    foreach ($entry in $providerConfig.GetEnumerator()) {
        if ([string] $written.($entry.Key) -ne [string] $entry.Value) {
            throw "Provider setting '$($entry.Key)' was not preserved."
        }
    }
    if ($written.ConnectionStrings__RocketDetailers -ne $connectionString) {
        throw 'The database connection string was not written.'
    }
    if ($written.Stripe__BaseUrl -ne 'https://api.stripe.com' -or
        $written.Ghl__BaseUrl -ne 'https://services.leadconnectorhq.com') {
        throw 'Credential-bearing gateways were not pinned to their official endpoints.'
    }
    foreach ($preservedKey in @(
        'Stripe__Webhook__SigningSecret',
        'Slack__SigningSecret',
        'Safety__TestContactId',
        'Safety__TestContactLocationId',
        'Slack__UserMap__0__SlackUserId',
        'Slack__UserMap__0__Email')) {
        if ([string] $written.$preservedKey -ne [string] $existingConfig[$preservedKey]) {
            throw "Existing setting '$preservedKey' was not preserved."
        }
    }
    if ((Get-Acl -LiteralPath $configPath).Sddl -ne $aclBefore) {
        throw 'Replacing the configuration changed its protected Windows ACL.'
    }
    foreach ($staleKey in @(
        'Ghl__Locations__3__LocationId',
        'Ghl__Locations__3__Token',
        'Ghl__Locations__3__Name')) {
        if ($written.PSObject.Properties.Name -contains $staleKey) {
            throw "Managed stale setting '$staleKey' survived credential rotation."
        }
    }

    $backupPath = "$configPath.previous"
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        throw 'Replacing an existing configuration did not create a rollback backup.'
    }
    if ((Get-Acl -LiteralPath $backupPath).Sddl -ne $aclBefore) {
        throw 'The rollback backup did not retain the protected Windows ACL.'
    }
    $backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
    foreach ($entry in $existingConfig.GetEnumerator()) {
        if ([string] $backup.($entry.Key) -ne [string] $entry.Value) {
            throw "Rollback backup setting '$($entry.Key)' was not preserved."
        }
    }

    $beforeRejectedWrite = [IO.File]::ReadAllBytes($configPath)
    $invalid = $providerJson | ConvertFrom-Json
    $invalid.PSObject.Properties.Remove('Slack__IncomingWebhookUrl')
    $rejected = $false
    try {
        & $writer `
            -ConfigPath $configPath `
            -ConnectionString $connectionString `
            -ApplicationEnvironmentJson ($invalid | ConvertTo-Json -Compress)
    } catch {
        $rejected = $_.Exception.Message -like "*missing required key 'Slack__IncomingWebhookUrl'*"
    }
    if (-not $rejected) {
        throw 'A provider bundle missing a required credential was not rejected.'
    }
    $afterRejectedWrite = [IO.File]::ReadAllBytes($configPath)
    if ([Convert]::ToBase64String($afterRejectedWrite) -ne
        [Convert]::ToBase64String($beforeRejectedWrite)) {
        throw 'A rejected provider bundle modified the existing configuration.'
    }
    if (Test-Path -LiteralPath "$configPath.new") {
        throw 'A temporary credential file remained after a rejected write.'
    }

    $whitespacePath = Join-Path $tempRoot 'whitespace.json'
    $whitespaceConfig = $providerJson | ConvertFrom-Json
    $whitespaceConfig.Stripe__ApiKey = "rk_live_contract_test_only`n"
    $whitespaceRejected = $false
    try {
        & $writer `
            -ConfigPath $whitespacePath `
            -ConnectionString $connectionString `
            -ApplicationEnvironmentJson ($whitespaceConfig | ConvertTo-Json -Compress)
    } catch {
        $whitespaceRejected = $_.Exception.Message -like '*must not contain leading or trailing whitespace*'
    }
    if (-not $whitespaceRejected -or (Test-Path -LiteralPath $whitespacePath)) {
        throw 'A credential with trailing whitespace was not rejected before writing configuration.'
    }

    $backupBeforeRejectedWrite = [IO.File]::ReadAllBytes($backupPath)
    $pendingBackupRejected = $false
    try {
        & $writer `
            -ConfigPath $configPath `
            -ConnectionString $connectionString `
            -ApplicationEnvironmentJson $providerJson
    } catch {
        $pendingBackupRejected = $_.Exception.Message -like '*pending configuration backup*'
    }
    if (-not $pendingBackupRejected) {
        throw 'A second write was not blocked while rollback evidence existed.'
    }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -ne
        [Convert]::ToBase64String($beforeRejectedWrite) -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($backupPath)) -ne
        [Convert]::ToBase64String($backupBeforeRejectedWrite)) {
        throw 'A blocked second write modified the live file or rollback evidence.'
    }

    $freshPath = Join-Path $tempRoot 'fresh.json'
    & $writer `
        -ConfigPath $freshPath `
        -ConnectionString $connectionString `
        -ApplicationEnvironmentJson $providerJson
    if (-not (Test-Path -LiteralPath $freshPath -PathType Leaf) -or
        (Test-Path -LiteralPath "$freshPath.previous")) {
        throw 'First-time configuration creation did not follow the expected contract.'
    }

    $unsafeDirectory = Join-Path $tempRoot 'unsafe'
    New-Item -ItemType Directory -Path $unsafeDirectory | Out-Null
    $unsafeAcl = Get-Acl -LiteralPath $unsafeDirectory
    $unsafeAcl.SetAccessRuleProtection($true, $true)
    $everyoneRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        [System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'),
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $inheritance,
        $propagation,
        $allow)
    [void] $unsafeAcl.AddAccessRule($everyoneRule)
    Set-Acl -LiteralPath $unsafeDirectory -AclObject $unsafeAcl
    $unsafeRejected = $false
    try {
        & $writer `
            -ConfigPath (Join-Path $unsafeDirectory 'unsafe.json') `
            -ConnectionString $connectionString `
            -ApplicationEnvironmentJson $providerJson
    } catch {
        $unsafeRejected = $_.Exception.Message -like '*grants access to broad principal*'
    }
    if (-not $unsafeRejected) {
        throw 'A broadly readable secret directory was not rejected.'
    }

    $workflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github\workflows\ci-cd.yml'
    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    foreach ($requiredWorkflowText in @(
        'Write-RocketDetailerEnvironment.ps1',
        'Protect-RocketDetailerSecretStore.ps1',
        'Protect live secret store',
        'LIVE_APPLICATION_ENVIRONMENT_JSON',
        'Prepare configuration transaction',
        'Prepare release transaction',
        'deploy-transaction.json',
        'RD_RELEASE_TXN_ARMED',
        'RD_CONFIG_TXN_ARMED',
        'RD_CONFIG_UPDATED',
        'RD_CONFIG_BACKUP_PATH',
        'RD_DEPLOY_VERIFIED',
        'cancelled()',
        'timeout-minutes: 8',
        'Get-FileHash',
        'Roll back failed release and configuration')) {
        if ($workflow -notlike "*$requiredWorkflowText*") {
            throw "Deployment workflow is missing credential-rotation wiring '$requiredWorkflowText'."
        }
    }
    if ($workflow -notmatch
        '(?s)& \$configWriter.*-ApplicationEnvironmentJson \$env:LIVE_APPLICATION_ENVIRONMENT_JSON') {
        throw 'Deployment workflow does not pass the provider bundle to the validated writer.'
    }
    if ($workflow -match
        '(?s)\$configJson\s*=\s*@\{\s*ConnectionStrings__RocketDetailers') {
        throw 'Deployment workflow has regressed to a connection-string-only inline writer.'
    }

    Write-Host 'Rocket Detailer external-configuration contract passed.'
} finally {
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected test path: $resolvedTemp"
    }
    if (Test-Path -LiteralPath $resolvedTemp) {
        [IO.Directory]::Delete($resolvedTemp, $true)
    }
}
