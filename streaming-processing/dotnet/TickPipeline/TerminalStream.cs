using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;

namespace Benzene.Patterns.Streaming.TickPipeline;

/// <summary>
/// <c>UseStream</c>, marked terminal — which is what it already claims to be.
/// </summary>
/// <remarks>
/// <para>
/// <b>A three-line framework finding.</b> <c>StreamExtensions.UseStream</c> is documented as "a
/// terminal stream-processing step", and it is: nothing runs after it. But it is built on the
/// ordinary <c>Use(name, func)</c> rather than <c>UseTerminal(name, func)</c>, so it is not marked
/// <c>ITerminalMiddleware</c> — and Benzene's own start-up check then refuses to boot a pipeline that
/// ends in it:
/// </para>
/// <code>
/// terminal-middleware: 1 pipeline(s) cannot handle a message:
///   the StreamContext`1 pipeline has no terminal middleware
/// </code>
/// <para>
/// Which is the check doing exactly its job, on a false positive. The fix upstream is one word —
/// <c>UseStream</c> calling <c>UseTerminal</c> instead of <c>Use</c> — and this file is that fix,
/// written locally so the example can boot. Everything else about the step is unchanged.
/// </para>
/// <para>
/// Worth noting how this was found: not by reading the source, but by the start-up check failing on
/// the first run and naming the pipeline and the missing piece. That is the same check that caught a
/// missing terminal middleware in three other examples in this repo.
/// </para>
/// </remarks>
public static class TerminalStreamExtensions
{
    public static IMiddlewarePipelineBuilder<StreamContext<TItem>> UseTerminalStream<TItem>(
        this IMiddlewarePipelineBuilder<StreamContext<TItem>> app,
        Func<StreamContext<TItem>, Task> process)
    {
        return app.UseTerminal("Stream", async (StreamContext<TItem> context, Func<Task> next) =>
        {
            await process(context);
            await next();
        });
    }
}
