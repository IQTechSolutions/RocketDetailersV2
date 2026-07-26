$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$root = 'C:\inetpub\services\rocket-detailer'
$current = Join-Path $root 'current'
$dotnetEntry = Join-Path $current 'RD.Web.exe'

function Import-ProcessEnvironment([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required environment file is missing: $Path"
    }

    $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($property in $config.PSObject.Properties) {
        [Environment]::SetEnvironmentVariable($property.Name, [string] $property.Value, 'Process')
    }
}

# The shared launcher intentionally understands both generations of the app.
# That keeps rollback to the pre-.NET release possible after the first cutover.
if (Test-Path -LiteralPath $dotnetEntry -PathType Leaf) {
    Import-ProcessEnvironment 'C:\ProgramData\RocketDetailer\secrets\dotnet.application.environment.json'
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Production', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', 'http://127.0.0.1:3000', 'Process')

    Set-Location -LiteralPath $current
    & $dotnetEntry
    exit $LASTEXITCODE
}

# Legacy Node release fallback. Keep this branch until every retained rollback
# release is the .NET application.
$configPath = 'C:\ProgramData\RocketDetailer\secrets\application.environment.json'
$databasePath = 'C:\ProgramData\RocketDetailer\secrets\database.env'
Import-ProcessEnvironment $configPath

$databaseLine = Get-Content -LiteralPath $databasePath |
    Where-Object { $_ -like 'DATABASE_URL=*' } |
    Select-Object -First 1
if (-not $databaseLine) {
    throw 'DATABASE_URL is missing'
}

[Environment]::SetEnvironmentVariable(
    'DATABASE_URL',
    $databaseLine.Substring('DATABASE_URL='.Length),
    'Process')
[Environment]::SetEnvironmentVariable('PORT', '3000', 'Process')

$node = 'C:\Program Files\RocketDetailerRuntime\Node\v24.18.0\node.exe'
$tsx = Join-Path $current 'node_modules\tsx\dist\cli.mjs'
$entry = Join-Path $current 'src\index.ts'
Set-Location -LiteralPath $current
& $node $tsx $entry
exit $LASTEXITCODE
