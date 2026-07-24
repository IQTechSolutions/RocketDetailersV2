namespace RD.Domain;

/// <summary>The inferred payment arrangement: typical amount, cadence in days, and how much to trust it.</summary>
public readonly record struct InferredArrangement(decimal? ExpectedAmount, int? CadenceDays, ArrangementStatus Status);

/// <summary>
/// Infers a client's payment arrangement (typical amount + cadence) from their
/// recurring payment history. There is no stored record of the negotiated terms,
/// so we read them from behaviour — and only TRUST the read when the cadence is
/// regular across enough payments; everything else is flagged for a human.
/// </summary>
public static class ArrangementInference
{
    /// <summary>Regular cadence across ≥ this many payments is required to auto-trust an inference.</summary>
    public const int ConfidentPaymentCount = 3;

    /// <summary>Gap standard deviation must be within this fraction of the mean gap to count as "regular".</summary>
    public const double RegularityTolerance = 0.4;

    public static InferredArrangement Infer(IReadOnlyList<(DateTimeOffset When, decimal Amount)> payments)
    {
        if (payments.Count == 0) return new InferredArrangement(null, null, ArrangementStatus.Unknown);

        var ordered = payments.OrderBy(p => p.When).ToList();
        var amount = Median(ordered.Select(p => p.Amount).ToList());

        // One payment: an amount but no cadence → a human decides.
        if (ordered.Count == 1) return new InferredArrangement(amount, null, ArrangementStatus.NeedsReview);

        var gaps = new List<double>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var days = (ordered[i].When - ordered[i - 1].When).TotalDays;
            if (days > 0) gaps.Add(days);
        }
        if (gaps.Count == 0) return new InferredArrangement(amount, null, ArrangementStatus.NeedsReview);

        var avgGap = gaps.Average();
        var cadence = Canonicalize(avgGap);
        var regular = ordered.Count >= ConfidentPaymentCount && StdDev(gaps, avgGap) <= RegularityTolerance * avgGap;
        return new InferredArrangement(amount, cadence, regular ? ArrangementStatus.Inferred : ArrangementStatus.NeedsReview);
    }

    /// <summary>Snap a mean gap to a canonical cadence (weekly/biweekly/monthly) when close, else round to the day.</summary>
    private static int Canonicalize(double avgDays) => avgDays switch
    {
        <= 10 => 7,
        <= 20 => 14,
        <= 45 => 30,
        _ => (int)System.Math.Round(avgDays),
    };

    private static decimal Median(List<decimal> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        var n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2m;
    }

    private static double StdDev(List<double> xs, double mean)
    {
        if (xs.Count < 2) return 0;
        var variance = xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1);
        return System.Math.Sqrt(variance);
    }
}
