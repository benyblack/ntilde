namespace Ntilde.Mux.Tests.Support;

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {because}.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
