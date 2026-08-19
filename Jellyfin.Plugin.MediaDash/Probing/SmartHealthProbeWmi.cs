using System;
using System.Collections.Generic;
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

    // Windows exposes SMART interpretation via the static method
    //   PS_StorageCmdlets::GetStorageReliabilityCounter(PhysicalDisk, [out] StorageReliabilityCounter)
    // in root\Microsoft\Windows\Storage (this is what the Get-StorageReliabilityCounter PowerShell cmdlet
    // wraps). We invoke that method on the ManagementClass and hand it the physical-disk instance.
    private static void FillReliabilityCounters(System.Management.ManagementBaseObject pd, SmartHealthResult result)
    {
        if (pd is not System.Management.ManagementObject mo)
        {
            return;
        }

        try
        {
            var scope = new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var cmdletsClass = new System.Management.ManagementClass(
                scope,
                new System.Management.ManagementPath("PS_StorageCmdlets"),
                null);
            using var inParams = cmdletsClass.GetMethodParameters("GetStorageReliabilityCounter");
            inParams["PhysicalDisk"] = mo;

            using var outParams = cmdletsClass.InvokeMethod("GetStorageReliabilityCounter", inParams, null);
            if (outParams?["StorageReliabilityCounter"] is not System.Management.ManagementBaseObject counter)
            {
                return;
            }

            using (counter)
            {
                result.TemperatureCelsius = TryInt(counter["Temperature"]);
                result.TemperatureMaxCelsius = TryInt(counter["TemperatureMax"]);
                result.WearPercent = TryInt(counter["Wear"]);
                result.PowerOnHours = TryLong(counter["PowerOnHours"]);
                result.ReadErrorsUncorrected = TryLong(counter["ReadErrorsUncorrected"]);
                result.WriteErrorsUncorrected = TryLong(counter["WriteErrorsUncorrected"]);
            }
        }
        catch (System.Management.ManagementException ex)
        {
            Diagnostics.Record("SmartHealth.Wmi", "GetStorageReliabilityCounter failed for " + (result.ModelName ?? "physical disk") + ": " + ex.Message + ". Stats will be blank.");
        }
        catch (COMException ex)
        {
            Diagnostics.Record("SmartHealth.Wmi", "GetStorageReliabilityCounter COM error for " + (result.ModelName ?? "physical disk") + ": " + ex.Message + ".");
        }
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
