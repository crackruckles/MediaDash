namespace Jellyfin.Plugin.MediaDash.Metadata;

/// <summary>One episode from a TVDb series' default (aired) order.</summary>
/// <param name="Season">Season number; 0 holds specials.</param>
/// <param name="Number">Episode number within the season.</param>
/// <param name="Name">Episode title (English when TVDb has a translation).</param>
/// <param name="RuntimeMinutes">Listed runtime in minutes, when TVDb has one.</param>
public sealed record TvdbEpisode(int Season, int Number, string Name, int? RuntimeMinutes);
