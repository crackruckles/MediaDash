using System;

namespace Jellyfin.Plugin.MediaDash.Metadata;

/// <summary>TVDb rejected the configured API key.</summary>
public sealed class TvdbAuthenticationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TvdbAuthenticationException"/> class.</summary>
    public TvdbAuthenticationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TvdbAuthenticationException"/> class.</summary>
    /// <param name="message">Message.</param>
    public TvdbAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TvdbAuthenticationException"/> class.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Inner exception.</param>
    public TvdbAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
