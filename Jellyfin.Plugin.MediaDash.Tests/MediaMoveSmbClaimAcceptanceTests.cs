using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Trait("Category", "ReportedIssueExternal")]
public sealed class MediaMoveSmbClaimAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "md-owned-smb-claims-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MoveAndRestoreShareAliasedFutureDestinationClaims(bool restoreFirst)
    {
        Assert.True(OperatingSystem.IsWindows(), "This case requires actual loopback SMB.");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(source, "owned movie");
        var destination = Path.Combine(_root, "new folder", "movie.mkv");
        var alias = Alias(destination);
        Assert.True(Directory.Exists(Alias(_root)));
        var database = new MediaDashDb(Path.Combine(_root, "moves.db"));
        var plan = Plan(source, destination);
        var restoreSource = Path.Combine(_root, "bin", "original.mkv");
        if (restoreFirst)
        {
            Assert.NotNull(database.TryBeginRestore("owned bin", null, restoreSource, alias));
            Assert.Null(database.Moves.TryBegin(plan));
        }
        else
        {
            Assert.NotNull(database.Moves.TryBegin(plan));
            Assert.True(database.Moves.HasActiveConflict(alias));
            Assert.Null(database.TryBeginRestore("owned bin", null, restoreSource, alias));
        }

        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
        Assert.Equal("owned movie", File.ReadAllText(source));
    }

    [Fact]
    public async Task ConcurrentAliasedMoveRestoreClaimsHaveExactlyOneOwner()
    {
        Assert.True(OperatingSystem.IsWindows(), "This case requires actual loopback SMB.");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(source, "owned movie");
        var destination = Path.Combine(_root, "new folder", "movie.mkv");
        var database = new MediaDashDb(Path.Combine(_root, "moves.db"));
        var plan = Plan(source, destination);
        var moving = Task.Run(() => database.Moves.TryBegin(plan));
        var restoring = Task.Run(() => database.TryBeginRestore("owned bin", null, Path.Combine(_root, "bin", "original.mkv"), Alias(destination)));
        await Task.WhenAll(moving, restoring);
        Assert.True((await moving is null) != (await restoring is null), "Alias claims must share the same atomic boundary.");
    }

    [Fact]
    public void IndependentMovesCannotReserveOneFutureDestinationThroughDifferentAliases()
    {
        Assert.True(OperatingSystem.IsWindows(), "This case requires actual loopback SMB.");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "first.mkv");
        var other = Path.Combine(_root, "second.mkv");
        File.WriteAllText(source, "first payload");
        File.WriteAllText(other, "second payload");
        var destination = Path.Combine(_root, "new folder", "movie.mkv");
        var database = new MediaDashDb(Path.Combine(_root, "moves.db"));
        Assert.NotNull(database.Moves.TryBegin(Plan(source, destination)));
        Assert.Null(database.Moves.TryBegin(Plan(other, Alias(destination))));
        Assert.NotNull(database.Moves.TryBegin(Plan(other, Path.Combine(_root, "independent folder", "movie.mkv"))));
    }

    private static string Alias(string path)
    {
        var drive = Path.GetPathRoot(path)!;
        Assert.True(drive.Length == 3 && drive[1] == ':');
        return @"\\localhost\" + drive[0] + @"$\" + path[drive.Length..];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopbackSmbAliasCannotAcquireAnotherClaimOnTheSamePhysicalSource(bool directory)
    {
        Assert.True(OperatingSystem.IsWindows(), "This acceptance case requires actual Windows loopback SMB access.");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, directory ? "bundle" : "source.mkv");
        if (directory) Directory.CreateDirectory(source);
        var media = directory ? Path.Combine(source, "primary.mkv") : source;
        File.WriteAllText(media, "owned source bytes");
        if (directory) Directory.CreateDirectory(Path.Combine(source, "empty"));
        var drive = Path.GetPathRoot(source)!;
        Assert.True(drive.Length == 3 && char.IsLetter(drive[0]) && drive[1] == ':');
        var alias = @"\\localhost\" + drive[0] + @"$\" + source[drive.Length..];
        Assert.True(File.Exists(alias) || Directory.Exists(alias), "The owned fixture must be readable through loopback SMB.");
        var first = Plan(source, Path.Combine(_root, "first", "source.mkv"));
        var second = Plan(alias, Path.Combine(_root, "second", "source.mkv"));
        Assert.Equal(first.Entries[0].Inventory[0].Volume, second.Entries[0].Inventory[0].Volume);
        Assert.Equal(first.Entries[0].Inventory[0].FileId, second.Entries[0].Inventory[0].FileId);
        var database = new MediaDashDb(Path.Combine(_root, "moves.db"));
        Assert.NotNull(database.Moves.TryBegin(first));
        Assert.True(database.Moves.HasActiveConflict(alias));
        if (directory) Assert.True(database.Moves.HasActiveConflict(Path.Combine(alias, "new-folder", "incoming.mkv")));
        Assert.Null(database.Moves.TryBegin(second));

        var independent = Path.Combine(_root, "independent.mkv");
        File.Copy(media, independent);
        var copy = Plan(independent, Path.Combine(_root, "third", "independent.mkv"));
        var originalPayload = first.Entries[0].Inventory.Single(payload => !payload.IsDirectory);
        Assert.Equal(originalPayload.Sha256, copy.Entries[0].Inventory[0].Sha256);
        Assert.NotEqual(originalPayload.FileId, copy.Entries[0].Inventory[0].FileId);
        Assert.False(database.Moves.HasActiveConflict(independent));
        Assert.NotNull(database.Moves.TryBegin(copy));
        Assert.Equal("owned source bytes", File.ReadAllText(media));
        Assert.Equal("owned source bytes", File.ReadAllText(independent));
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("published")]
    [InlineData("recovery")]
    public void AliasedRecordedSurvivorsRemainReservedAfterJournalProgress(string phase)
    {
        Assert.True(OperatingSystem.IsWindows(), "This acceptance case requires actual Windows loopback SMB access.");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.mkv");
        File.WriteAllText(source, "owned survivor");
        var plan = Plan(source, Path.Combine(_root, "first", "source.mkv"));
        var database = new MediaDashDb(Path.Combine(_root, "moves.db"));
        var operation = database.Moves.TryBegin(plan)!;
        var manifest = MediaMoveManifest.TryRead(operation.ManifestJson, operation.Id)!;
        var step = manifest.Steps[0];
        var survivor = phase == "stage" ? step.Stage : phase == "published" ? plan.Entries[0].Destination : step.Recovery;
        Directory.CreateDirectory(Path.GetDirectoryName(survivor)!);
        if (phase == "recovery") File.Move(source, survivor);
        else File.Copy(source, survivor);
        var inventory = MediaMoveSnapshot.Capture(survivor).Export();
        step = phase switch
        {
            "stage" => step with { Phase = "StageVerified", StageInventory = inventory },
            "published" => step with { Phase = "Published", FinalInventory = inventory },
            _ => step with { Phase = "SourceCaptured", RecoveryInventory = inventory }
        };
        Assert.True(database.Moves.Update(operation.Id, operation.OwnerToken, "Prepared", "Executing", (manifest with { Steps = [step] }).Serialize()));
        var drive = Path.GetPathRoot(survivor)!;
        Assert.True(drive.Length == 3 && char.IsLetter(drive[0]) && drive[1] == ':');
        var alias = @"\\localhost\" + drive[0] + @"$\" + survivor[drive.Length..];
        Assert.True(File.Exists(alias), "The owned survivor must be readable through loopback SMB.");
        Assert.True(database.Moves.HasActiveConflict(alias));
        Assert.Null(database.Moves.TryBegin(Plan(alias, Path.Combine(_root, "second", "survivor.mkv"))));
        Assert.Equal("owned survivor", File.ReadAllText(survivor));
    }

    private static MediaMovePlan Plan(string source, string destination) => new MediaMovePlan
    {
        ItemId = Guid.NewGuid(), Type = IssueType.Ungrouped,
        Identity = MediaMovePlan.Hash("owned identity"), PolicyStamp = MediaMovePlan.Hash("owned policy"),
        Entries = [new(source, destination, Directory.Exists(source), MediaMoveSnapshot.Capture(source).Export())]
    }.Seal();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
