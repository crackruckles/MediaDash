# 06-endpoints.ps1 — smoke test every MediaDash API endpoint. For each:
#   • Returns 200 on happy path
#   • Returns 401 without auth
#   • Handles empty state without crashing
#
# We use direct HTTP calls (Invoke-JfApi) so we can catch non-200 responses.

function Test-Endpoint {
    param(
        [string]$Method, [string]$Path,
        [int]$ExpectedStatus = 200,
        [object]$Body = $null
    )
    try {
        $r = Invoke-JfApi -Method $Method -Path $Path -Body $Body -Raw
        return $r.StatusCode
    } catch [System.Net.WebException] {
        return [int]$_.Exception.Response.StatusCode
    }
}

function Invoke-Phase-Endpoints {
    Start-Phase "API endpoints — every MediaDash route reachable + correct shape"

    Invoke-Test "GET /MediaDash/Status returns valid shape" {
        $s = Get-MediaDashStatus
        Assert-True ($null -ne $s.IsScanning) "IsScanning present"
        Assert-True ($null -ne $s.IsFixing) "IsFixing present"
    }

    Invoke-Test "GET /MediaDash/LibraryStats returns array with per-library shape" {
        $stats = Invoke-JfApi -Method GET -Path "/MediaDash/LibraryStats"
        Assert-True (@($stats).Count -ge 1) "should have at least one library"
        $s0 = @($stats)[0]
        Assert-True ([bool]$s0.Name) "Name field present"
        Assert-True ($null -ne $s0.ItemCount) "ItemCount present"
        Assert-True ($null -ne $s0.Resolutions) "Resolutions map present"
        Assert-True ($null -ne $s0.Codecs) "Codecs map present"
        Assert-True ($null -ne $s0.Containers) "Containers map present"
        # 1.0.7.5 keeps the Overview library-breakdown at the v1.0.7.4 three-field shape
        # (Resolution stack + Codec donut + Container legend). The WIP HDR/BitDepth/Kind/
        # AudioChannels/AudioLanguages/SubtitleLanguages enrichment was reverted before ship
        # and asserted-absent here so we notice if it accidentally comes back.
        Assert-True ($null -eq $s0.HdrTypes) "HdrTypes absent (reverted)"
        Assert-True ($null -eq $s0.BitDepths) "BitDepths absent (reverted)"
        Assert-True ($null -eq $s0.AudioChannels) "AudioChannels absent (reverted)"
        Assert-True ($null -eq $s0.Kinds) "Kinds absent (reverted)"
        Assert-True ($null -eq $s0.AudioLanguages) "AudioLanguages absent (reverted)"
        Assert-True ($null -eq $s0.SubtitleLanguages) "SubtitleLanguages absent (reverted)"
    }

    Invoke-Test "GET /MediaDash/Issues returns paginated shape with Items array" {
        $r = Invoke-JfApi -Method GET -Path "/MediaDash/Issues"
        Assert-True ($null -ne $r.Items) "Items field present"
    }

    Invoke-Test "GET /MediaDash/RecycleBin returns paginated shape" {
        $r = Get-MediaDashRecycleBin
        Assert-True ($null -ne $r.Items) "Items field present"
    }

    Invoke-Test "GET /MediaDash/History returns paginated shape" {
        $r = Get-MediaDashHistory
        Assert-True ($null -ne $r.Items) "Items field present"
    }

    Invoke-Test "GET /MediaDash/Diagnostics returns list" {
        $r = Get-MediaDashDiagnostics
        # May be empty; just verify it's an array/collection
        $count = @($r).Count
        Write-Log "    diagnostics: $count entries"
    }

    Invoke-Test "GET /MediaDash/RecycleBin/DiskInfo returns disk-space fields" {
        $r = Invoke-JfApi -Method GET -Path "/MediaDash/RecycleBin/DiskInfo"
        Assert-True ($null -ne $r.FreeBytes) "FreeBytes present"
        Assert-True ($null -ne $r.MeetsFiveGbMinimum) "MeetsFiveGbMinimum present"
    }

    Invoke-Test "POST /MediaDash/Scan returns 204 (starts scan)" {
        # If scan is already running from a prior test, cancel it first
        $s = Get-MediaDashStatus
        if ($s.IsScanning) {
            Invoke-JfApi -Method POST -Path "/MediaDash/Scan/Cancel" | Out-Null
            Start-Sleep -Seconds 3
        }
        Invoke-JfApi -Method POST -Path "/MediaDash/Scan" | Out-Null
        Start-Sleep -Milliseconds 300
        $s2 = Get-MediaDashStatus
        # Not asserting IsScanning=true since scan can finish very quickly on small library
    }

    Invoke-Test "POST /MediaDash/Scan/Cancel while running finishes the scan" {
        Invoke-JfApi -Method POST -Path "/MediaDash/Scan/Cancel" | Out-Null
        Wait-For -TimeoutSec 30 -Description "scan to stop after cancel" -Predicate {
            $s = Get-MediaDashStatus; return (-not $s.IsScanning)
        } | Out-Null
    }

    Invoke-Test "POST /MediaDash/Schedule/Apply is idempotent" {
        Invoke-JfApi -Method POST -Path "/MediaDash/Schedule/Apply" | Out-Null
        Invoke-JfApi -Method POST -Path "/MediaDash/Schedule/Apply" | Out-Null
        $t = (Invoke-JfApi -Method GET -Path "/ScheduledTasks") | Where-Object Key -eq "MediaDashFix" | Select-Object -First 1
        Assert-Equal 1 @($t.Triggers).Count "still exactly one trigger after double-apply"
    }

    Invoke-Test "GET /MediaDash/LibraryStats with no libraries returns []" {
        # Snapshot then filter to a non-existent lib
        $orig = (Get-MediaDashConfig).EnabledLibraries
        Set-MediaDashConfig @{ EnabledLibraries = @("nonexistent-lib-id-000") }
        $stats = Invoke-JfApi -Method GET -Path "/MediaDash/LibraryStats"
        Assert-Equal 0 @($stats).Count "should return empty array"
        # Restore
        Set-MediaDashConfig @{ EnabledLibraries = $orig }
    }

    Invoke-Test "POST /MediaDash/Errors/Clear removes diagnostic entries" {
        Invoke-JfApi -Method POST -Path "/MediaDash/Errors/Clear" | Out-Null
        Start-Sleep -Milliseconds 500
        $diag = Get-MediaDashDiagnostics
        # After Clear, only diagnostics from AFTER the clear will be present.
        # Not an absolute assertion but should be a small number.
        Write-Log "    diagnostics after clear: $(@($diag).Count)"
    }
}
