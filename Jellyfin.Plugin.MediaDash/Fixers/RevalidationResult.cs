using Jellyfin.Plugin.MediaDash.Probing;

namespace Jellyfin.Plugin.MediaDash.Fixers;

// RevalidationVerdict moved to RevalidationVerdict.cs (00-D file-layout move).

/// <summary>
/// Wraps a <see cref="RevalidationVerdict"/> with the successful probe payload (when available) so
/// the repair ladder can reuse it, and a human-readable diagnostic for the Indeterminate case.
/// </summary>
/// <param name="Verdict">The revalidation outcome.</param>
/// <param name="Probe">The successful probe result (null when Verdict is Indeterminate).</param>
/// <param name="Diagnostic">Human-readable explanation for Indeterminate results; null otherwise.</param>
/// <param name="ContentProblem">True when the file itself is the reason (not media at all), so there is nothing for the user to fix in MediaDash or FFmpeg.</param>
internal readonly record struct RevalidationResult(RevalidationVerdict Verdict, FfprobeData? Probe, string? Diagnostic, bool ContentProblem = false)
{
    /// <summary>Successful revalidation confirming the flagged defect still holds.</summary>
    /// <param name="probe">The probe result to reuse in the repair ladder.</param>
    /// <returns>A Broken revalidation result.</returns>
    public static RevalidationResult Broken(FfprobeData probe) => new(RevalidationVerdict.Broken, probe, null);

    /// <summary>Successful revalidation showing the file is no longer broken.</summary>
    /// <param name="probe">The probe result.</param>
    /// <returns>A Healthy revalidation result.</returns>
    public static RevalidationResult Healthy(FfprobeData probe) => new(RevalidationVerdict.Healthy, probe, null);

    /// <summary>Revalidation could not run (infrastructure failure). No mutation permitted.</summary>
    /// <param name="diagnostic">Human-readable reason for the indeterminate outcome.</param>
    /// <returns>An Indeterminate revalidation result.</returns>
    public static RevalidationResult Indeterminate(string diagnostic) => new(RevalidationVerdict.Indeterminate, null, diagnostic);

    /// <summary>The file is not media at all: no repair is possible, and nothing is wrong with MediaDash or FFmpeg.</summary>
    /// <param name="diagnostic">Human-readable reason shown with the issue.</param>
    /// <returns>An Indeterminate revalidation result marked as a content problem.</returns>
    public static RevalidationResult NotMedia(string diagnostic) => new(RevalidationVerdict.Indeterminate, null, diagnostic, ContentProblem: true);
}
