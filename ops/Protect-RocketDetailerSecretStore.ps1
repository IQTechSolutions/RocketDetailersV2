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
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier[]] $TrustedOwnerSids
    )

    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "Secret path '$Path' still inherits Windows ACL entries."
    }

    $ownerSid = $acl.GetOwner(
        [System.Security.Principal.SecurityIdentifier])
    if ($ownerSid.Value -notin @($TrustedOwnerSids | ForEach-Object Value)) {
        throw "Secret path '$Path' has an untrusted owner '$($ownerSid.Value)'."
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

function Set-RestrictedDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.DirectoryInfo] $Directory,

        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier[]] $FullControlSids,

        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier] $ServiceSid,

        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier] $OwnerSid
    )

    $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $acl = Get-Acl -LiteralPath $Directory.FullName
    $acl.SetOwner($OwnerSid)
    $acl.SetAccessRuleProtection($true, $false)
    $rules = $acl.GetAccessRules(
        $true,
        $false,
        [System.Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        $acl.RemoveAccessRuleSpecific($rule)
    }
    foreach ($identity in $FullControlSids) {
        [void] $acl.AddAccessRule((New-AllowRule `
            -Identity $identity `
            -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl) `
            -Inheritance $inheritance))
    }
    if ($ServiceSid.Value -notin @($FullControlSids | ForEach-Object Value)) {
        [void] $acl.AddAccessRule((New-AllowRule `
            -Identity $ServiceSid `
            -Rights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) `
            -Inheritance $inheritance))
    }
    $Directory.SetAccessControl($acl)
}

function Set-RestrictedFileAcl {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $File,

        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier[]] $FullControlSids,

        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier] $ServiceSid,

        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier] $OwnerSid
    )

    $acl = Get-Acl -LiteralPath $File.FullName
    $acl.SetOwner($OwnerSid)
    $acl.SetAccessRuleProtection($true, $false)
    $rules = $acl.GetAccessRules(
        $true,
        $false,
        [System.Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        $acl.RemoveAccessRuleSpecific($rule)
    }
    foreach ($identity in $FullControlSids) {
        [void] $acl.AddAccessRule((New-AllowRule `
            -Identity $identity `
            -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)))
    }
    if ($ServiceSid.Value -notin @($FullControlSids | ForEach-Object Value)) {
        [void] $acl.AddAccessRule((New-AllowRule `
            -Identity $ServiceSid `
            -Rights ([System.Security.AccessControl.FileSystemRights]::Read)))
    }
    $File.SetAccessControl($acl)
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

$directoryPrefix = $resolvedDirectory.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Resolve-StoreChildPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $childPath = [IO.Path]::GetFullPath($Path)
    if (-not $childPath.StartsWith(
            $directoryPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Secret-store child resolves outside the expected directory: $childPath"
    }
    return $childPath
}

# Complete a no-mutation preflight before hardening anything. This prevents an
# existing junction or symbolic link from escaping the intended secret store.
$preflightDirectories = [Collections.Generic.Stack[string]]::new()
$preflightDirectories.Push($resolvedDirectory)
while ($preflightDirectories.Count -gt 0) {
    $preflightPath = $preflightDirectories.Pop()
    $preflightDirectory = Get-Item -LiteralPath $preflightPath -Force
    if (-not $preflightDirectory.PSIsContainer -or
        ($preflightDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to traverse unsafe secret-store directory: $preflightPath"
    }

    foreach ($child in @(Get-ChildItem -LiteralPath $preflightPath -Force)) {
        $childPath = Resolve-StoreChildPath -Path $child.FullName
        $freshChild = Get-Item -LiteralPath $childPath -Force
        if ($freshChild.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to secure reparse point in the secret store: $childPath"
        }
        if ($freshChild.PSIsContainer) {
            $preflightDirectories.Push($childPath)
        }
    }
}

$currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$administratorsSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$serviceSid = Resolve-IdentitySid -Identity $ServiceIdentity
$fullControlSids = [System.Security.Principal.SecurityIdentifier[]] @(
    @($currentSid, $systemSid, $administratorsSid) |
        Sort-Object -Property Value -Unique)
$trustedOwnerSids = $fullControlSids

# Harden top-down from fresh filesystem state. Each parent is protected before
# its children are enumerated, and each child directory is protected before it
# is queued for traversal. Reparse points are rechecked immediately before use.
$pendingDirectories = [Collections.Generic.Stack[string]]::new()
$pendingDirectories.Push($resolvedDirectory)
while ($pendingDirectories.Count -gt 0) {
    $directoryPath = $pendingDirectories.Pop()
    $directory = Get-Item -LiteralPath $directoryPath -Force
    if (-not $directory.PSIsContainer -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Secret-store directory became unsafe during hardening: $directoryPath"
    }
    Set-RestrictedDirectoryAcl `
        -Directory ([IO.DirectoryInfo] $directory) `
        -FullControlSids $fullControlSids `
        -ServiceSid $serviceSid `
        -OwnerSid $currentSid

    foreach ($child in @(Get-ChildItem -LiteralPath $directoryPath -Force)) {
        $childPath = Resolve-StoreChildPath -Path $child.FullName
        $freshChild = Get-Item -LiteralPath $childPath -Force
        if ($freshChild.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Secret-store child became a reparse point during hardening: $childPath"
        }

        if ($freshChild.PSIsContainer) {
            Set-RestrictedDirectoryAcl `
                -Directory ([IO.DirectoryInfo] $freshChild) `
                -FullControlSids $fullControlSids `
                -ServiceSid $serviceSid `
                -OwnerSid $currentSid
            $pendingDirectories.Push($childPath)
        } else {
            Set-RestrictedFileAcl `
                -File ([IO.FileInfo] $freshChild) `
                -FullControlSids $fullControlSids `
                -ServiceSid $serviceSid `
                -OwnerSid $currentSid
        }
    }
}

# Fresh final traversal proves that nothing unprocessed or unsafe appeared while
# ACLs were changing. A newly created item would still inherit its ACL and fail
# the protected-ACL assertion, keeping deployment fail-closed.
$finalDirectories = [Collections.Generic.Stack[string]]::new()
$finalDirectories.Push($resolvedDirectory)
$directoryCount = 0
$fileCount = 0
while ($finalDirectories.Count -gt 0) {
    $directoryPath = $finalDirectories.Pop()
    $directory = Get-Item -LiteralPath $directoryPath -Force
    if (-not $directory.PSIsContainer -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Secret-store directory became unsafe during verification: $directoryPath"
    }
    Assert-NoBroadAccess `
        -Path $directoryPath `
        -TrustedOwnerSids $trustedOwnerSids
    $directoryCount++

    foreach ($child in @(Get-ChildItem -LiteralPath $directoryPath -Force)) {
        $childPath = Resolve-StoreChildPath -Path $child.FullName
        $freshChild = Get-Item -LiteralPath $childPath -Force
        if ($freshChild.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Secret-store child became a reparse point during verification: $childPath"
        }
        Assert-NoBroadAccess `
            -Path $childPath `
            -TrustedOwnerSids $trustedOwnerSids
        if ($freshChild.PSIsContainer) {
            $finalDirectories.Push($childPath)
        } else {
            $fileCount++
        }
    }
}

Write-Host (
    "Rocket Detailer secret store protected ({0} existing files across {1} directories)." -f
        $fileCount,
        $directoryCount)
