$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$workflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github\workflows\ci-cd.yml'
$workflowLines = Get-Content -LiteralPath $workflowPath
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'rocket-detailer-deploy-transaction-' + [Guid]::NewGuid().ToString('N'))

function Get-WorkflowStepScript {
    param([Parameter(Mandatory = $true)][string] $StepName)

    $nameLine = "      - name: $StepName"
    $nameIndex = -1
    for ($index = 0; $index -lt $workflowLines.Count; $index++) {
        if ($workflowLines[$index] -eq $nameLine) {
            $nameIndex = $index
            break
        }
    }
    if ($nameIndex -lt 0) {
        throw "Workflow step '$StepName' was not found."
    }

    $runIndex = -1
    for ($index = $nameIndex + 1; $index -lt $workflowLines.Count; $index++) {
        if ($workflowLines[$index] -eq '        run: |') {
            $runIndex = $index
            break
        }
        if ($workflowLines[$index] -match '^      - name: ') {
            break
        }
    }
    if ($runIndex -lt 0) {
        throw "Workflow step '$StepName' has no PowerShell run block."
    }

    $scriptLines = @()
    for ($index = $runIndex + 1; $index -lt $workflowLines.Count; $index++) {
        if ($workflowLines[$index] -match '^          (.*)$') {
            $scriptLines += $Matches[1]
        } elseif ([string]::IsNullOrWhiteSpace($workflowLines[$index])) {
            $scriptLines += ''
        } else {
            break
        }
    }
    return ($scriptLines -join [Environment]::NewLine)
}

function Expand-WorkflowScript {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][string] $Root
    )

    return $Text.
        Replace('${{ vars.APP_ROOT }}', $Root).
        Replace('${{ vars.WINDOWS_SERVICE }}', 'contract-service')
}

function Set-CurrentLink {
    param(
        [Parameter(Mandatory = $true)][string] $Current,
        [Parameter(Mandatory = $true)][string] $Target
    )

    $existing = Get-Item -LiteralPath $Current -Force -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        if (-not ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Test current path is unexpectedly not a link: $Current"
        }
        [IO.Directory]::Delete($existing.FullName)
    }
    New-Item -ItemType Junction -Path $Current -Target $Target | Out-Null
}

function Assert-CurrentTarget {
    param(
        [Parameter(Mandatory = $true)][string] $Current,
        [Parameter(Mandatory = $true)][string] $Expected
    )

    $item = Get-Item -LiteralPath $Current -Force -ErrorAction Stop
    $actual = [IO.Path]::GetFullPath([string] $item.Target)
    $expectedFull = [IO.Path]::GetFullPath($Expected)
    if (-not $actual.Equals($expectedFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Expected current target '$expectedFull', found '$actual'."
    }
}

$script:startServiceShouldFail = $false
$script:startCount = 0
$script:stopCount = 0

function Stop-Service {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [switch] $Force
    )
    $script:stopCount++
}

function Start-Service {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Name)
    $script:startCount++
    if ($script:startServiceShouldFail) {
        Write-Error 'Injected service-start failure.'
    }
}

