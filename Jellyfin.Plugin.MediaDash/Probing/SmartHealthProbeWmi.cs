using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Jellyfin.Plugin.MediaDash.Api;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Windows-only SMART probe that reads <c>MSFT_PhysicalDisk</c> (Windows 8+) via WMI, no external tool
/// required. Falls through to smartctl when WMI can't answer (Storage Spaces virtual disks, USB-bridged
/// drives that hide their SMART data, etc.). Never throws — callers get <see cref="SmartHealth.Unknown"/>
/// if anything goes sideways and the fallback chain takes over.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SmartHealthProbeWmi
{
    /// <summary>
    /// Returns SMART health for the physical disk hosting <paramref name="driveRoot"/> as reported by
    /// Windows' own Storage subsystem. Returns null when WMI is unavailable or the drive can't be
    /// correlated, so the outer probe can keep walking the fallback chain.
    /// </summary>
    /// <param name="driveRoot">Drive root, e.g. <c>C:\</c>.</param>
    /// <returns>The WMI verdict, or null when nothing usable came back.</returns>
    public static SmartHealthResult? Query(string driveRoot)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var letter = ExtractDriveLetter(driveRoot);
        if (letter is null)
        {
            return null;
        }

        try
        {
            return QueryStorageManagement(letter) ?? QueryLegacyPredict(letter);
        }
        catch (System.Management.ManagementException ex)
        {
            Diagnostics.Record("SmartHealth.Wmi", "WMI query failed for " + letter + ": " + ex.Message + ". Falling back to smartctl.");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostics.Record("SmartHealth.Wmi", "WMI access denied for " + letter + ": " + ex.Message + ". Jellyfin needs admin rights to read SMART via WMI on some hosts.");
            return null;
        }
        catch (COMException ex)
        {
            Diagnostics.Record("SmartHealth.Wmi", "WMI COM error for " + letter + ": " + ex.Message + ".");
            return null;
        }
    }

    // MSFT_PhysicalDisk is the modern Storage subsystem view (Win8+) and exposes HealthStatus
    // (0=Healthy, 1=Warning, 2=Unhealthy, 5=Unknown). Correlation via ManagementObject.GetRelated()
    // walks the Volume → Partition → Disk → PhysicalDisk association chain without hand-building WQL
    // (the ObjectId strings contain backslashes/quotes that break naive WQL).
    private static SmartHealthResult? QueryStorageManagement(string driveLetter)
    {
        // MSFT_Volume.DriveLetter is a single char without the colon; letter comes in as "C:".
        var vLetter = driveLetter.TrimEnd(':');
        // Defense in depth: validate strictly before interpolating into WQL. Current call sites pass
        // DriveInfo.GetDrives() results (already safe), but any future user-controlled path into this
        // method would otherwise be a WQL injection surface.
        if (vLetter.Length != 1 || !char.IsAsciiLetter(vLetter[0]))
        {
            return null;
        }

        var scope = new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        scope.Connect();

        using var volumeSearcher = new System.Management.ManagementObjectSearcher(
            scope,
            new System.Management.ObjectQuery("SELECT * FROM MSFT_Volume WHERE DriveLetter = '" + vLetter + "'"));
        foreach (var v in volumeSearcher.Get())
        {
            using (v as IDisposable)
            {
                if (v is not System.Management.ManagementObject volume)
                {
                    continue;
                }

                foreach (var partitionObj in volume.GetRelated("MSFT_Partition"))
                {
                    using (partitionObj as IDisposable)
                    {
                        if (partitionObj is not System.Management.ManagementObject partition)
                        {
                            continue;
                        }

                        foreach (var diskObj in partition.GetRelated("MSFT_Disk"))
                        {
                            using (diskObj as IDisposable)
                            {
                                if (diskObj is not System.Management.ManagementObject disk)
                                {
                                    continue;
                                }

                                foreach (var pd in disk.GetRelated("MSFT_PhysicalDisk"))
                                {
                                    using (pd as IDisposable)
                                    {
                                        return MapPhysicalDisk(pd, driveLetter);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Fallback for machines with exactly one physical disk: skip the (fragile) association chain and
        // just report that disk's health for the requested drive. On a home Jellyfin box this is common.
        return QuerySolePhysicalDisk(scope, driveLetter);
    }

    private static SmartHealthResult? QuerySolePhysicalDisk(System.Management.ManagementScope scope, string driveLetter)
    {
        using var pdSearcher = new System.Management.ManagementObjectSearcher(
            scope,
            new System.Management.ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
        var results = pdSearcher.Get();
        // Enumerate defensively — GetRelated is what we skipped, so only trust this when there's exactly one physical disk.
        System.Management.ManagementBaseObject? only = null;
        var count = 0;
        foreach (var pd in results)
        {
            count++;
            if (count > 1)
            {
                only?.Dispose();
                (pd as IDisposable)?.Dispose();
                return null;
            }

            only = pd;
        }

        if (only is null)
        {
            return null;
        }

        try
        {
            return MapPhysicalDisk(only, driveLetter);
        }
        finally
        {
            only.Dispose();
        }
    }

    private static SmartHealthResult MapPhysicalDisk(System.Management.ManagementBaseObject pd, string driveLetter)
    {
        var health = TryUShort(pd["HealthStatus"]);
        var model = pd["FriendlyName"]?.ToString() ?? "physical disk";
        var result = health switch
        {
            0 => new SmartHealthResult(SmartHealth.Healthy, "Windows reports " + model + " (hosting " + driveLetter + ") is Healthy (WMI/MSFT_PhysicalDisk)."),
            1 => new SmartHealthResult(SmartHealth.Warning, "Windows reports " + model + " (hosting " + driveLetter + ") is Warning (WMI/MSFT_PhysicalDisk) — replace soon."),
            2 => new SmartHealthResult(SmartHealth.Failing, "Windows reports " + model + " (hosting " + driveLetter + ") is Unhealthy (WMI/MSFT_PhysicalDisk) — back up now."),
            _ => new SmartHealthResult(SmartHealth.Unknown, "Windows can't determine health for " + model + " (WMI/MSFT_PhysicalDisk).")
        };

        result.ModelName = pd["FriendlyName"]?.ToString();
        FillReliabilityCounters(pd, result);
        return result;
    }

    // Windows exposes SMART attributes on the MSFT_StorageReliabilityCounter class, associated to
    // each MSFT_PhysicalDisk. Previously we invoked PS_StorageCmdlets::GetStorageReliabilityCounter
    // as a static method — that path was more fragile than the association traversal (was returning
    // no data on a machine where the same call via Get-StorageReliabilityCounter cmdlet worked
    // fine). GetRelated is the same mechanism Volume→Partition→Disk→PhysicalDisk uses upstream and
    // is what surfaces the tiles on the Overview.
    private static void FillReliabilityCounters(System.Management.ManagementBaseObject pd, SmartHealthResult result)
    {
        if (pd is not System.Management.ManagementObject mo)
        {
            return;
        }

        // Two access paths in priority order — some Windows builds return an empty association from
        // GetRelated while an explicit query works, and vice versa. Try associations first (cheap),
        // then fall through to an explicit ASSOCIATORS OF WQL if nothing came back.
        try
        {
            if (FillFromReliabilityCounter(mo.GetRelated("MSFT_StorageReliabilityCounter"), result))
            {
                return;
            }

            // Association enumeration returned nothing. Try the explicit associators query — some
            // hosts don't publish the reverse-direction assoc from PhysicalDisk to Counter, but do
            // via MSFT_PhysicalDiskToStorageReliabilityCounter directly.
            if (mo.Path?.RelativePath is string relPath && !string.IsNullOrEmpty(relPath))
            {
                var scope = mo.Scope ?? new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                if (!scope.IsConnected)
                {
                    scope.Connect();
                }

                using var searcher = new System.Management.ManagementObjectSearcher(
                    scope,
                    new System.Management.RelatedObjectQuery(relPath, "MSFT_StorageReliabilityCounter"));
                if (FillFromReliabilityCounter(searcher.Get(), result))
                {
                    return;
                }
            }

            // Both System.Management (DCOM/classic WMI) paths returned empty. On some hosts —
            // observed on Windows 11 with a Lexar NM790 — MSFT_StorageReliabilityCounter is only
            // enumerable through the newer CIM/WSMan stack that PowerShell's Get-CimAssociatedInstance
            // uses. Shell out as a last resort. It's Windows-only, gated to library/recycle drives,
            // and only fires when both WMI paths already came back empty, so overhead is bounded.
            if (FillFromPowerShell(result))
            {
                return;
            }

            Diagnostics.Record("SmartHealth.Wmi", "No MSFT_StorageReliabilityCounter association found for " + (result.ModelName ?? "physical disk") + " via any WMI or PowerShell path. Detail tiles will show 'no attributes reported'; the health pill is unaffected.");
        }
        catch (System.Management.ManagementException ex)
        {
            Diagnostics.Record("SmartHealth.Wmi", "MSFT_StorageReliabilityCounter lookup failed for " + (result.ModelName ?? "physical disk") + ": " + ex.Message + ". Stats will be blank.");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Some Windows hosts require elevation to read StorageReliabilityCounter even when the
            // health pill is readable. Surface this specifically so users know to run Jellyfin
            // elevated (or accept blank tiles) — the pill still shows honestly.
            Diagnostics.Record("SmartHealth.Wmi", "Access denied reading MSFT_StorageReliabilityCounter for " + (result.ModelName ?? "physical disk") + ": " + ex.Message + ". Detail tiles will be blank; run Jellyfin as an administrator to expose them (the SMART pill still works).");
        }
        catch (COMException ex)
        {
            Diagnostics.Record("SmartHealth.Wmi", "MSFT_StorageReliabilityCounter COM error for " + (result.ModelName ?? "physical disk") + ": " + ex.Message + ".");
        }
    }

    private static bool FillFromReliabilityCounter(System.Management.ManagementObjectCollection results, SmartHealthResult result)
    {
        foreach (var counterObj in results)
        {
            using (counterObj as IDisposable)
            {
                if (counterObj is not System.Management.ManagementBaseObject counter)
                {
                    continue;
                }

                result.TemperatureCelsius = TryInt(counter["Temperature"]);
                result.TemperatureMaxCelsius = TryInt(counter["TemperatureMax"]);
                result.WearPercent = TryInt(counter["Wear"]);
                result.PowerOnHours = TryLong(counter["PowerOnHours"]);
                result.ReadErrorsUncorrected = TryLong(counter["ReadErrorsUncorrected"]);
                result.WriteErrorsUncorrected = TryLong(counter["WriteErrorsUncorrected"]);
                return true;
            }
        }

        return false;
    }

    // Last-resort PowerShell shell-out. Matches by FriendlyName against the pre-populated
    // result.ModelName. Cheap because it's gated behind both WMI paths returning empty AND
    // fires at most once per (drive, cache-TTL=10min).
    private static bool FillFromPowerShell(SmartHealthResult result)
    {
        if (string.IsNullOrEmpty(result.ModelName))
        {
            return false;
        }

        // Emit one row per PhysicalDisk with tab-separated fields. Empty fields become empty
        // strings (Get-StorageReliabilityCounter returns null for unsupported attributes on a
        // given drive — Lexar NM790 for example doesn't report PowerOnHours).
        // -join keeps the payload single-line-per-drive so a simple line-split parser works.
        const string script =
            "$ErrorActionPreference='Stop';" +
            "Get-PhysicalDisk | ForEach-Object {" +
            "  $c = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue;" +
            "  ($_.FriendlyName,$c.Temperature,$c.TemperatureMax,$c.Wear,$c.PowerOnHours,$c.ReadErrorsUncorrected,$c.WriteErrorsUncorrected) -join [char]9" +
            "}";

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try
                {
                    p.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var matched = false;
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim('\r');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var parts = trimmed.Split('\t');
                // PowerShell's -join drops trailing empty fields, so a drive that only reports
                // Temperature/TemperatureMax/Wear (like the Lexar NM790) comes out with 4 fields
                // instead of 7. Accept anything with a name + at least one value and fill
                // per-position; missing indexes just stay null.
                if (parts.Length < 2)
                {
                    continue;
                }

                // Trim each field individually — PowerShell can pad with spaces when a value is
                // whitespace-sensitive, and stray leading spaces have caused the OrdinalIgnoreCase
                // name match to silently miss. This also stabilises int.TryParse below.
                for (var i = 0; i < parts.Length; i++)
                {
                    parts[i] = parts[i].Trim();
                }

                if (!string.Equals(parts[0], result.ModelName?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched = true;

                if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                {
                    result.TemperatureCelsius = t;
                }

                if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tm))
                {
                    result.TemperatureMaxCelsius = tm;
                }

                if (parts.Length > 3 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
                {
                    result.WearPercent = w;
                }

                if (parts.Length > 4 && long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var poh))
                {
                    result.PowerOnHours = poh;
                }

                if (parts.Length > 5 && long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var re))
                {
                    result.ReadErrorsUncorrected = re;
                }

                if (parts.Length > 6 && long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var we))
                {
                    result.WriteErrorsUncorrected = we;
                }

                // Consider it a hit if we got AT LEAST one field. Some drives report only a subset.
                if (result.TemperatureCelsius.HasValue
                    || result.WearPercent.HasValue
                    || result.PowerOnHours.HasValue
                    || result.ReadErrorsUncorrected.HasValue)
                {
                    return true;
                }
            }

            // We got here with matched=false → PowerShell ran but no line matched the model name.
            // Surface the (truncated) stdout so we can see WHAT PowerShell returned. Common cause:
            // FriendlyName mismatch between the WMI ModelName we set and PowerShell's FriendlyName
            // (e.g. trailing space, alternate spelling from a driver update).
            if (!matched)
            {
                var preview = stdout.Length > 200 ? stdout[..200] + "…" : stdout;
                preview = preview.Replace('\t', '|').Replace('\r', ' ').Replace('\n', ' ');
                Diagnostics.Record(
                    "SmartHealth.Wmi",
                    "PowerShell counter fallback ran for '" + result.ModelName + "' but no output line's FriendlyName matched. Raw stdout (tabs shown as |): '" + preview + "'.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // powershell.exe not on PATH (unusual on Windows, but possible on stripped installs).
        }
        catch (System.IO.IOException)
        {
        }

        return false;
    }

    // Older Windows or drives Storage Spaces doesn't enumerate — fall back to the ATA predict-fail bit
    // exposed by MSStorageDriver. Coarse (bool only) but has been around since XP.
    private static SmartHealthResult? QueryLegacyPredict(string driveLetter)
    {
        var scope = new System.Management.ManagementScope(@"\\.\root\wmi");
        scope.Connect();
        using var searcher = new System.Management.ManagementObjectSearcher(
            scope,
            new System.Management.ObjectQuery("SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus"));
        var anyFailing = false;
        var anySeen = false;
        foreach (var status in searcher.Get())
        {
            using (status as IDisposable)
            {
                anySeen = true;
                if (status["PredictFailure"] is bool predict && predict)
                {
                    anyFailing = true;
                }
            }
        }

        if (!anySeen)
        {
            return null;
        }

        if (anyFailing)
        {
            // We can't cheaply pin which physical drive maps to which letter here — a fail on ANY drive
            // is still worth surfacing at drive-scope, but be honest about the imprecision.
            return new SmartHealthResult(SmartHealth.Failing, "Windows SMART self-assessment predicts failure on at least one physical drive (WMI/MSStorageDriver). Check Storage Management.");
        }

        return new SmartHealthResult(SmartHealth.Healthy, "Windows SMART self-assessment passes on all drives (WMI/MSStorageDriver).");
    }

    private static string? ExtractDriveLetter(string driveRoot)
    {
        var t = driveRoot.TrimEnd('\\', '/');
        return t.Length >= 2 && t[1] == ':' ? t[..2].ToUpper(CultureInfo.InvariantCulture) : null;
    }

    private static int TryUShort(object? o)
        => o switch
        {
            ushort us => us,
            int i => i,
            long l => (int)l,
            _ => int.TryParse(o?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1
        };

    private static int? TryInt(object? o)
    {
        if (o is null)
        {
            return null;
        }

        return o switch
        {
            byte b => b,
            ushort us => us,
            short s => s,
            int i => i,
            uint ui => (int)ui,
            long l => (int)l,
            ulong ul => (int)ul,
            _ => int.TryParse(o.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        };
    }

    private static long? TryLong(object? o)
    {
        if (o is null)
        {
            return null;
        }

        return o switch
        {
            byte b => b,
            ushort us => us,
            short s => s,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul => (long)ul,
            _ => long.TryParse(o.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        };
    }
}
