using System;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>One series, episode or movie and the TVDb title it belongs to.</summary>
/// <param name="ItemId">Jellyfin item id (changes when Jellyfin re-creates the item after a move).</param>
/// <param name="Kind"><c>series</c>, <c>episode</c> or <c>movie</c>.</param>
/// <param name="Path">Item path when recorded.</param>
/// <param name="Name">Item name.</param>
/// <param name="Year">Production / premiere year, when known.</param>
/// <param name="SeriesItemId">Owning series item id (episodes only).</param>
/// <param name="TvdbId">TVDb id of the title (the series id for episodes).</param>
/// <param name="TvdbSource">Where the TVDb id came from: <c>jellyfin</c>, <c>tvdb-remoteid</c>, <c>tvdb-search</c> or empty.</param>
/// <param name="EpisodeTvdbId">TVDb id of the episode itself (episodes only).</param>
/// <param name="ImdbId">IMDb id, when Jellyfin has one.</param>
/// <param name="TmdbId">TMDb id, when Jellyfin has one.</param>
public sealed record MediaIdentity(
    Guid ItemId,
    string Kind,
    string Path,
    string Name,
    int? Year,
    Guid? SeriesItemId,
    int? TvdbId,
    string TvdbSource,
    int? EpisodeTvdbId,
    string? ImdbId,
    string? TmdbId);
