<#
.SYNOPSIS
    End-to-end smoke test for a running NohddX server.

.DESCRIPTION
    Exercises every operator-facing endpoint and prints a pass/fail summary.
    Useful as a CI gate after a deploy and as a "did my change break anything"
    check during development. Exits 0 only when every assertion passes.

    The server is expected to be already running. By default the script targets
    http://localhost:8080 with no admin API key (works in Development with
    AllowAnonymousAdminInDev=true). Override with -ServerUrl / -ApiKey.

.PARAMETER ServerUrl
    Base URL of the NohddX server. Default: http://localhost:8080

.PARAMETER ApiKey
    X-Admin-Api-Key value. Leave empty in dev.

.EXAMPLE
    .\scripts\smoke-test.ps1
    .\scripts\smoke-test.ps1 -ServerUrl http://10.0.0.5:8080 -ApiKey 0123abcd...
#>
[CmdletBinding()]
param(
    [string] $ServerUrl = "http://localhost:8080",
    [string] $ApiKey = ""
)

$ErrorActionPreference = "Stop"
$script:Failures = 0
$script:Passes = 0

function Invoke-Api {
    param(
        [string] $Method,
        [string] $Path,
        [object] $Body = $null,
        [string] $ContentType = "application/json"
    )
    $uri = "$ServerUrl$Path"
    $headers = @{}
    if ($ApiKey) { $headers["X-Admin-Api-Key"] = $ApiKey }

    $params = @{
        Uri = $uri
        Method = $Method
        Headers = $headers
        ContentType = $ContentType
    }
    if ($Body -ne $null) {
        $params["Body"] = if ($ContentType -eq "application/json") { $Body | ConvertTo-Json -Depth 5 } else { $Body }
    }
    return Invoke-RestMethod @params
}

function Assert {
    param([string] $Name, [scriptblock] $Test)
    Write-Host -NoNewline "  $Name ... "
    try {
        $result = & $Test
        if ($result -ne $false) {
            Write-Host "PASS" -ForegroundColor Green
            $script:Passes++
        } else {
            Write-Host "FAIL (returned false)" -ForegroundColor Red
            $script:Failures++
        }
    } catch {
        Write-Host "FAIL: $($_.Exception.Message)" -ForegroundColor Red
        $script:Failures++
    }
}

Write-Host ""
Write-Host "NohddX smoke test against $ServerUrl" -ForegroundColor Cyan
Write-Host ("=" * 60)

# ── Liveness ────────────────────────────────────────────────────
Write-Host ""
Write-Host "Liveness" -ForegroundColor Yellow

Assert "agents/ping" {
    $resp = Invoke-WebRequest -Uri "$ServerUrl/api/agents/ping" -UseBasicParsing
    return ($resp.StatusCode -eq 200 -and $resp.Content -eq "pong")
}

# ── Storage / Monitoring (read-only) ────────────────────────────
Write-Host ""
Write-Host "Read endpoints" -ForegroundColor Yellow

Assert "storage/health returns pool summary" {
    $h = Invoke-Api GET "/api/storage/health"
    return ($h -ne $null -and $h.PSObject.Properties.Name -contains "totalBytes")
}

Assert "storage/disks returns at least one drive" {
    $disks = Invoke-Api GET "/api/storage/disks"
    return ($disks -ne $null -and $disks.Count -ge 1 -and ($disks[0].PSObject.Properties.Name -contains "device"))
}

Assert "monitoring/health enumerates components" {
    $h = Invoke-Api GET "/api/monitoring/health"
    return ($h.components -ne $null -and $h.components.Count -ge 1)
}

Assert "monitoring/audit responds (may be empty)" {
    $a = Invoke-Api GET "/api/monitoring/audit?take=1"
    return ($a -ne $null)
}

Assert "cluster/status responds" {
    $c = Invoke-Api GET "/api/cluster/status"
    return ($c -ne $null)
}

# ── Round-trip: client register → list → assign → wake → delete ──
Write-Host ""
Write-Host "Client lifecycle" -ForegroundColor Yellow

