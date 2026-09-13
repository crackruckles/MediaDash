using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// A structured warning attached to an <see cref="Issue"/> via its <c>DetailsJson.warnings[]</c>
/// array. Scanners emit these when a fix would incur data loss that the user should knowingly
/// consent to before it happens. When any warning has <see cref="Blocking"/> = true, the FixTask
/// auto-queue step keeps the issue as <see cref="IssueStatus.Detected"/> — the user has to hit
/// Approve on the Issues tab, which surfaces the warning message alongside the button.
/// </summary>
public sealed class IssueWarning
{
    /// <summary>
    /// Gets or sets the stable warning code (e.g. <c>bitmap-subs-dropped</c>). UI + tests key off
    /// this instead of the human-readable message so wording changes don't break consumers.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this warning blocks auto-queue. When true, the
    /// FixTask's Automatic-mode auto-queue leaves the issue as Detected and requires the user to
    /// hit Approve manually.
    /// </summary>
    [JsonPropertyName("blocking")]
    public bool Blocking { get; set; }

    /// <summary>
    /// Gets or sets the human-readable message. Rendered verbatim under the issue row on the
    /// Issues tab and shown as the Approve button's title so a hover reveals the consequence
    /// before the click.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
