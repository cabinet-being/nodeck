namespace MyApp.Api;

/// <summary>
/// Optional totals for paged lists. Lists keep returning a plain JSON array; when the client
/// asks with <c>include_total=true</c> the number of all matching items is sent in the
/// <c>X-Total-Count</c> header, so existing callers are unaffected.
/// </summary>
public static class Paging
{
    public const string TotalHeader = "X-Total-Count";

    public static bool WantsTotal(string? includeTotal) =>
        includeTotal is not null && (includeTotal == "1" ||
            string.Equals(includeTotal, "true", StringComparison.OrdinalIgnoreCase));

    public static void SetTotal(HttpResponse response, long total) =>
        response.Headers[TotalHeader] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Validates limit/offset; returns an error message or null.</summary>
    public static string? Validate(int? limit, int? offset, int maxLimit = 200) =>
        limit is < 1 || limit > maxLimit || offset is < 0
            ? $"limit must be 1–{maxLimit} and offset must be nonnegative."
            : null;
}
