using System.Buffers;
using Mugi.Json;

namespace Mugi;

public partial class Context
{
    private JsonOptions? _responseJsonOptionsSource;
    private JsonOptions? _responseJsonOptions;
    private LengthOnlyJsonBufferWriter? _lengthOnlyJsonWriter;

    private JsonOptions GetResponseJsonOptions()
    {
        var source = _options.Json;
        if (ReferenceEquals(source, _responseJsonOptionsSource))
        {
            return _responseJsonOptions!;
        }

        var responseOptions = new JsonOptions
        {
            MaxDocumentByteLength = int.MaxValue,
            MaxDepth = source.MaxDepth,
            MaxStringByteLength = int.MaxValue,
            MaxCollectionSize = int.MaxValue,
            MaxNumberDigits = int.MaxValue,
            MaxPooledBufferByteLength = source.MaxPooledBufferByteLength,
            AllowNonFiniteNumbers = source.AllowNonFiniteNumbers,
            CancellationToken = source.CancellationToken,
        };
        _responseJsonOptionsSource = source;
        _responseJsonOptions = responseOptions;
        return responseOptions;
    }

    private void WriteJsonResponse<T>(T value, IJsonCodec<T> codec)
    {
        var options = GetResponseJsonOptions();
        if (!ShouldSuppressBody())
        {
            global::Mugi.Json.Json.Serialize(_responseWriter, value, codec, options);
            return;
        }

        var writer = _lengthOnlyJsonWriter ??= new LengthOnlyJsonBufferWriter();
        writer.Reset(_options.MaxBufferedResponseBytes);
        try
        {
            global::Mugi.Json.Json.Serialize(writer, value, codec, options);
            if (!IsContentLengthForbidden()
                && string.Equals(Req.Method, "HEAD", StringComparison.Ordinal))
            {
                _suppressedBodyLength = writer.WrittenCount;
            }
        }
        finally
        {
            writer.Reset(_options.MaxBufferedResponseBytes);
        }
    }

    private sealed class LengthOnlyJsonBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[]? _scratch;

        internal long WrittenCount { get; private set; }

        public void Advance(int count)
        {
            if (count < 0 || _scratch is null || count > _scratch.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            WrittenCount = checked(WrittenCount + count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _scratch;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _scratch;
        }

        internal void Reset(int maxRetainedBytes)
        {
            WrittenCount = 0;
            if (_scratch is not null && _scratch.Length > maxRetainedBytes)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = null;
            }
        }

        public void Dispose()
        {
            WrittenCount = 0;
            if (_scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = null;
            }
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            if (_scratch is not null && _scratch.Length >= sizeHint)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(sizeHint);
            if (_scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
            }

            _scratch = replacement;
        }
    }
}
