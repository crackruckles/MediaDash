using System;
using Jellyfin.Plugin.MediaDash.Fixers;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class MediaMoveUserDataTests
{
    [Fact]
    public void CopiesUserStateWithoutCopyingTheOldNativeKey()
    {
        var original = new UserItemData { Key = "old", Played = true, PlayCount = 4, PlaybackPositionTicks = 123,
            IsFavorite = true, LastPlayedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Rating = 8,
            AudioStreamIndex = 2, SubtitleStreamIndex = 3 };
        var target = new UserItemData { Key = "new" };
        Assert.True(MediaMoveUserData.RestoreState(original, target));
        Assert.Equal("new", target.Key);
        Assert.Equal(original.Played, target.Played);
        Assert.Equal(original.PlayCount, target.PlayCount);
        Assert.Equal(original.PlaybackPositionTicks, target.PlaybackPositionTicks);
        Assert.Equal(original.IsFavorite, target.IsFavorite);
        Assert.Equal(original.LastPlayedDate, target.LastPlayedDate);
        Assert.Equal(original.Rating, target.Rating);
        Assert.Equal(original.Likes, target.Likes);
        Assert.Equal(original.AudioStreamIndex, target.AudioStreamIndex);
        Assert.Equal(original.SubtitleStreamIndex, target.SubtitleStreamIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoReplacesOnlyTheUnchangedOriginalBaseline(bool editedAfterCapture)
    {
        var baseline = new UserItemData { Key = "original", Played = true, PlayCount = 1 };
        var current = new UserItemData { Key = "indexed-original", Played = true, PlayCount = 1,
            PlaybackPositionTicks = editedAfterCapture ? 500 : 0 };
        var newer = new UserItemData { Key = "moved", Played = true, PlayCount = 2, IsFavorite = true };
        Assert.Equal(!editedAfterCapture, MediaMoveUserData.RestoreState(newer, current, baseline));
        Assert.Equal("indexed-original", current.Key);
        Assert.Equal(editedAfterCapture ? 1 : 2, current.PlayCount);
        Assert.Equal(editedAfterCapture ? 500 : 0, current.PlaybackPositionTicks);
        Assert.Equal(!editedAfterCapture, current.IsFavorite);
    }

    [Fact]
    public void PreservesNewerTargetActivityAndNeverMutatesTheSnapshot()
    {
        var original = new UserItemData { Key = "old", Played = true, PlayCount = 4, IsFavorite = true };
        var target = new UserItemData { Key = "new", PlayCount = 1, PlaybackPositionTicks = 999 };
        Assert.False(MediaMoveUserData.RestoreState(original, target));
        Assert.Equal(999, target.PlaybackPositionTicks);
        Assert.Equal(1, target.PlayCount);
        Assert.False(target.Played);
        Assert.True(original.Played);
        Assert.Equal(4, original.PlayCount);
    }
}
