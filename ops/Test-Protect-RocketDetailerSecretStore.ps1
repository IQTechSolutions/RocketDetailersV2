$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$protector = Join-Path $PSScriptRoot 'Protect-RocketDetailerSecretStore.ps1'
$tempBase = Join-Path ([IO.Path]::GetTempPath()) (
    'rocket-detailer-secret-acl-' + [Guid]::NewGuid().ToString('N'))
$tempRoot = Join-Path $tempBase 'secrets'
$outsideRoot = Join-Path $tempBase 'outside'
$rootJunction = Join-Path $tempBase 'root-junction'
$junctionPath = Join-Path $tempRoot 'unexpected-link'
$nestedDirectory = Join-Path $tempRoot 'legacy-backups'
$deepDirectory = Join-Path $nestedDirectory '2025'
$emptyDirectory = Join-Path $nestedDirectory 'empty'
$secretPath = Join-Path $tempRoot 'dotnet.application.environment.json'
$nestedSecretPath = Join-Path $nestedDirectory 'application.environment.previous.json'
$binarySecretPath = Join-Path $deepDirectory 'archived-secret.bin'
$outsideSentinelPath = Join-Path $outsideRoot 'sentinel.bin'
$originalContent = '{"contract":"preserve"}'
$nestedOriginalContent = '{"contract":"preserve-nested"}'
$binaryContent = [byte[]] (0, 1, 2, 3, 127, 128, 254, 255)
$outsideContent = [byte[]] (9, 8, 7, 6, 5)

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

function Get-TreeInventory {
    param([Parameter(Mandatory = $true)][string] $Root)

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $entries = [Collections.Generic.List[string]]::new()
    foreach ($item in @(Get-ChildItem -LiteralPath $resolvedRoot -Force -Recurse)) {
        $relativePath = $item.FullName.Substring($resolvedRoot.Length + 1)
        if ($item.PSIsContainer) {
            $entries.Add("D|$relativePath")
        } else {
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            $entries.Add("F|$relativePath|$($item.Length)|$hash|$([int] $item.Attributes)")
        }
    }
    return @($entries | Sort-Object)
}

function Get-TreeSddl {
    param([Parameter(Mandatory = $true)][string] $Root)

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $paths = @($resolvedRoot) + @(
        Get-ChildItem -LiteralPath $resolvedRoot -Force -Recurse |
            ForEach-Object FullName)
    return @($paths | Sort-Object | ForEach-Object {
        $relativePath = if ($_ -eq $resolvedRoot) {
            '.'
        } else {
            $_.Substring($resolvedRoot.Length + 1)
        }
        "$relativePath|$((Get-Acl -LiteralPath $_).Sddl)"
    })
}

function Assert-SequenceEqual {
    param(
        [Parameter(Mandatory = $true)][object[]] $Expected,
        [Parameter(Mandatory = $true)][object[]] $Actual,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (($Expected -join "`n") -cne ($Actual -join "`n")) {
        throw "$Description changed unexpectedly."
    }
}

function Assert-RestrictedAcl {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier] $OwnerSid,
        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier] $ServiceSid,
        [Parameter(Mandatory = $true)][bool] $IsDirectory
    )

    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "ACL inheritance remains enabled for '$Path'."
    }
    $actualOwner = $acl.GetOwner(
        [System.Security.Principal.SecurityIdentifier])
    if ($actualOwner.Value -ne $OwnerSid.Value) {
        throw "Unexpected owner '$($actualOwner.Value)' on '$Path'."
    }

    $systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $allowedSids = @(
        $OwnerSid.Value,
        $systemSid.Value,
        $administratorsSid.Value,
        $ServiceSid.Value) | Sort-Object -Unique
    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [System.Security.Principal.SecurityIdentifier]))
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne
            [System.Security.AccessControl.AccessControlType]::Allow) {
            throw "Unexpected deny ACL retained on '$Path'."
        }
        if ($rule.IdentityReference.Value -notin $allowedSids) {
            throw "Unexpected principal '$($rule.IdentityReference.Value)' retained on '$Path'."
        }
    }

    $serviceRules = @($rules | Where-Object {
        $_.IdentityReference.Value -eq $ServiceSid.Value
    })
    if ($serviceRules.Count -ne 1) {
        throw "Expected one service ACL on '$Path'; found $($serviceRules.Count)."
    }
    $serviceRights = $serviceRules[0].FileSystemRights
    $requiredRights = if ($IsDirectory) {
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute
    } else {
        [System.Security.AccessControl.FileSystemRights]::Read
    }
    if (($serviceRights -band $requiredRights) -ne $requiredRights) {
        throw "Service read rights are missing on '$Path'."
    }
    $forbiddenRights = [System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership
    if (($serviceRights -band $forbiddenRights) -ne 0) {
        throw "Service write or ownership rights were granted on '$Path': $serviceRights."
    }
}

