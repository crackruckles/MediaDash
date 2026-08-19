namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>The values the Overview cares about: coarse verdict, one-line context, and detailed stats.</summary>
public sealed class SmartHealthResult
{
    /// <summary>Initializes a new instance of the <see cref="SmartHealthResult"/> class.</summary>
    /// <param name="status">Coarse verdict bucket.</param>
    /// <param name="message">Human-readable one-liner for the tooltip.</param>
    public SmartHealthResult(SmartHealth status, string message)
    {
        Status = status;
        Message = message;
    }

    /// <summary>Gets the coarse verdict bucket.</summary>
    public SmartHealth Status { get; }

    /// <summary>Gets the human-readable one-liner for the tooltip.</summary>
    public string Message { get; }

    /// <summary>Gets or sets the drive model/friendly name if the source could report it (e.g., "Lexar SSD NM790 2TB").</summary>
    public string? ModelName { get; set; }

    /// <summary>Gets or sets the current drive temperature in Celsius, when the source reports it.</summary>
    public int? TemperatureCelsius { get; set; }

    /// <summary>Gets or sets the highest temperature ever recorded on this drive in Celsius, when the source reports it.</summary>
    public int? TemperatureMaxCelsius { get; set; }

    /// <summary>Gets or sets the wear percentage (0 = new, 100 = end-of-life) for SSDs, when the source reports it.
    /// Windows reports 0-100 directly; smartctl reports the same via Percentage_Used or Media_Wearout_Indicator.</summary>
    public int? WearPercent { get; set; }

    /// <summary>Gets or sets total hours the drive has been powered on, when the source reports it.</summary>
    public long? PowerOnHours { get; set; }

    /// <summary>Gets or sets uncorrected read-error count (indicates data-loss risk), when the source reports it.</summary>
    public long? ReadErrorsUncorrected { get; set; }

    /// <summary>Gets or sets uncorrected write-error count (indicates data-loss risk), when the source reports it.</summary>
    public long? WriteErrorsUncorrected { get; set; }
}
