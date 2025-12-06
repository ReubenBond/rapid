namespace Rapid;

internal static class TaskExtensions
{
    public static async Task<T?> WithDefaultOnException<T>(this Task<T> task)
    {
        await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return task switch
        {
            { IsCompletedSuccessfully: true } => await task.ConfigureAwait(false),
            _ => default
        };
    }
}