function Assert-ProtectorRejectsReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string] $DirectoryPath,
        [Parameter(Mandatory = $true)][string] $ServiceIdentity
    )

    $rejected = $false
    try {
        & $protector `
            -DirectoryPath $DirectoryPath `
            -ServiceIdentity $ServiceIdentity
    } catch {
        if ($_.Exception.Message -notmatch 'reparse|unsafe') {
            throw
        }
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Protector accepted reparse-point path '$DirectoryPath'."
    }
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $outsideRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $deepDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $emptyDirectory -Force | Out-Null
    [IO.File]::WriteAllText(
        $secretPath,
        $originalContent,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $nestedSecretPath,
        $nestedOriginalContent,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes($binarySecretPath, $binaryContent)
    [IO.File]::SetAttributes($binarySecretPath, [IO.FileAttributes]::Hidden)
    [IO.File]::WriteAllBytes($outsideSentinelPath, $outsideContent)

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $serviceSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-19')
    if ($serviceSid.Value -eq $currentSid.Value) {
        throw 'The ACL contract requires a service SID distinct from the runner SID.'
    }
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
    foreach ($directory in @(
            $tempRoot,
            $nestedDirectory,
            $deepDirectory,
            $emptyDirectory)) {
        Set-Acl -LiteralPath $directory -AclObject $directoryAcl
    }

    $fileAcl = New-Object System.Security.AccessControl.FileSecurity
    $fileAcl.SetAccessRuleProtection($true, $false)
    [void] $fileAcl.AddAccessRule((New-AllowRule `
        -Identity $currentSid `
        -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)))
    [void] $fileAcl.AddAccessRule((New-AllowRule `
        -Identity $everyoneSid `
        -Rights ([System.Security.AccessControl.FileSystemRights]::Read)))
    foreach ($file in @($secretPath, $nestedSecretPath, $binarySecretPath)) {
        Set-Acl -LiteralPath $file -AclObject $fileAcl
    }

    $beforeInventory = Get-TreeInventory -Root $tempRoot
    $rootSddlBeforeReparse = (Get-Acl -LiteralPath $tempRoot).Sddl
    $outsideHashBefore = (Get-FileHash `
        -LiteralPath $outsideSentinelPath `
        -Algorithm SHA256).Hash
    $outsideSddlBefore = (Get-Acl -LiteralPath $outsideSentinelPath).Sddl

    New-Item `
        -ItemType Junction `
        -Path $junctionPath `
        -Target $outsideRoot | Out-Null
    Assert-ProtectorRejectsReparsePoint `
        -DirectoryPath $tempRoot `
        -ServiceIdentity $serviceSid.Value
    if ((Get-Acl -LiteralPath $tempRoot).Sddl -cne $rootSddlBeforeReparse) {
        throw 'Nested reparse rejection changed the secret-root ACL.'
    }
    if ((Get-FileHash -LiteralPath $outsideSentinelPath -Algorithm SHA256).Hash `
            -cne $outsideHashBefore -or
        (Get-Acl -LiteralPath $outsideSentinelPath).Sddl `
            -cne $outsideSddlBefore) {
        throw 'Nested reparse rejection changed the external sentinel.'
    }
    [IO.Directory]::Delete($junctionPath)

    New-Item `
        -ItemType Junction `
        -Path $rootJunction `
        -Target $outsideRoot | Out-Null
    Assert-ProtectorRejectsReparsePoint `
        -DirectoryPath $rootJunction `
        -ServiceIdentity $serviceSid.Value
    if ((Get-FileHash -LiteralPath $outsideSentinelPath -Algorithm SHA256).Hash `
            -cne $outsideHashBefore -or
        (Get-Acl -LiteralPath $outsideSentinelPath).Sddl `
            -cne $outsideSddlBefore) {
        throw 'Root reparse rejection changed the external sentinel.'
    }
    [IO.Directory]::Delete($rootJunction)

    & $protector `
        -DirectoryPath $tempRoot `
        -ServiceIdentity $serviceSid.Value

    $allItems = @((Get-Item -LiteralPath $tempRoot -Force)) + @(
        Get-ChildItem -LiteralPath $tempRoot -Force -Recurse)
    foreach ($item in $allItems) {
        Assert-RestrictedAcl `
            -Path $item.FullName `
            -OwnerSid $currentSid `
            -ServiceSid $serviceSid `
            -IsDirectory ([bool] $item.PSIsContainer)
    }
    Assert-SequenceEqual `
        -Expected $beforeInventory `
        -Actual (Get-TreeInventory -Root $tempRoot) `
        -Description 'Secret-store inventory or file hashes'
    if (-not ((Get-Item -LiteralPath $binarySecretPath -Force).Attributes -band
            [IO.FileAttributes]::Hidden)) {
        throw 'Protecting the secret store removed a hidden-file attribute.'
    }

    $firstRunSddl = Get-TreeSddl -Root $tempRoot
    & $protector `
        -DirectoryPath $tempRoot `
        -ServiceIdentity $serviceSid.Value
    Assert-SequenceEqual `
        -Expected $firstRunSddl `
        -Actual (Get-TreeSddl -Root $tempRoot) `
        -Description 'Idempotent secret-store ACLs'
    Assert-SequenceEqual `
        -Expected $beforeInventory `
        -Actual (Get-TreeInventory -Root $tempRoot) `
        -Description 'Idempotent secret-store inventory or file hashes'

    Write-Host 'Rocket Detailer secret-store ACL contract passed.'
} finally {
    foreach ($junction in @($junctionPath, $rootJunction)) {
        if (Test-Path -LiteralPath $junction) {
            $junctionItem = Get-Item -LiteralPath $junction -Force
            if (-not ($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Refusing to clean non-reparse test path: $junction"
            }
            [IO.Directory]::Delete($junction)
        }
    }

    $resolvedTempBase = [IO.Path]::GetFullPath($tempBase)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedTempBase.StartsWith(
            $systemTemp,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected test path: $resolvedTempBase"
    }
    if (Test-Path -LiteralPath $resolvedTempBase) {
        [IO.Directory]::Delete($resolvedTempBase, $true)
    }
}
