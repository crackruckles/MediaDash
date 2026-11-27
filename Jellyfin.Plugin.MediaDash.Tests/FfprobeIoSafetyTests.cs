using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Probing;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class FfprobeIoSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mediadash-io-safety-" + Guid.NewGuid().ToString("N"));
    private readonly MediaDashDb _db;
    private readonly string _path;
    private readonly FileInfo _file;
    private readonly FfprobeService _probe;

    public FfprobeIoSafetyTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "movie.mkv");
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3 });
        _file = new FileInfo(_path);
        _db = new MediaDashDb(Path.Combine(_directory, "review.db"));
        _db.Probes.StoreProbe(_path, _file.Length, _file.LastWriteTimeUtc.Ticks,
            """{"format":{"format_name":"matroska","duration":"120"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264"},{"index":1,"codec_type":"audio","codec_name":"aac"}]}""");
        var encoder = DispatchProxy.Create<IMediaEncoder, IdleCheckTests.ProxyHandler>();
        _probe = new FfprobeService(encoder, _db.Probes, NullLogger<FfprobeService>.Instance);
    }

    [Fact]
    public void AudioDecodeCacheIsIndependentAndInvalidatesOnChangesOrReset()
    {
        var size = _file.Length;
        var modified = _file.LastWriteTimeUtc.Ticks;
        Assert.False(_db.Probes.HasCleanAudioDecode(_path, size, modified, "file:tracks"));
        _db.Probes.StoreCleanAudioDecode(_path, size, modified, "file:tracks");
        Assert.True(_db.Probes.HasCleanAudioDecode(_path, size, modified, "file:tracks"));
        Assert.False(_db.Probes.HasCleanAudioDecode(_path, size + 1, modified, "file:tracks"));
        Assert.False(_db.Probes.HasCleanAudioDecode(_path, size, modified + 1, "file:tracks"));
        Assert.False(_db.Probes.HasCleanAudioDecode(_path, size, modified, "replacement:tracks"));
        Assert.Null(_db.Probes.GetCachedDecode(_path, size, modified));
        _db.ResetScanState();
        Assert.False(_db.Probes.HasCleanAudioDecode(_path, size, modified, "file:tracks"));
    }

    [Fact]
    public void LegacyDecodeTimeoutIsNotAValidCacheHit()
    {
        _db.Probes.StoreDecode(_path, _file.Length, _file.LastWriteTimeUtc.Ticks, "decode check timed out");
        Assert.Null(_db.Probes.GetCachedDecode(_path, _file.Length, _file.LastWriteTimeUtc.Ticks));
    }

    [Fact]
    public async Task LegacyDecodeTimeoutCannotAuthorizeDisposal()
    {
        _db.Probes.StoreDecode(_path, _file.Length, _file.LastWriteTimeUtc.Ticks, "decode check timed out");
        var fixer = (PlayabilityFixer)RuntimeHelpers.GetUninitializedObject(typeof(PlayabilityFixer));
        typeof(PlayabilityFixer).GetField("_ffprobe", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixer, _probe);
        var task = (Task<RevalidationResult>)typeof(PlayabilityFixer)
            .GetMethod("RevalidateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixer, new object[] { new Issue { Path = _path, DetailsJson = """{"reason":"decode-error"}""" }, CancellationToken.None })!;

        Assert.Equal(RevalidationVerdict.Indeterminate, (await task).Verdict);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task MissingDecoderIsNotACleanDecodeAndIsNotCached()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _probe.DecodeCheckAsync(_path, 120, CancellationToken.None));
        Assert.Null(_db.Probes.GetCachedDecode(_path, _file.Length, _file.LastWriteTimeUtc.Ticks));
    }

    [Fact]
    public void ActualDecodeErrorRemainsCached()
    {
        _db.Probes.StoreDecode(_path, _file.Length, _file.LastWriteTimeUtc.Ticks, "Invalid data found when processing input");
        Assert.Equal("Invalid data found when processing input", _db.Probes.GetCachedDecode(_path, _file.Length, _file.LastWriteTimeUtc.Ticks));
    }

    [Theory]
    [InlineData("Permission denied")]
    [InlineData("Input/output error")]
    [InlineData("Decoder (codec example) not found for input stream #0:0")]
    public async Task CachedInfrastructureFailureIsRecheckedInsteadOfFlaggedAsCorruption(string error)
    {
        _db.Probes.StoreDecode(_path, _file.Length, _file.LastWriteTimeUtc.Ticks, error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _probe.DecodeCheckAsync(_path, 120, CancellationToken.None));
    }

    [Fact]
    public async Task CachedStorageReadErrorIsNotAValidMediaProbe()
    {
        _db.Probes.StoreProbe(_path, _file.Length, _file.LastWriteTimeUtc.Ticks, """{"error":{"code":-13,"string":"Permission denied"}}""");
        Assert.Null(await _probe.ProbeAsync(_path, CancellationToken.None));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
