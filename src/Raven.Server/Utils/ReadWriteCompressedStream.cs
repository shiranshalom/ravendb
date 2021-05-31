using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations.Attachments;
using Sparrow;
using Sparrow.Json;

namespace Raven.Server.Utils
{
    public class ReadWriteCompressedStream : Stream
    {
        private readonly Stream _inner;
        private readonly GZipStream _input, _output;

        public unsafe ReadWriteCompressedStream(Stream inner, JsonOperationContext.MemoryBuffer alreadyOnBuffer)
        {
            Stream innerInput = inner;
            int valid = alreadyOnBuffer.Valid - alreadyOnBuffer.Used;
            if (valid > 0)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(valid);
                fixed (byte* pBuffer = buffer)
                {
                    Memory.Copy(pBuffer, alreadyOnBuffer.Address + alreadyOnBuffer.Used, valid);
                }

                innerInput = new ConcatStream(new ConcatStream.RentedBuffer { Buffer = buffer, Offset = 0, Count = valid }, inner);
                alreadyOnBuffer.Valid = alreadyOnBuffer.Used; // consume all the data from the buffer
            }

            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _input = new GZipStream(new ForwardingStream
            {
                Dest = innerInput
            }, CompressionMode.Decompress, leaveOpen: true);
            _output = new GZipStream(
                new ForwardingStream
                {
                    Dest = inner
                }
                , CompressionMode.Compress, leaveOpen: true);
        }

        private class ForwardingStream : Stream
        {
            public Stream Dest;
            public override bool CanRead { get; } = true;
            public override bool CanSeek { get; } = false;
            public override bool CanWrite { get; } = true;

            public override long Length
            {
                get { throw new NotSupportedException(); }
            }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
                Dest.Flush();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Console.WriteLine(Convert.ToBase64String(buffer, offset, count));
                Dest.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var r = Dest.Read(buffer, offset, count);
                Console.WriteLine($"{r} = Read()");
                return r;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _input?.Dispose();
            _output?.Dispose();
            _inner.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            if (_input != null)
                await _input.DisposeAsync().ConfigureAwait(false);
            if (_output != null)
                await _output.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int r = await _input.ReadAsync(buffer, offset, count, cancellationToken);
            DebugOutput(" < ", new Span<byte>(buffer, offset, r));
            return r;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _output.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            return _input.BeginRead(buffer, offset, count, callback, state);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            return _output.BeginWrite(buffer, offset, count, callback, state);
        }

        public override void CopyTo(Stream destination, int bufferSize)
        {
            _input.CopyTo(destination, bufferSize);
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            return _input.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            return _input.EndRead(asyncResult);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            _output.EndWrite(asyncResult);
        }

        public override void Flush()
        {
            Console.WriteLine("flush");
            _output.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _output.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int r = _input.Read(buffer, offset, count);
            DebugOutput(" < ", new Span<byte>(buffer, offset, r));
            return r;
        }

        public override int Read(Span<byte> buffer)
        {
            var r = _input.Read(buffer);
            DebugOutput(" < ", buffer.Slice(0, r));
            return r;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = new CancellationToken())
        {
            var r = await _input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            DebugOutput(" < ", buffer.Span.Slice(0, r));
            return r;
        }

        public override int ReadByte()
        {
            return _input.ReadByte();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            DebugOutput(" > ", new Span<byte>(buffer, offset, count));

            _output.Write(buffer, offset, count);
        }

        private static void DebugOutput(string prefix, ReadOnlySpan<byte> buffer)
        {
            Console.Write(prefix);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] >= 32 && buffer[i] <= 127)
                {
                    Console.Write((char)buffer[i]);
                }
                else
                {
                    Console.Write("?");
                }
            }

            Console.WriteLine();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            DebugOutput(" > ", buffer);

            _output.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = new CancellationToken())
        {
            DebugOutput(" > ", buffer.Span);
            return _output.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            _output.WriteByte(value);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanTimeout => _inner.CanTimeout;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
        public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }
    }

}
