
namespace Harmony.IntegrationTests.Infrastructure;

public static class Eventually
{
    public static async Task<T> GetAsync<T>(
        Func<Task<T>> action,
        Func<T, bool> predicate,
        int retries = 60,
        int intervalMs = 50
    )
    {
        T result = default!;
        for (int i = 0; i < retries; i++)
        {
            result = await action();
            if (predicate(result))
                return result;
            await Task.Delay(intervalMs);
        }
        return result;
    }

    public static async Task<IEnumerable<T>> HasAnyAsync<T>(
        Func<Task<IEnumerable<T>>> action,
        int retries = 60,
        int intervalMs = 50
    )
    {
        for (int i = 0; i < retries; i++)
        {
            var result = (await action()).ToList();
            if (result.Any())
                return result;
            await Task.Delay(intervalMs);
        }
        return [];
    }

    public static async Task<IEnumerable<T>> MatchesAsync<T>(
        Func<Task<IEnumerable<T>>> action,
        Func<IEnumerable<T>, bool> predicate,
        int retries = 60,
        int intervalMs = 50
    )
    {
        for (int i = 0; i < retries; i++)
        {
            var result = (await action()).ToList();
            if (predicate(result))
                return result;
            await Task.Delay(intervalMs);
        }
        return [];
    }
}
