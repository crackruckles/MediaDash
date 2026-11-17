using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatCompatibilityFixerTests
{
    [Fact]
    public void CanFixReturnsTrueForFormatCompatibility()
    {
        var (fixer, _, _) = Setup();
        Assert.True(fixer.CanFix(IssueType.FormatCompatibility));
        Assert.False(fixer.CanFix(IssueType.Duplicate));
    }

    [Fact]
    public async Task RefusesWhenSourceMissing()
    {
        var (fixer, _, _) = Setup();
        var issue = MakeIssue(@"C:\nonexistent-mediadash-test\file.mobi");
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("missing", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesInMixedLibraryWithoutApproval()
    {
        var source = MakeTempFile(".mobi");
        try
        {
            // StubConverter "ebook" not registered → hits ConverterUnavailable before mixed check.
            // Register it so the test reaches the mixed-library gate.
            var (fixer, _, registry) = Setup();
            registry.Register(new StubConverter("ebook", MediaKind.Ebook, new CollectionType?[] { CollectionType.books }));
            var issue = MakeIssue(source.FullName, mixedLibrary: true);
            // getItemById returns null → isMixed = true
            var result = await fixer.FixAsync(issue, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("mixed", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(source.FullName); } catch { }
        }
    }

    [Fact]
    public async Task DryRunProducesPreview()
    {
        var source = MakeTempFile(".mobi");
        try
        {
            var itemId = Guid.NewGuid();
            var fakeItem = new FakeItem(itemId, source.FullName);

            var (fixer, resolver, registry) = Setup(
                dryRun: true,
                getItemById: id => id == itemId ? fakeItem : null);

            registry.Register(new StubConverter("ebook", MediaKind.Ebook, new CollectionType?[] { CollectionType.books }));
            resolver.Assign(fakeItem, CollectionType.books);

            var issue = MakeIssue(source.FullName, itemId: itemId);
            var result = await fixer.FixAsync(issue, null, CancellationToken.None);
            Assert.True(result.WasDryRun);
            Assert.Contains("Preview", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(source.FullName); } catch { }
        }
    }

    // -- P1 safety bug tests --

    /// <summary>
    /// P1-3: VerifyOutputAsync returns a failure message for a bogus EPUB (random bytes).
    /// Instantiates a real fixer (null seams for VRS/guard/protection) and invokes the instance
    /// method so the behaviour under test is the production code path, not a replica.
    /// </summary>
    [Fact]
    public async Task RefusesOutputWhenProbeFails()
    {
        var bogusEpub = Path.Combine(Path.GetTempPath(), "mediadash-p13-" + Guid.NewGuid() + ".epub");
        try
        {
            File.WriteAllBytes(bogusEpub, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });

            var (fixer, _, _) = Setup();
            var error = await fixer.VerifyOutputAsync(bogusEpub, CancellationToken.None);

            Assert.NotNull(error);
            Assert.Contains("integrity probe", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(bogusEpub); } catch { }
        }
    }

    /// <summary>
    /// P1-1: dry-run flipped on after the user approved a fix must abort the commit. Exercises
    /// the real <see cref="FormatCompatibilityFixer.EvaluateCommitPolicy"/> (external review
    /// feedback: tests used to mirror the gate logic instead of calling the production code).
    /// </summary>
    [Fact]
    public void CommitPolicyRefusesWhenDryRunFlippedOn()
    {
        var decision = FormatCompatibilityFixer.EvaluateCommitPolicy(
            freshConfig: new PluginConfiguration { DryRun = true, ConvertEbooks = true, FormatCompatibilityFixMode = FixMode.Automatic },
            converterKind: MediaKind.Ebook,
            protection: null,
            path: @"C:\lib\book.mobi",
            issueType: IssueType.FormatCompatibility);

        Assert.False(decision.Allowed);
        Assert.Equal(FixReason.TypeDisabled, decision.Reason);
    }

    [Fact]
    public void CommitPolicyRefusesWhenFixModeOff()
    {
        var decision = FormatCompatibilityFixer.EvaluateCommitPolicy(
            freshConfig: new PluginConfiguration { DryRun = false, ConvertEbooks = true, FormatCompatibilityFixMode = FixMode.Off },
            converterKind: MediaKind.Ebook,
            protection: null,
            path: @"C:\lib\book.mobi",
            issueType: IssueType.FormatCompatibility);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void CommitPolicyRefusesWhenPerKindToggleOff()
    {
        var decision = FormatCompatibilityFixer.EvaluateCommitPolicy(
            freshConfig: new PluginConfiguration { DryRun = false, ConvertEbooks = false, FormatCompatibilityFixMode = FixMode.Automatic },
            converterKind: MediaKind.Ebook,
            protection: null,
            path: @"C:\lib\book.mobi",
            issueType: IssueType.FormatCompatibility);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void CommitPolicyAllowsWhenClean()
    {
        var decision = FormatCompatibilityFixer.EvaluateCommitPolicy(
            freshConfig: new PluginConfiguration { DryRun = false, ConvertEbooks = true, FormatCompatibilityFixMode = FixMode.Automatic },
            converterKind: MediaKind.Ebook,
            protection: null,
            path: @"C:\lib\book.mobi",
            issueType: IssueType.FormatCompatibility);

        Assert.True(decision.Allowed);
    }

    /// <summary>External review #1: folder-merge used to overwrite an existing sibling .m4b with
    /// <c>File.Move(..., overwrite: true)</c>. The fixer must refuse before converting when the
    /// destination path already holds a file.</summary>
    [Fact]
    public async Task FolderMergeRefusesWhenDestinationFileExists()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "mediadash-folder-" + Guid.NewGuid());
        var sourceDir = Path.Combine(tmpRoot, "Book");
        Directory.CreateDirectory(sourceDir);
        // ChangeExtension on a folder path produces the sibling path we need to pre-populate.
        var destinationPath = Path.ChangeExtension(sourceDir, ".m4b");
        File.WriteAllBytes(destinationPath, new byte[] { 1, 2, 3 });
        try
        {
            var (fixer, resolver, registry) = Setup();
            registry.Register(new StubConverter("audiobook", MediaKind.Audiobook, new CollectionType?[] { CollectionType.books }));
            // External review #4: unindexed path now re-resolves via ResolveByPath, so the stub
            // needs an explicit mapping — the frozen libraryKind in details is only a tiebreaker.
            resolver.AssignByPath(sourceDir, CollectionType.books);

            var issue = new Issue
            {
                Id = 7,
                Type = IssueType.FormatCompatibility,
                ItemId = Guid.Empty,
                Path = sourceDir,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    kind = "audiobook-folder-merge",
                    converter = "audiobook",
                    targetExt = ".m4b",
                    libraryKind = "books",
                    mixedLibrary = false
                }),
                SuggestedFix = "Merge",
                Status = IssueStatus.Detected
            };

            var result = await fixer.FixAsync(issue, null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("length")]
    [InlineData("mtime")]
    [InlineData("unchanged")]
    public async Task CapturesSourceIdentityBeforeConversion(string change)
    {
        var root = Path.Combine(Path.GetTempPath(), "mediadash-identity-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "book.mobi");
        var destination = Path.ChangeExtension(source, ".epub");
        var recycled = Path.Combine(root, "original.mobi");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
        try
        {
            var replacement = new VerifiedReplacementService(path =>
            {
                File.Move(path, recycled);
                return recycled;
            }, new SystemIOReplacementFileOperations(), logger: null);
            var (fixer, resolver, registry) = Setup(replacement: replacement);
            resolver.AssignByPath(source, CollectionType.books);
            registry.Register(new StubConverter("ebook", MediaKind.Ebook, new CollectionType?[] { CollectionType.books }, (path, staged) =>
            {
                using (var zip = System.IO.Compression.ZipFile.Open(staged, System.IO.Compression.ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("mimetype").Open()))
                {
                    writer.Write("application/epub+zip");
                }

                if (change == "length") File.AppendAllText(path, "changed");
                if (change == "mtime") File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            }));
            var result = await fixer.FixAsync(MakeIssue(source, itemId: Guid.Empty), null, CancellationToken.None);

            Assert.Equal(change == "unchanged", result.Success);
            Assert.Equal(change == "unchanged", File.Exists(destination));
            Assert.Equal(change == "unchanged", File.Exists(recycled));
            if (change != "unchanged")
            {
                Assert.Equal(FixReason.SourceChanged, result.Reason);
                var expected = change == "length" ? new byte[] { 1, 2, 3, 4, 99, 104, 97, 110, 103, 101, 100 } : new byte[] { 1, 2, 3, 4 };
                Assert.Equal(expected, File.ReadAllBytes(source));
                Assert.False(File.Exists(source + ".mediadash.tmp.epub"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InsufficientSpace_RefusesBeforeConverterRuns()
    {
        var source = MakeTempFile(".mobi");
        try
        {
            var (fixer, resolver, registry) = Setup(spaceCheck: (_, multiplier) => multiplier == 2 ? "owned low-space refusal" : null);
            resolver.AssignByPath(source.FullName, CollectionType.books);
            registry.Register(new StubConverter("ebook", MediaKind.Ebook, [CollectionType.books], (_, _) => throw new InvalidOperationException("must not convert")));
            var result = await fixer.FixAsync(MakeIssue(source.FullName), null, default);
            Assert.Equal(FixReason.InsufficientSpace, result.Reason);
            Assert.True(File.Exists(source.FullName));
        }
        finally
        {
            File.Delete(source.FullName);
        }
    }

    [Theory]
    [InlineData("J.R.R. Tolkien - The Hobbit")]
    [InlineData("Book 1.5 - Title")]
    public async Task FolderMerge_DottedName_UsesCompleteName(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "md-folder-name-" + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var destination = folder + ".m4b";
        File.WriteAllBytes(destination, [1, 2, 3]);
        try
        {
            var (fixer, resolver, registry) = Setup();
            registry.Register(new StubConverter("audiobook", MediaKind.Audiobook, [CollectionType.books]));
            resolver.AssignByPath(folder, CollectionType.books);
            var issue = new Issue { Type = IssueType.FormatCompatibility, Path = folder,
                DetailsJson = JsonSerializer.Serialize(new { sourceExt = "", targetExt = ".m4b", converter = "audiobook", kind = "audiobook-folder-merge", mixedLibrary = false }) };
            var result = await fixer.FixAsync(issue, null, default);
            Assert.Equal(FixReason.OutputConflict, result.Reason);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // -- P2 fix tests --

    /// <summary>
    /// P2-4: FixResult carries RecoveryPath and RecyclePath forwarded from ReplacementResult.
    /// Exercises the result properties directly to confirm they round-trip.
    /// </summary>
    [Fact]
    public void PreservesRecoveryAndRecyclePathsOnFailedReplacement()
    {
        var fail = FixResult.Fail(FixReason.RecoveryRequired, "Replacement failed.");
        fail.RecyclePath = "X:\\recycle\\original.mobi";
        fail.RecoveryPath = "Y:\\recovery\\original.epub.mediadash.tmp.epub";

        Assert.Equal("X:\\recycle\\original.mobi", fail.RecyclePath);
        Assert.Equal("Y:\\recovery\\original.epub.mediadash.tmp.epub", fail.RecoveryPath);
        Assert.False(fail.Success);
    }

    /// <summary>
    /// P2-7: EbookPreferredTarget = "pdf" overrides the matrix default (.epub) in the DryRun
    /// preview message. Uses the dry-run path so no real VRS/converter needed.
    /// </summary>
    [Fact]
    public async Task EbookPreferredTargetOverridesMatrixDefault()
    {
        var source = MakeTempFile(".mobi");
        try
        {
            var itemId = Guid.NewGuid();
            var fakeItem = new FakeItem(itemId, source.FullName);

            // dryRun=true so we get a message without needing a real converter/VRS.
            var (fixer, resolver, registry) = Setup(
                dryRun: true,
                ebookPreferredTarget: "pdf",
                getItemById: id => id == itemId ? fakeItem : null);

            registry.Register(new StubConverter("ebook", MediaKind.Ebook, new CollectionType?[] { CollectionType.books }));
            resolver.Assign(fakeItem, CollectionType.books);

            // Issue carries the matrix default: .epub
            var issue = MakeIssue(source.FullName, itemId: itemId);
            var result = await fixer.FixAsync(issue, null, CancellationToken.None);

            Assert.True(result.WasDryRun);
            // Config override should win — message must say "pdf", not "epub".
            Assert.Contains("pdf", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".epub", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(source.FullName); } catch { }
        }
    }

    /// <summary>
    /// P4-2: ImagePreferredTarget is ignored because ImageConverter always emits JPEG bytes
    /// regardless of the stagedTargetPath extension. The dry-run preview must show the matrix
    /// default (.jpg) even when ImagePreferredTarget = "png".
    /// This test locks in the current-limitation behavior documented in FormatCompatibilityFixer.cs.
    /// </summary>
    [Fact]
    public async Task PreferredTargetIgnoredWhenConverterCantHonor()
    {
        var source = MakeTempFile(".heic");
        try
        {
            var itemId = Guid.NewGuid();
            var fakeItem = new FakeItem(itemId, source.FullName);

            // imagePreferredTarget = "png" — but ImageConverter can't honour it.
            var (fixer, resolver, registry) = Setup(
                dryRun: true,
                imagePreferredTarget: "png",
                getItemById: id => id == itemId ? fakeItem : null);

            registry.Register(new StubConverter("image", MediaKind.Image, new CollectionType?[] { CollectionType.homevideos }));
            resolver.Assign(fakeItem, CollectionType.homevideos);

            // Issue carries the matrix default: .jpg (what the scanner would stamp)
            var issue = new Issue
            {
                Id = 99,
                Type = IssueType.FormatCompatibility,
                ItemId = itemId,
                Path = source.FullName,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    sourceExt = ".heic",
                    targetExt = ".jpg",
                    converter = "image",
                    mixedLibrary = false
                }),
                SuggestedFix = "Convert",
                Status = IssueStatus.Detected
            };

            var result = await fixer.FixAsync(issue, null, CancellationToken.None);

            Assert.True(result.WasDryRun);
            // Must show matrix default .jpg — NOT the ignored .png preference.
            Assert.Contains(".jpg", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".png", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(source.FullName); } catch { }
        }
    }

    // -- helpers --

    private static Issue MakeIssue(string path, bool mixedLibrary = false, Guid? itemId = null)
        => new()
        {
            Id = 42,
            Type = IssueType.FormatCompatibility,
            ItemId = itemId ?? Guid.NewGuid(),
            Path = path,
            DetailsJson = JsonSerializer.Serialize(new
            {
                sourceExt = Path.GetExtension(path),
                targetExt = ".epub",
                converter = "ebook",
                mixedLibrary
            }),
            SuggestedFix = "Convert",
            Status = IssueStatus.Detected
        };

    private static (FormatCompatibilityFixer fixer, StubResolver resolver, StubRegistry registry) Setup(
        bool dryRun = false,
        string ebookPreferredTarget = "",
        string imagePreferredTarget = "",
        Func<Guid, BaseItem?>? getItemById = null,
        VerifiedReplacementService? replacement = null,
        Func<string, int, string?>? spaceCheck = null)
    {
        var registry = new StubRegistry();
        var resolver = new StubResolver();
        var config = new StubConfig(dryRun, ebookPreferredTarget, imagePreferredTarget);
        // Guard/protection stay null; the identity tests inject a real replacement service.
        var fixer = new FormatCompatibilityFixer(
            converters: registry,
            resolver: resolver,
            replacement: replacement,
            guard: null,
            protection: null,
            configProvider: config,
            getItemById: getItemById ?? (_ => null),
            libraryManager: null,
            libraryMonitor: null,
            logger: NullLogger<FormatCompatibilityFixer>.Instance,
            spaceCheck: spaceCheck);
        return (fixer, resolver, registry);
    }

    private sealed class StubConfig : IPluginConfigurationProvider
    {
        private readonly bool _dryRun;
        private readonly string _ebookPreferredTarget;
        private readonly string _imagePreferredTarget;
        public StubConfig(bool dryRun, string ebookPreferredTarget = "", string imagePreferredTarget = "")
        {
            _dryRun = dryRun;
            _ebookPreferredTarget = ebookPreferredTarget;
            _imagePreferredTarget = imagePreferredTarget;
        }

        public PluginConfiguration GetSnapshot()
            => new()
            {
                DryRun = _dryRun,
                ConvertEbooks = true,
                ConvertComics = true,
                ConvertImages = true,
                ConvertAudiobooks = true,
                ConvertMusic = true,
                ConvertVideos = true,
                FormatCompatibilityFixMode = FixMode.Automatic,
                EbookPreferredTarget = _ebookPreferredTarget,
                ImagePreferredTarget = _imagePreferredTarget
            };
    }

    private static FileInfo MakeTempFile(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), "mediadash-fixer-" + Guid.NewGuid() + ext);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return new FileInfo(path);
    }


    private sealed class StubConverter : IFormatConverter
    {
        private readonly Action<string, string>? _convert;

        public StubConverter(string key, MediaKind kind, CollectionType?[] allowed, Action<string, string>? convert = null)
        {
            _convert = convert;
            Key = key;
            Kind = kind;
            AllowedLibraryTypes = new HashSet<CollectionType?>(allowed);
        }

        public string Key { get; }
        public MediaKind Kind { get; }
        public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; }
        public ConverterAvailability Availability => ConverterAvailability.Ready;
        public bool CanConvert(string sourceExtension) => true;
        public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken ct) => Task.FromResult(ConverterAvailability.Ready);
        public Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken ct)
        {
            if (_convert is null) File.WriteAllBytes(stagedTargetPath, new byte[] { 5, 6, 7, 8 });
            else _convert(sourcePath, stagedTargetPath);
            return Task.FromResult(new ConversionResult(true, stagedTargetPath, string.Empty));
        }
    }

    private sealed class StubRegistry : IFormatConverterRegistry
    {
        private readonly Dictionary<string, IFormatConverter> _map = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<IFormatConverter> All => _map.Values;
        public IFormatConverter? Get(string key) => _map.TryGetValue(key, out var c) ? c : null;
        public void Register(IFormatConverter c) => _map[c.Key] = c;
    }

    private sealed class StubResolver : ILibraryCollectionResolver
    {
        private readonly Dictionary<BaseItem, CollectionType?> _map = new();
        private readonly Dictionary<string, CollectionType?> _pathMap = new(StringComparer.OrdinalIgnoreCase);
        public void Assign(BaseItem item, CollectionType? type)
        {
            _map[item] = type;
            if (!string.IsNullOrEmpty(item.Path))
            {
                _pathMap[item.Path] = type;
            }
        }

        public void AssignByPath(string path, CollectionType? type) => _pathMap[path] = type;

        public CollectionType? Resolve(BaseItem item) => _map.TryGetValue(item, out var t) ? t : ResolveByPath(item.Path);

        public CollectionType? ResolveByPath(string path) => _pathMap.TryGetValue(path ?? string.Empty, out var t) ? t : null;
    }

    private sealed class FakeItem : BaseItem
    {
        public FakeItem(Guid id, string path) { Id = id; Path = path; }
    }
}
