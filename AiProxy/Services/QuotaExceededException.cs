using System.Globalization;
using System.Text.RegularExpressions;

namespace AiProxy.Services;

public sealed partial class QuotaExceededException : Exception
{
    public QuotaExceededException(string message, int retryAfterSeconds)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int RetryAfterSeconds { get; }

    public static QuotaExceededException? TryParse(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return null;

        if (!error.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return null;

        var match = RetryAfterRegex().Match(error);

        if (!match.Success)
            return null;

        var hours = ParseGroup(match.Groups["hours"]);
        var minutes = ParseGroup(match.Groups["minutes"]);
        var seconds = ParseGroup(match.Groups["seconds"]);

        var retryAfterSeconds = hours * 3600 + minutes * 60 + seconds;
        return new QuotaExceededException(error.Trim(), retryAfterSeconds);
    }

    private static int ParseGroup(Group group)
    {
        return group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"Resets in\s*(?:(?<hours>\d+)h)?(?:(?<minutes>\d+)m)?(?:(?<seconds>\d+)s)?", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfterRegex();
}
