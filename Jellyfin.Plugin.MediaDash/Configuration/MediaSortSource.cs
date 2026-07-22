namespace Jellyfin.Plugin.MediaDash.Configuration;

/// <summary>
/// Where the media sorter reads the movie/TV classification of a file from.
/// </summary>
public enum MediaSortSource
{
    /// <summary>Use Jellyfin's own metadata (item type / provider IDs). Skips files Jellyfin couldn't identify.</summary>
    JellyfinMetadata = 0,

    /// <summary>Guess from the filename with a simple heuristic (SxxExx / \d+x\d+ patterns → TV, else movie). Cheap fallback for files Jellyfin didn't tag.</summary>
    FilenameHeuristic = 1
}
