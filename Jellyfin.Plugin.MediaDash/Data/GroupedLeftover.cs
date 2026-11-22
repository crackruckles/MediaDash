namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>A series folder whose TVDb title is grouped into a different master folder.</summary>
/// <param name="Path">The other folder.</param>
/// <param name="TvdbId">TVDb series id shared with the master.</param>
/// <param name="MasterPath">Master folder for the title.</param>
/// <param name="MasterYear">Year of the master title, when known.</param>
public sealed record GroupedLeftover(string Path, int TvdbId, string MasterPath, int? MasterYear);
