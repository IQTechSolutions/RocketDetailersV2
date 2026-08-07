[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [uri] $BaseUri
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$origin = '{0}://{1}' -f $BaseUri.Scheme, $BaseUri.Authority
$webSocketScheme = switch ($BaseUri.Scheme) {
    'https' { 'wss' }
    'http' { 'ws' }
    default { throw "Unsupported URI scheme: $($BaseUri.Scheme)" }
}
$webSocketOrigin = '{0}://{1}' -f $webSocketScheme, $BaseUri.Authority

function Invoke-TransportProbe {
    param([bool] $OfferCompression)

    $label = if ($OfferCompression) { 'compression-offered' } else { 'baseline' }
    $socket = $null
    $connectCancellation = $null
    $sendCancellation = $null
    $receiveCancellation = $null

    try {
        $negotiation = Invoke-RestMethod `
            -Uri "$origin/_blazor/negotiate?negotiateVersion=1" `
            -Method Post `
            -ContentType 'text/plain;charset=UTF-8' `
            -SessionVariable negotiationSession `
            -TimeoutSec 15

        if ([string]::IsNullOrWhiteSpace([string] $negotiation.connectionToken)) {
            throw 'Negotiate response omitted connectionToken.'
        }

        $socket = New-Object System.Net.WebSockets.ClientWebSocket
        $socket.Options.SetRequestHeader('Origin', $origin)

        $cookieHeader = $negotiationSession.Cookies.GetCookieHeader([uri] $origin)
        if (-not [string]::IsNullOrWhiteSpace($cookieHeader)) {
            $socket.Options.SetRequestHeader('Cookie', $cookieHeader)
        }

        if ($OfferCompression) {
            $socket.Options.SetRequestHeader(
                'Sec-WebSocket-Extensions',
                'permessage-deflate; client_max_window_bits')
        }

        $webSocketUri = [uri](
            $webSocketOrigin +
            '/_blazor?id=' +
            [uri]::EscapeDataString([string] $negotiation.connectionToken))

        $connectCancellation = New-Object System.Threading.CancellationTokenSource
        $connectCancellation.CancelAfter(10000)
        $socket.ConnectAsync(
            $webSocketUri,
            $connectCancellation.Token).GetAwaiter().GetResult() | Out-Null

        $payload = [Text.Encoding]::UTF8.GetBytes(
            "{`"protocol`":`"blazorpack`",`"version`":1}" + [char] 0x1e)
        $sendBuffer = New-Object 'System.ArraySegment[byte]' -ArgumentList @(,$payload)
        $sendCancellation = New-Object System.Threading.CancellationTokenSource
        $sendCancellation.CancelAfter(5000)

        $socket.SendAsync(
            $sendBuffer,
            [System.Net.WebSockets.WebSocketMessageType]::Text,
            $true,
            $sendCancellation.Token).GetAwaiter().GetResult() | Out-Null

        $responseBytes = New-Object byte[] 64
        $receiveBuffer = New-Object 'System.ArraySegment[byte]' -ArgumentList @(,$responseBytes)
        $receiveCancellation = New-Object System.Threading.CancellationTokenSource
        $receiveCancellation.CancelAfter(5000)

        $response = $socket.ReceiveAsync(
            $receiveBuffer,
            $receiveCancellation.Token).GetAwaiter().GetResult()

        $responseHex = if ($response.Count -gt 0) {
            ($responseBytes[0..($response.Count - 1)] |
                ForEach-Object { $_.ToString('x2') }) -join ''
        } else {
            ''
        }

        if ($response.MessageType -ne
                [System.Net.WebSockets.WebSocketMessageType]::Binary -or
            $responseHex -ne '7b7d1e') {
            throw "Expected SignalR acknowledgement 7b7d1e; received $responseHex."
        }

        Write-Host "Blazor transport passed: $label."
    }
    catch {
        throw "Public Blazor transport probe '$label' failed: $($_.Exception.GetBaseException().Message)"
    }
    finally {
        if ($null -ne $receiveCancellation) { $receiveCancellation.Dispose() }
        if ($null -ne $sendCancellation) { $sendCancellation.Dispose() }
        if ($null -ne $connectCancellation) { $connectCancellation.Dispose() }
        if ($null -ne $socket) { $socket.Dispose() }
    }
}

function Invoke-TransportProbeWithRetry {
    param(
        [bool] $OfferCompression,
        [int] $Attempts = 3
    )

    foreach ($attempt in 1..$Attempts) {
        try {
            Invoke-TransportProbe -OfferCompression $OfferCompression
            return
        }
        catch {
            if ($attempt -eq $Attempts) {
                throw
            }

            Write-Warning "$($_.Exception.Message) Retrying ($attempt/$Attempts)."
            Start-Sleep -Seconds 2
        }
    }
}

Invoke-TransportProbeWithRetry -OfferCompression $false
Invoke-TransportProbeWithRetry -OfferCompression $true
