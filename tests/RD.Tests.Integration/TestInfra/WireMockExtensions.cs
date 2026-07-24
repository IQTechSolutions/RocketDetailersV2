using WireMock.Logging;

namespace RD.Tests.Integration.TestInfra;

/// <summary>Null-safe accessors for WireMock request log entries.</summary>
internal static class WireMockExtensions
{
    public static string Header(this ILogEntry entry, string name)
    {
        var headers = entry.RequestMessage?.Headers;
        return headers is { } h && h.TryGetValue(name, out var values) ? values.ToString() ?? "" : "";
    }

    public static string Url(this ILogEntry entry) => entry.RequestMessage?.Url ?? "";

    public static string Path(this ILogEntry entry) => entry.RequestMessage?.Path ?? "";
}
