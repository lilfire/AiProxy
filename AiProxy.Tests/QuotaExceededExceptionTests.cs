using AiProxy.Services;

namespace AiProxy.Tests;

[TestClass]
public class QuotaExceededExceptionTests
{
    [TestMethod]
    public void TryParse_null_error_returns_null()
    {
        var result = QuotaExceededException.TryParse(null!);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParse_error_without_quota_returns_null()
    {
        var result = QuotaExceededException.TryParse("some random error");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParse_quota_with_seconds_returns_retry_after_seconds()
    {
        var result = QuotaExceededException.TryParse("Quota exceeded. Resets in 45s");

        Assert.IsNotNull(result);
        Assert.AreEqual(45, result.RetryAfterSeconds);
        Assert.AreEqual("Quota exceeded. Resets in 45s", result.Message);
    }

    [TestMethod]
    public void TryParse_quota_with_minutes_and_seconds_returns_calculated_seconds()
    {
        var result = QuotaExceededException.TryParse("Quota exceeded. Resets in 2m30s");

        Assert.IsNotNull(result);
        Assert.AreEqual(150, result.RetryAfterSeconds);
    }

    [TestMethod]
    public void TryParse_quota_with_hours_minutes_and_seconds_returns_calculated_seconds()
    {
        var result = QuotaExceededException.TryParse("Quota exceeded. Resets in 1h2m3s");

        Assert.IsNotNull(result);
        Assert.AreEqual(3723, result.RetryAfterSeconds);
    }
}
