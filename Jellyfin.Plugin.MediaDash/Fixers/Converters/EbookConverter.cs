using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Scanners;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Shells out to Calibre's <c>ebook-convert</c> CLI. Handles MOBI/AZW/AZW3/DjVu and Calibre-supported
/// office formats (TXT/RTF/DOC/DOCX/FB2/LIT/LRF/PDB/HTMLZ/CHM). Target format is EPUB unless the row
/// picks something else (DjVu targets PDF) or the user override <see cref="PluginConfiguration.EbookPreferredTarget"/>
/// pins one.
/// </summary>
public sealed class EbookConverter : IFormatConverter
{
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".azw", ".azw3", ".djvu",
        ".txt", ".rtf", ".doc", ".docx",
        ".fb2", ".lit", ".lrf", ".pdb", ".htmlz", ".chm"
    };

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    private readonly CalibreToolchain _toolchain;
    private readonly IPluginConfigurationProvider _config;
    private readonly ILogger<EbookConverter> _logger;

    /// <summary>Initializes a new instance of the <see cref="EbookConverter"/> class.</summary>
    /// <param name="toolchain">Calibre toolchain resolver.</param>
    /// <param name="config">Config snapshot provider.</param>
    /// <param name="logger">The logger.</param>
    public EbookConverter(CalibreToolchain toolchain, IPluginConfigurationProvider config, ILogger<EbookConverter> logger)
    {
        _toolchain = toolchain;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "ebook";

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Ebook;

    /// <inheritdoc />
    public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; } = new HashSet<CollectionType?> { CollectionType.books };

    /// <inheritdoc />
    public ConverterAvailability Availability => _toolchain.IsAvailable ? ConverterAvailability.Ready : ConverterAvailability.Unavailable;

    /// <inheritdoc />
    public bool CanConvert(string sourceExtension) => Handled.Contains(sourceExtension);

    /// <inheritdoc />
    public async Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken)
    {
        var cfg = _config.GetSnapshot();
        return await _toolchain.EnsureAsync(cfg.CalibreOverridePath, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var readiness = await EnsureToolchainAsync(cancellationToken).ConfigureAwait(false);
        if (readiness != ConverterAvailability.Ready || _toolchain.EbookConvertPath is null)
        {
            return new ConversionResult(false, null, "Calibre's ebook-convert is not available on this server. Install Calibre or set the override path in Settings.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagedTargetPath)!);

            var psi = new ProcessStartInfo
            {
                FileName = _toolchain.EbookConvertPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add(stagedTargetPath);
            if (Path.GetExtension(sourcePath).Equals(".mobi", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(stagedTargetPath).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            {
                // Calibre can preserve MOBI anchors directly under the body, which EPUB 2 disallows.
                psi.ArgumentList.Add("--epub-version");
                psi.ArgumentList.Add("3");
            }

            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            var stdout = new StringBuilder();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.AppendLine(e.Data);
                }
            };

            if (!proc.Start())
            {
                return new ConversionResult(false, null, "ebook-convert failed to start.");
            }

            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConversionTimeout);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(proc);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                return new ConversionResult(false, null, "ebook-convert timed out after " + ConversionTimeout.TotalMinutes + " minutes.");
            }

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("ebook-convert failed (exit={Exit}) for {Source}: {Stderr}", proc.ExitCode, sourcePath, stderr);
                return new ConversionResult(false, null, "ebook-convert exited " + proc.ExitCode + ".", stderr.ToString());
            }

            if (!File.Exists(stagedTargetPath) || new FileInfo(stagedTargetPath).Length == 0)
            {
                return new ConversionResult(false, null, "ebook-convert produced no output.", stdout.ToString());
            }

            return new ConversionResult(true, stagedTargetPath, string.Empty);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(stagedTargetPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ebook conversion crashed: {Source} -> {Target}", sourcePath, stagedTargetPath);
            SafeDelete(stagedTargetPath);
            return new ConversionResult(false, null, "Ebook conversion crashed: " + ex.Message, ex.ToString());
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
