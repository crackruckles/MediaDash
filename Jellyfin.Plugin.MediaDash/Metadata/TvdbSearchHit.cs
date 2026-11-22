using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Metadata;

/// <summary>One TVDb search result.</summary>
/// <param name="Id">TVDb id.</param>
/// <param name="Name">Primary title.</param>
/// <param name="Year">First-aired / release year.</param>
/// <param name="Aliases">Alternative titles.</param>
public sealed record TvdbSearchHit(int Id, string Name, int? Year, IReadOnlyList<string> Aliases);
