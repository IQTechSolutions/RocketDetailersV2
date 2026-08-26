using System.Text.Json;
using RD.Domain.Policy;

namespace RD.Web.Services;

/// <summary>
/// Host-only command-line boundary for a manual Meta shadow comparison. It
/// exposes no HTTP endpoint and contains no scheduler integration.
/// </summary>
public static class MetaShadowOneShotMode
{
    public const string Switch = "--meta-shadow-compare-once";

    public static bool IsRequested(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));

    public static string[] HostArguments(IEnumerable<string> args) =>
        args.Where(arg => !string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static string SerializeSummary(MetaShadowComparisonReport report)
    {
        var classifications = report.Rows
            .GroupBy(row => row.Classification)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key.ToString(), group => group.Count(), StringComparer.Ordinal);

        return JsonSerializer.Serialize(
            new
            {
                report.From,
                report.AsOf,
                MatchWindowHours = report.MatchWindow.TotalHours,
                Rows = report.Rows.Count,
                Classifications = classifications,
                report.Metrics,
            },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
