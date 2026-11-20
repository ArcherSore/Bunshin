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
    private int _closeReason;
    private const int Disposed = 1;
    private const int BrokenFrame = 2;
    private const string BrokenPipeMessage = "IPC 连接因未完成的帧已关闭。";

    public ProtocolConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async Task<WireEnvelope?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        byte[] payload;
        try {
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

            payload = GC.AllocateUninitializedArray<byte>((int)payloadLength);
            await _stream.ReadExactlyAsync(payload, cancellationToken);
        } catch (Exception error) when (Volatile.Read(ref _closeReason) == BrokenFrame && !cancellationToken.IsCancellationRequested
            && error is ObjectDisposedException or OperationCanceledException) {
            throw new IOException(BrokenPipeMessage, error);
        }

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
        ThrowIfClosed();
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
        if (payload.Length == 0 || payload.Length > ProtocolConstants.MaximumFramePayloadBytes) {
            throw new ProtocolException($"IPC frame payload 越界：{payload.Length} bytes。 ");
        }

        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

        await _writeGate.WaitAsync(cancellationToken);
        var writingFrame = false;
        try {
            ThrowIfClosed();
            ThrowIfWritesRetired();
            _writeSequence++;
            onWriteLocked?.Invoke(_writeSequence);
            writingFrame = true;
            await _stream.WriteAsync(prefix, cancellationToken);
            // A length prefix alone cannot deliver a request. Once the payload write starts, delivery is uncertain
            // even if the write or flush subsequently fails.
            ThrowIfClosed();
            ThrowIfWritesRetired();
            cancellationToken.ThrowIfCancellationRequested();
            onSending?.Invoke();
            await _stream.WriteAsync(payload, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        } catch when (writingFrame) {
            if (!_writesRetired) {
                // Even a partial prefix makes the next frame undecodable. Seal before releasing the gate.
                await CloseAsync(BrokenFrame);
            }
            throw;
        } finally {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync() => CloseAsync(Disposed);

    private ValueTask CloseAsync(int reason)
    {
        if (Interlocked.CompareExchange(ref _closeReason, reason, 0) != 0) {
            return ValueTask.CompletedTask;
        }

        // Writers may still be unwinding or waiting to observe the closed stream.
        return _stream.DisposeAsync();
    }

    private void ThrowIfClosed()
    {
        var reason = Volatile.Read(ref _closeReason);
        if (reason == BrokenFrame) {
            throw new IOException(BrokenPipeMessage);
        }
        ObjectDisposedException.ThrowIf(reason == Disposed, this);
    }

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
