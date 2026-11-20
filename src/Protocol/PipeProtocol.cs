using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Protocol;

public sealed class ProtocolConnection : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _writeSequence;
    private volatile bool _writesRetired;
    private int _disposed;

    public ProtocolConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async Task<WireEnvelope?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var prefix = new byte[sizeof(uint)];
        var prefixBytesRead = await _stream.ReadAsync(prefix, cancellationToken);
        if (prefixBytesRead == 0) {
            return null;
        }
        await _stream.ReadExactlyAsync(prefix.AsMemory(prefixBytesRead), cancellationToken);

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (payloadLength == 0 || payloadLength > ProtocolConstants.MaximumFramePayloadBytes) {
            throw new ProtocolException($"非法 IPC frame 长度：{payloadLength}。 ");
        }
        var payload = GC.AllocateUninitializedArray<byte>((int)payloadLength);
        await _stream.ReadExactlyAsync(payload, cancellationToken);

        string json;
        try {
            json = StrictUtf8.GetString(payload);
        } catch (DecoderFallbackException exception) {
            throw new ProtocolException("IPC frame 不是合法 UTF-8。", exception);
        }

        try {
            return JsonSerializer.Deserialize<WireEnvelope>(json, ProtocolJson.Options)
                   ?? throw new ProtocolException("IPC envelope 为空。 ");
        } catch (JsonException exception) {
            throw new ProtocolException("IPC envelope 不是合法 JSON。", exception);
        }
    }

    public Task WriteAsync(WireEnvelope envelope, CancellationToken cancellationToken) =>
        WriteAsync(envelope, cancellationToken, null);

    public Task WriteAsync(WireEnvelope envelope, CancellationToken cancellationToken, Action? onSending) =>
        WriteFrameAsync(envelope, cancellationToken, null, onSending);

    // The admitted peer is retiring. Keep reading its final frames until EOF.
    public void RetireWrites() => _writesRetired = true;

    // The callbacks run under the same lock as every other writer on this stream.
    public async Task WriteFrameAsync(WireEnvelope envelope, CancellationToken cancellationToken,
        Action<long>? onWriteLocked, Action? onSending)
    {
        ThrowIfDisposed();
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
        if (payload.Length == 0 || payload.Length > ProtocolConstants.MaximumFramePayloadBytes) {
            throw new ProtocolException($"IPC frame payload 越界：{payload.Length} bytes。 ");
        }

        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

        await _writeGate.WaitAsync(cancellationToken);
        var bytesWritten = 0;
        try {
            ThrowIfDisposed();
            ThrowIfWritesRetired();
            _writeSequence++;
            onWriteLocked?.Invoke(_writeSequence);
            // Stream.WriteAsync has no partial count. Complete one byte separately so a
            // cancelled first write is distinguishable from a failure inside the prefix.
            await _stream.WriteAsync(prefix.AsMemory(0, 1), cancellationToken);
            bytesWritten = 1;
            await _stream.WriteAsync(prefix.AsMemory(1), cancellationToken);
            bytesWritten = prefix.Length;
            // A length prefix alone cannot deliver a request. Once the payload write starts, delivery is uncertain
            // even if the write or flush subsequently fails.
            ThrowIfDisposed();
            ThrowIfWritesRetired();
            cancellationToken.ThrowIfCancellationRequested();
            onSending?.Invoke();
            await _stream.WriteAsync(payload, cancellationToken);
            bytesWritten += payload.Length;
            await _stream.FlushAsync(cancellationToken);
        } finally {
            if (bytesWritten > 0 && bytesWritten < prefix.Length + payload.Length) {
                RetireWrites();
            }
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? _stream.DisposeAsync() : ValueTask.CompletedTask;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ThrowIfWritesRetired()
    {
        if (_writesRetired) {
            throw new IOException("IPC 写端已停止接收请求。");
        }
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
