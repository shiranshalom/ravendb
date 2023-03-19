using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Sparrow.Binary;

namespace Sparrow.Json
{
    public class AsyncBlittableJsonTextWriter : AbstractBlittableJsonTextWriter, IAsyncDisposable
    {
        private const int MaxBufferedSize = 1024*256;
        private readonly Stream _outputStream;
        private readonly CancellationToken _cancellationToken;
        private MemoryStream _doubleBuffer;
        private Task _previous;

        public AsyncBlittableJsonTextWriter(JsonOperationContext context, Stream stream, CancellationToken cancellationToken = default) : base(context, context.CheckoutMemoryStream())
        {
            _outputStream = stream ?? throw new ArgumentNullException(nameof(stream));
            _cancellationToken = cancellationToken;
            context.CheckoutMemoryStream(out _doubleBuffer);
            _previous = Task.CompletedTask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<int> MaybeOuterFlushAsync()
        {
            var innerStream = _stream as MemoryStream;
            if (innerStream == null)
                ThrowInvalidTypeException(_stream?.GetType());
            if (innerStream.Length * 2 <= innerStream.Capacity && innerStream.Length < MaxBufferedSize) 
                return new ValueTask<int>(0);

            FlushInternal();
            return new ValueTask<int>(OuterFlushAsync());
        }

        public async Task<int> OuterFlushAsync()
        {
            await _previous.ConfigureAwait(false);
            var innerStream = _stream as MemoryStream;
            if (innerStream == null)
                ThrowInvalidTypeException(_stream?.GetType());

            FlushInternal();
            innerStream.TryGetBuffer(out var bytes);
            var bytesCount = bytes.Count;
            if (bytesCount == 0)
                return 0;
            await _outputStream.WriteAsync(bytes.Array, bytes.Offset, bytesCount, _cancellationToken).ConfigureAwait(false);
            innerStream.SetLength(0);
            return bytesCount;
        }

        public async ValueTask WriteStreamAsync(Stream stream, CancellationToken token = default)
        {
            await FlushAsync(token).ConfigureAwait(false);

            while (true)
            {
                _pos = await stream.ReadAsync(_pinnedBuffer.Memory.Memory, token).ConfigureAwait(false);
                if (_pos == 0)
                    break;

                await FlushAsync(token).ConfigureAwait(false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<int> MaybeFlushAsync(CancellationToken token = default)
        {
            var innerStream = _stream as MemoryStream;
            if (innerStream == null)
                ThrowInvalidTypeException(_stream?.GetType());
            if (innerStream.Length * 2 <= innerStream.Capacity && innerStream.Length < MaxBufferedSize)
                return new ValueTask<int>(0);

            FlushInternal(); // this is OK, because inner stream is a MemoryStream
            return FlushAsync(token);
        }

        public async ValueTask<int> FlushAsync(CancellationToken token = default)
        {
            await _previous.ConfigureAwait(false);
         
            var innerStream = _stream as MemoryStream;
            if (innerStream == null)
                ThrowInvalidTypeException(_stream?.GetType());
            FlushInternal();
            var bytesCount = innerStream.Length;
            if (bytesCount == 0)
                return 0;

            _doubleBuffer.SetLength(bytesCount);
            _doubleBuffer.TryGetBuffer(out ArraySegment<byte> buffer);
            innerStream.TryGetBuffer(out ArraySegment<byte> bytes);
            Array.Copy(bytes.Array, bytes.Offset, buffer.Array, buffer.Offset, buffer.Count);
            innerStream.SetLength(0);
            
            _previous = _outputStream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count, token);
            return buffer.Count;
        }

        public async ValueTask DisposeAsync()
        {
            DisposeInternal();

            if (await FlushAsync().ConfigureAwait(false) > 0)
            {
                await _previous.ConfigureAwait(false);
                await _outputStream.FlushAsync().ConfigureAwait(false);
            }

            _context.ReturnMemoryStream((MemoryStream)_stream);
        }

        private void ThrowInvalidTypeException(Type typeOfStream)
        {
            throw new ArgumentException($"Expected stream to be MemoryStream, but got {(typeOfStream == null ? "null" : typeOfStream.ToString())}.");
        }
    }
}
