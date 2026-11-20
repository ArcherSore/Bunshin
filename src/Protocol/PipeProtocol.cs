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
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public Task<Exception?> Closed => _closed.Task;
    public bool IsClosed => Volatile.Read(ref _disposed) != 0;

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

    public async Task WriteAsync(WireEnvelope envelope, CancellationToken cancellationToken, Action? onSending)
    {
        ThrowIfDisposed();
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
        if (payload.Length == 0 || payload.Length > ProtocolConstants.MaximumFramePayloadBytes) {
            throw new ProtocolException($"IPC frame payload 越界：{payload.Length} bytes。 ");
        }

        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

        await _writeGate.WaitAsync(cancellationToken);
        var incomplete = false;
        try {
            ThrowIfDisposed();
            // Stream.WriteAsync returns no byte count on cancellation. A one-byte write
            // distinguishes an untouched stream from a partially written length prefix.
            await _stream.WriteAsync(prefix.AsMemory(0, 1), cancellationToken);
            incomplete = true;
            // Even a writer slow to acknowledge cancellation cannot complete the frame here.
            await _stream.WriteAsync(prefix.AsMemory(1), cancellationToken).AsTask().WaitAsync(cancellationToken);
            // A length prefix alone cannot deliver a request. Once the payload write starts, delivery is uncertain
            // even if the write or flush subsequently fails.
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            onSending?.Invoke();
            await _stream.WriteAsync(payload, cancellationToken);
            incomplete = false;
            await _stream.FlushAsync(cancellationToken);
        } catch (Exception error) when (incomplete) {
            Close(new IOException("IPC 帧未写完整。", error));
            throw;
        } finally {
            _writeGate.Release();
        }
    }

    public void Close(Exception? reason = null)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) {
            try {
                _stream.Dispose();
            } finally {
                _closed.TrySetResult(reason);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
