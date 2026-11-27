using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.MediaDash.Compat;

/// <summary>
/// Reflects over whatever SkiaSharp assembly the host process has loaded, so MediaDash can
/// decode images without a compile-time or runtime dependency on a specific SkiaSharp major
/// version. Jellyfin 10.11 ships SkiaSharp 2.88 with native libSkiaSharp 88.x; Jellyfin 12.0
/// ships SkiaSharp 3.x with native 119.x. Managed 2.88 and native 119 do not interoperate,
/// so bundling either version in the plugin folder breaks the other host.
/// </summary>
/// <remarks>
/// Discovery happens lazily on first use. All lookups are cached. Any reflection failure
/// leaves the bridge in an "unavailable" state and callers get a null/false result — never
/// an unhandled exception.
/// </remarks>
public sealed class SkiaSharpBridge
{
    private static readonly Lazy<SkiaSharpBridge> LazyInstance = new(() => new SkiaSharpBridge());

    private readonly MethodInfo? _decodeStream;
    private readonly PropertyInfo? _widthProperty;
    private readonly PropertyInfo? _heightProperty;

    private SkiaSharpBridge()
    {
        var skiaAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "SkiaSharp", StringComparison.Ordinal));

        // ponytail: if not yet loaded, attempt a by-name load. This covers test runners and any host that
        // ships SkiaSharp but hasn't touched it before our plugin initialises. Failure is benign for the
        // plugin overall (artwork scanner falls back to shallow checks), but the admin should see it once
        // so they know why deeper image validation isn't running.
        if (skiaAssembly is null)
        {
            try
            {
                skiaAssembly = Assembly.Load("SkiaSharp");
            }
            catch (Exception ex)
            {
                Api.Diagnostics.Record("SkiaSharpBridge.LoadFailed", "Artwork checks are limited: the image library MediaDash uses (SkiaSharp) is not available on this server, so it only checks image headers and may miss some damaged artwork. Nothing needs to be done unless broken artwork goes unflagged. Details: " + ex.Message);
                return;
            }
        }

        var bitmapType = skiaAssembly.GetType("SkiaSharp.SKBitmap");
        if (bitmapType is null)
        {
            Api.Diagnostics.Record("SkiaSharpBridge.LoadFailed", "Artwork checks are limited: this server has a version of the image library (SkiaSharp) that MediaDash does not recognise, so it only checks image headers. Updating MediaDash may restore full checks.");
            return;
        }

        // SKBitmap.Decode(Stream) exists in both 2.x and 3.x.
        _decodeStream = bitmapType.GetMethod(
            "Decode",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(Stream)],
            modifiers: null);

        _widthProperty = bitmapType.GetProperty("Width", BindingFlags.Public | BindingFlags.Instance);
        _heightProperty = bitmapType.GetProperty("Height", BindingFlags.Public | BindingFlags.Instance);
    }

    /// <summary>Gets the process-wide singleton bridge.</summary>
    public static SkiaSharpBridge Instance => LazyInstance.Value;

    /// <summary>
    /// Gets a value indicating whether SkiaSharp was discovered in the host process and the required
    /// methods are callable via reflection. False on any host that doesn't have SkiaSharp loaded, or
    /// where the API shape has drifted beyond what this bridge understands.
    /// </summary>
    public bool IsAvailable => _decodeStream is not null && _widthProperty is not null && _heightProperty is not null;

    /// <summary>
    /// Attempts to decode the given stream as an image.
    /// Any reflection exception is caught and surfaced as a <see cref="SkiaSharpDecodeResult.Reason"/>.
    /// </summary>
    /// <param name="stream">The stream containing the image bytes. Not disposed.</param>
    /// <returns>The result.</returns>
    public SkiaSharpDecodeResult Decode(Stream stream)
    {
        if (!IsAvailable)
        {
            return new SkiaSharpDecodeResult(false, null, 0, 0);
        }

        object? bitmap;
        try
        {
            bitmap = _decodeStream!.Invoke(null, [stream]);
        }
        catch (TargetInvocationException ex)
        {
            Api.Diagnostics.Record("SkiaSharpBridge.DecodeUnavailable", "Full artwork checks are not available on this server, so images are only checked by their header. Details: " + (ex.InnerException?.Message ?? ex.Message));
            return new SkiaSharpDecodeResult(false, null, 0, 0);
        }
        catch (Exception ex)
        {
            Api.Diagnostics.Record("SkiaSharpBridge.DecodeUnavailable", "Full artwork checks are not available on this server, so images are only checked by their header. Details: " + ex.Message);
            return new SkiaSharpDecodeResult(false, null, 0, 0);
        }

        if (bitmap is null)
        {
            return new SkiaSharpDecodeResult(false, "decode returned null bitmap", 0, 0);
        }

        try
        {
            using var disposable = bitmap as IDisposable;
            var width = _widthProperty!.GetValue(bitmap) as int? ?? 0;
            var height = _heightProperty!.GetValue(bitmap) as int? ?? 0;
            return new SkiaSharpDecodeResult(true, null, width, height);
        }
        catch (Exception ex)
        {
            Api.Diagnostics.Record("SkiaSharpBridge.DecodeUnavailable", "Full artwork checks are not available on this server, so images are only checked by their header. Details: " + ex.Message);
            return new SkiaSharpDecodeResult(false, null, 0, 0);
        }
    }
}
