using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// One recycle location in an "empty every recycle bin" plan: the root and the batch folders
/// under it that MediaDash can prove it created.
/// </summary>
/// <param name="RootPath">Absolute path of the recycle root.</param>
/// <param name="Batches">Batch folders under the root that will be deleted.</param>
/// <param name="SizeBytes">Total size of those batches.</param>
/// <param name="IsCurrent">True for the currently configured recycle bin.</param>
internal sealed record EmptyPlanLocation(string RootPath, IReadOnlyList<string> Batches, long SizeBytes, bool IsCurrent);
