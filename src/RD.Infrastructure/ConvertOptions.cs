namespace RD.Infrastructure;

/// <summary>
/// Feature flags for the Convert→Bill→Close wedge. Default-safe: the `close` tag write stays OFF
/// until deliberately enabled, and even then the GHL gateway's TestMode (default ON) redirects the
/// write to the test contact until that is also turned off.
/// </summary>
public sealed class ConvertOptions
{
    public const string SectionName = "Convert";

    /// <summary>
    /// When true, the close-write job performs the live `close` tag write on Paid conversions. Default
    /// false — nothing writes to GHL until an operator turns this on after validating the tag path.
    /// </summary>
    public bool CloseTagWriteEnabled { get; set; }
}
