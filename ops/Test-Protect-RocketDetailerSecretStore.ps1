$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$protector = Join-Path $PSScriptRoot 'Protect-RocketDetailerSecretStore.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'rocket-detailer-secret-acl-' + [Guid]::NewGuid().ToString('N'))
$secretPath = Join-Path $tempRoot 'dotnet.application.environment.json'
$originalContent = '{"contract":"preserve"}'

function New-AllowRule {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier] $Identity,

        [Parameter(Mandatory = $true)]
        [System.Security.AccessControl.FileSystemRights] $Rights,

        [System.Security.AccessControl.InheritanceFlags] $Inheritance =
            [System.Security.AccessControl.InheritanceFlags]::None
    )

    return New-Object System.Security.AccessControl.FileSystemAccessRule(
        $Identity,
        $Rights,
        $Inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
}

function Assert-RestrictedAcl {
    param([Parameter(Mandatory = $true)][string] $Path)

    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "ACL inheritance remains enabled for '$Path'."
    }

    $broadSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545', 'S-1-5-32-546')
    $rules = $acl.GetAccessRules(
        $true,
        $true,
        [System.Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq
                [System.Security.AccessControl.AccessControlType]::Allow -and
            $rule.IdentityReference.Value -in $broadSids) {
            throw "Broad principal '$($rule.IdentityReference.Value)' retained access to '$Path'."
        }
    }
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    [IO.File]::WriteAllText(
        $secretPath,
        $originalContent,
        [Text.UTF8Encoding]::new($false))

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $everyoneSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-1-0')
    $directoryAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $directoryAcl.SetAccessRuleProtection($true, $false)
    [void] $directoryAcl.AddAccessRule((New-AllowRule `
        -Identity $currentSid `
        -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl) `
        -Inheritance ([System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit')))
    [void] $directoryAcl.AddAccessRule((New-AllowRule `
        -Identity $everyoneSid `
        -Rights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
        -Inheritance ([System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit')))
    Set-Acl -LiteralPath $tempRoot -AclObject $directoryAcl

    $fileAcl = New-Object System.Security.AccessControl.FileSecurity
    $fileAcl.SetAccessRuleProtection($true, $false)
    [void] $fileAcl.AddAccessRule((New-AllowRule `
        -Identity $currentSid `
        -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)))
    [void] $fileAcl.AddAccessRule((New-AllowRule `
        -Identity $everyoneSid `
        -Rights ([System.Security.AccessControl.FileSystemRights]::Read)))
    Set-Acl -LiteralPath $secretPath -AclObject $fileAcl

    & $protector -DirectoryPath $tempRoot -ServiceIdentity $currentSid.Value

    Assert-RestrictedAcl -Path $tempRoot
    Assert-RestrictedAcl -Path $secretPath
    if ([IO.File]::ReadAllText($secretPath) -ne $originalContent) {
        throw 'Protecting the secret store changed secret-file contents.'
    }

    Write-Host 'Rocket Detailer secret-store ACL contract passed.'
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
