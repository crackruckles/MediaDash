namespace Jellyfin.Plugin.MediaDash.Compat;

/// <summary>
/// Decode outcome from <see cref="SkiaSharpBridge.Decode"/>.
/// When <see cref="Ok"/> is true, <see cref="Width"/> and <see cref="Height"/> carry the bitmap
/// dimensions. When false and <see cref="Reason"/> is non-null, the reason describes what went
/// wrong. When false and <see cref="Reason"/> is null, the bridge was unavailable (SkiaSharp not
/// loaded — callers should treat the file as un-verifiable and skip).
/// </summary>
/// <param name="Ok">True when decode succeeded.</param>
/// <param name="Reason">Reason text when decode failed with a specific cause.</param>
/// <param name="Width">Bitmap width (only meaningful when Ok is true).</param>
/// <param name="Height">Bitmap height (only meaningful when Ok is true).</param>
public readonly record struct SkiaSharpDecodeResult(bool Ok, string? Reason, int Width, int Height);