$mac = "AA:BB:CC:" + ("{0:X2}:{1:X2}:{2:X2}" -f (Get-Random -Minimum 0 -Maximum 256), (Get-Random -Minimum 0 -Maximum 256), (Get-Random -Minimum 0 -Maximum 256))
$expectedMac = ($mac -replace ":", "-").ToUpper()
$createdId = $null

Assert "register client (MAC=$mac)" {
    $body = @{ macAddress = $mac; hostname = "smoke-test-$(Get-Random)" }
    $resp = Invoke-Api POST "/api/clients" $body
    $script:createdId = $resp.id
    # MAC must be normalised to hyphen-uppercase form for /api/boot/{mac}.ipxe to work.
    return ($resp.macAddress -eq $expectedMac)
}

Assert "list contains the new client" {
    $clients = Invoke-Api GET "/api/clients"
    return (($clients | Where-Object { $_.id -eq $script:createdId }) -ne $null)
}

Assert "boot endpoint resolves the hyphen MAC (discovery script ok)" {
    $script = Invoke-WebRequest -Uri "$ServerUrl/api/boot/$expectedMac.ipxe" -UseBasicParsing
    # Without an assignment the server returns the "not registered" discovery
    # script. That still proves the route works and the MAC normalised lookup hit.
    return ($script.StatusCode -eq 200 -and $script.Content.StartsWith("#!ipxe"))
}

Assert "delete the client" {
    # Use Invoke-Api so the X-Admin-Api-Key header is only added when ApiKey
    # is non-empty (the auth handler 401s on an empty-value header in dev).
    Invoke-Api DELETE "/api/clients/$script:createdId" | Out-Null
    return $true
}

Assert "client is gone after delete" {
    try {
        Invoke-Api GET "/api/clients/$script:createdId" | Out-Null
        return $false  # if 200, delete didn't take
    } catch {
        # Expect 404
        return $_.Exception.Response.StatusCode.value__ -eq 404
    }
}

# ── Image upload round-trip ────────────────────────────────────
Write-Host ""
Write-Host "Image upload" -ForegroundColor Yellow

$tempFile = Join-Path $env:TEMP "nohddx-smoke-$(Get-Random).vhd"
try {
    # Tiny 64 KB test payload so the script stays fast.
    $bytes = New-Object byte[] (64 * 1024)
    (New-Object Random).NextBytes($bytes)
    [IO.File]::WriteAllBytes($tempFile, $bytes)
    $sha = (Get-FileHash $tempFile -Algorithm SHA256).Hash.ToLower()

    Assert "upload streams a 64 KB image" {
        $name = "smoke-img-$(Get-Random)"
        $uri = "$ServerUrl/api/images/upload?name=$name&osType=Linux&version=1.0&extension=vhd"
        $headers = @{}
        if ($ApiKey) { $headers["X-Admin-Api-Key"] = $ApiKey }
        $resp = Invoke-RestMethod -Method POST -Uri $uri -InFile $tempFile `
            -ContentType "application/octet-stream" -Headers $headers
        $script:uploadedImageId = $resp.id
        $script:uploadedChecksum = $resp.checksum
        return ($resp.sizeBytes -eq $bytes.Length)
    }

    Assert "server SHA-256 matches local hash" {
        return ($script:uploadedChecksum -eq "sha256:$sha")
    }

    Assert "audit log shows image.upload" {
        $entries = Invoke-Api GET "/api/monitoring/audit?action=image.upload&take=10"
        return (($entries | Where-Object { $_.targetId -eq $script:uploadedImageId }) -ne $null)
    }

    Assert "delete the uploaded image" {
        Invoke-Api DELETE "/api/images/$script:uploadedImageId" | Out-Null
        return $true
    }
} finally {
    if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
}

# ── Summary ────────────────────────────────────────────────────
Write-Host ""
Write-Host ("=" * 60)
$total = $script:Passes + $script:Failures
if ($script:Failures -eq 0) {
    Write-Host "ALL CHECKS PASSED ($script:Passes/$total)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:Failures)/$total checks FAILED" -ForegroundColor Red
    exit 1
}
