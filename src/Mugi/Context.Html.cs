using System.Buffers;
using System.Runtime.CompilerServices;

namespace Mugi;

public partial class Context
{
    /// <summary>
    /// Writes an HTML response. Interpolated values are escaped; literals are written as-is.
    /// </summary>
    public ValueTask Html([InterpolatedStringHandlerArgument("")] ref HtmlInterpolatedStringHandler html)
    {
        html.Consume();
        try
        {
            EnsureActive();
            var payload = html.WrittenMemory;
            BeginBufferedBody("text/html; charset=utf-8");
            if (SuppressMeasuredBody(payload.Length))
            {
                return ValueTask.CompletedTask;
            }

            _responseWriter.Write(payload.Span);
            return FinishBodyWrite();
        }
        finally
        {
            html.Release();
        }
    }
}
