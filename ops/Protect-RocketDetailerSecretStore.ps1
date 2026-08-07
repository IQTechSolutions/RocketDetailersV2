[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $DirectoryPath,

    [Parameter(Mandatory = $true)]
    [string] $ServiceIdentity
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-IdentitySid {
    param([Parameter(Mandatory = $true)][string] $Identity)

    if ($Identity -match '^S-1-[0-9-]+$') {
        return [System.Security.Principal.SecurityIdentifier]::new($Identity)
    }

    $normalized = switch -Regex ($Identity) {
        '^LocalSystem$' { 'NT AUTHORITY\SYSTEM'; break }
        '^(?:NT AUTHORITY\\)?LocalService$' { 'NT AUTHORITY\LOCAL SERVICE'; break }
        '^(?:NT AUTHORITY\\)?NetworkService$' { 'NT AUTHORITY\NETWORK SERVICE'; break }
        '^\.\\(.+)$' { "$env:COMPUTERNAME\$($Matches[1])"; break }
        default { $Identity }
    }

    try {
        return [System.Security.Principal.NTAccount]::new($normalized).Translate(
            [System.Security.Principal.SecurityIdentifier])
    } catch {
        throw "Cannot resolve Windows service identity '$Identity'."
    }
}

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

function Assert-NoBroadAccess {
    param([Parameter(Mandatory = $true)][string] $Path)

    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "Secret path '$Path' still inherits Windows ACL entries."
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
            throw "Secret path '$Path' grants access to broad principal '$($rule.IdentityReference.Value)'."
        }
    }
}

$resolvedDirectory = [IO.Path]::GetFullPath($DirectoryPath)
$parent = Split-Path -Parent $resolvedDirectory
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw "Secret-store parent directory is missing: $parent"
}
if (-not (Test-Path -LiteralPath $resolvedDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $resolvedDirectory | Out-Null
}

$directoryItem = Get-Item -LiteralPath $resolvedDirectory -Force
if ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw "Refusing to secure a reparse-point secret directory: $resolvedDirectory"
}
$childDirectories = @(Get-ChildItem -LiteralPath $resolvedDirectory -Directory -Force)
if ($childDirectories.Count) {
    throw "Secret directory contains unexpected child directories: $resolvedDirectory"
}

$currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$administratorsSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$serviceSid = Resolve-IdentitySid -Identity $ServiceIdentity
$fullControlSids = @($currentSid, $systemSid, $administratorsSid) |
    Sort-Object -Property Value -Unique
$inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'

$directoryAcl = Get-Acl -LiteralPath $resolvedDirectory
$directoryAcl.SetAccessRuleProtection($true, $false)
$directoryRules = $directoryAcl.GetAccessRules(
    $true,
    $false,
    [System.Security.Principal.SecurityIdentifier])
foreach ($rule in $directoryRules) {
    $directoryAcl.RemoveAccessRuleSpecific($rule)
}
foreach ($identity in $fullControlSids) {
    [void] $directoryAcl.AddAccessRule((New-AllowRule `
        -Identity $identity `
        -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl) `
        -Inheritance $inheritance))
}
if ($serviceSid.Value -notin @($fullControlSids | ForEach-Object Value)) {
    [void] $directoryAcl.AddAccessRule((New-AllowRule `
        -Identity $serviceSid `
        -Rights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
        -Inheritance $inheritance))
}
$directoryItem.SetAccessControl($directoryAcl)

$files = @(Get-ChildItem -LiteralPath $resolvedDirectory -File -Force)
foreach ($file in $files) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to secure reparse-point secret file: $($file.FullName)"
    }

    $fileAcl = Get-Acl -LiteralPath $file.FullName
    $fileAcl.SetAccessRuleProtection($true, $false)
    $fileRules = $fileAcl.GetAccessRules(
        $true,
        $false,
        [System.Security.Principal.SecurityIdentifier])
    foreach ($rule in $fileRules) {
        $fileAcl.RemoveAccessRuleSpecific($rule)
    }
    foreach ($identity in $fullControlSids) {
        [void] $fileAcl.AddAccessRule((New-AllowRule `
            -Identity $identity `
            -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)))
    }
    if ($serviceSid.Value -notin @($fullControlSids | ForEach-Object Value)) {
        [void] $fileAcl.AddAccessRule((New-AllowRule `
            -Identity $serviceSid `
            -Rights ([System.Security.AccessControl.FileSystemRights]::Read)))
    }
    $file.SetAccessControl($fileAcl)
    Assert-NoBroadAccess -Path $file.FullName
}

Assert-NoBroadAccess -Path $resolvedDirectory
Write-Host "Rocket Detailer secret store protected ($($files.Count) existing files)."