try {
    $oldRelease = Join-Path $tempRoot 'releases\old'
    $newRelease = Join-Path $tempRoot 'releases\new'
    $current = Join-Path $tempRoot 'current'
    $journal = Join-Path $tempRoot 'deploy-transaction.json'
    $githubEnvironment = Join-Path $tempRoot 'github.env'
    New-Item -ItemType Directory -Path $oldRelease -Force | Out-Null
    New-Item -ItemType Directory -Path $newRelease -Force | Out-Null

    $switchScript = [scriptblock]::Create((Expand-WorkflowScript `
        -Text (Get-WorkflowStepScript -StepName 'Switch release and restart service') `
        -Root $tempRoot))
    $rollbackScript = [scriptblock]::Create((Expand-WorkflowScript `
        -Text (Get-WorkflowStepScript -StepName 'Roll back failed release and configuration') `
        -Root $tempRoot))

    $env:RELEASE_PATH = $newRelease
    $env:PREVIOUS_RELEASE = $oldRelease
    $env:RD_CURRENT_EXISTED = 'true'
    $env:RD_RELEASE_TXN_ARMED = 'true'
    $env:RD_CONFIG_TXN_ARMED = 'false'
    $env:RD_RELEASE_JOURNAL_PATH = $journal
    $env:RD_CONFIG_HAD_PREVIOUS = 'false'
    $env:RD_CONFIG_PATH = Join-Path $tempRoot 'missing-config.json'
    $env:RD_CONFIG_BACKUP_PATH = "$($env:RD_CONFIG_PATH).previous"
    $env:GITHUB_ENV = $githubEnvironment

    Set-CurrentLink -Current $current -Target $oldRelease
    [IO.File]::WriteAllText($journal, '{}')
    & $switchScript
    Assert-CurrentTarget -Current $current -Expected $newRelease
    if ((Get-Content -LiteralPath $githubEnvironment -Raw) -notmatch 'SWITCHED=true') {
        throw 'The release switch did not persist its mutation marker.'
    }

    & $rollbackScript
    Assert-CurrentTarget -Current $current -Expected $oldRelease
    if (Test-Path -LiteralPath $journal) {
        throw 'A successful rollback did not remove the release journal.'
    }

    $configPath = Join-Path $tempRoot 'application-environment.json'
    $configBackup = "$configPath.previous"
    $failedConfigBackup = "$configPath.failed"
    [IO.File]::WriteAllText($configPath, '{"version":"new"}')
    [IO.File]::WriteAllText($configBackup, '{"version":"old"}')
    [IO.File]::WriteAllText($journal, '{}')
    $env:RD_CONFIG_PATH = $configPath
    $env:RD_CONFIG_BACKUP_PATH = $configBackup
    $env:RD_CONFIG_HAD_PREVIOUS = 'true'
    $env:RD_CONFIG_TXN_ARMED = 'true'
    $env:RD_RELEASE_TXN_ARMED = 'false'
    $startCountBeforeConfigRollback = $script:startCount

    & $rollbackScript
    if ([IO.File]::ReadAllText($configPath) -ne '{"version":"old"}') {
        throw 'Configuration rollback did not restore the previous file contents.'
    }
    if (Test-Path -LiteralPath $configBackup) {
        throw 'Successful configuration rollback retained its consumed backup.'
    }
    if (Test-Path -LiteralPath $failedConfigBackup) {
        throw 'Successful configuration rollback retained its temporary failed-config backup.'
    }
    if ($script:startCount -ne ($startCountBeforeConfigRollback + 1)) {
        throw 'Successful configuration rollback did not restart the prior service release.'
    }
    if (Test-Path -LiteralPath $journal) {
        throw 'Successful configuration rollback did not remove the release journal.'
    }

    $env:RD_CONFIG_TXN_ARMED = 'false'
    $env:RD_RELEASE_TXN_ARMED = 'true'
    $env:RD_CONFIG_HAD_PREVIOUS = 'false'
    $env:RD_CONFIG_PATH = Join-Path $tempRoot 'missing-config.json'
    $env:RD_CONFIG_BACKUP_PATH = "$($env:RD_CONFIG_PATH).previous"

    Set-CurrentLink -Current $current -Target $newRelease
    $env:PREVIOUS_RELEASE = Join-Path $tempRoot 'releases\missing'
    [IO.File]::WriteAllText($journal, '{}')
    $missingPreviousRejected = $false
    try {
        & $rollbackScript
    } catch {
        $missingPreviousRejected = $_.Exception.Message -like '*previous release path is unavailable*'
    }
    if (-not $missingPreviousRejected) {
        throw 'Rollback did not reject a missing previous release.'
    }
    Assert-CurrentTarget -Current $current -Expected $newRelease
    if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) {
        throw 'Failed rollback did not retain its release journal.'
    }

    Remove-Item -LiteralPath $journal -Force
    $env:PREVIOUS_RELEASE = $oldRelease
    Set-CurrentLink -Current $current -Target $newRelease
    [IO.File]::WriteAllText($journal, '{}')
    $script:startServiceShouldFail = $true
    $rollbackRestartRejected = $false
    try {
        & $rollbackScript
    } catch {
        $rollbackRestartRejected = $_.Exception.Message -like '*Injected service-start failure*'
    } finally {
        $script:startServiceShouldFail = $false
    }
    if (-not $rollbackRestartRejected) {
        throw 'An injected rollback restart failure was not reported.'
    }
    Assert-CurrentTarget -Current $current -Expected $oldRelease
    if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) {
        throw 'A failed rollback restart deleted its recovery journal.'
    }

    Remove-Item -LiteralPath $journal -Force
    $env:PREVIOUS_RELEASE = $oldRelease
    Set-CurrentLink -Current $current -Target $oldRelease
    [IO.File]::WriteAllText($journal, '{}')
    $script:startServiceShouldFail = $true
    $startFailureRejected = $false
    try {
        & $switchScript
    } catch {
        $startFailureRejected = $_.Exception.Message -like '*Injected service-start failure*'
    } finally {
        $script:startServiceShouldFail = $false
    }
    if (-not $startFailureRejected) {
        throw 'An injected service-start failure did not fail the switch step.'
    }
    Assert-CurrentTarget -Current $current -Expected $oldRelease
    if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) {
        throw 'An interrupted switch lost its persistent release journal.'
    }

    Write-Host 'Rocket Detailer release-transaction contract passed.'
} finally {
    foreach ($name in @(
        'RELEASE_PATH',
        'PREVIOUS_RELEASE',
        'RD_CURRENT_EXISTED',
        'RD_RELEASE_TXN_ARMED',
        'RD_CONFIG_TXN_ARMED',
        'RD_RELEASE_JOURNAL_PATH',
        'RD_CONFIG_HAD_PREVIOUS',
        'RD_CONFIG_PATH',
        'RD_CONFIG_BACKUP_PATH',
        'GITHUB_ENV')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }

    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected test path: $resolvedTemp"
    }
    $currentItem = Get-Item -LiteralPath (Join-Path $resolvedTemp 'current') `
        -Force -ErrorAction SilentlyContinue
    if ($null -ne $currentItem -and
        $currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        [IO.Directory]::Delete($currentItem.FullName)
    }
    if (Test-Path -LiteralPath $resolvedTemp) {
        [IO.Directory]::Delete($resolvedTemp, $true)
    }
}
