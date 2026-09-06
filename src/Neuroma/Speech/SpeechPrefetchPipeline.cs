namespace Neuroma.Speech;

internal static class SpeechPrefetchPipeline
{
    public static async Task RunAsync<TInput, TOutput>(
        IReadOnlyList<TInput> items,
        Func<TInput, int, CancellationToken, Task<TOutput>> generate,
        Func<TInput, TOutput, int, bool, CancellationToken, Task> play,
        Action<int>? waitingFor,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = pipelineCancellation.Token;
        TOutput ready = await generate(items[0], 0, token).ConfigureAwait(false);
        Task<TOutput>? next = null;

        try
        {
            for (int index = 0; index < items.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                next = index + 1 < items.Count ? generate(items[index + 1], index + 1, token) : null;
                await play(items[index], ready, index, next is not null, token).ConfigureAwait(false);
                if (next is null) break;
                if (!next.IsCompletedSuccessfully) waitingFor?.Invoke(index + 1);
                ready = await next.ConfigureAwait(false);
                next = null;
            }
        }
        catch
        {
            pipelineCancellation.Cancel();
            if (next is not null)
            {
                try { await next.ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }
}
