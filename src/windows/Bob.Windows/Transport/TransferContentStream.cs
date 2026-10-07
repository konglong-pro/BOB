namespace Bob.Windows.Transport;

/// <summary>HTTP cleanup and control cancellation share one file-stream release.</summary>
internal sealed class TransferContentStream(FileStream file) : Stream
{
    private int _releaseStarted;

    public void FlushToDisk() => file.Flush(flushToDisk: true);
    public override bool CanRead => file.CanRead;
    public override bool CanSeek => file.CanSeek;
    public override bool CanWrite => file.CanWrite;
    public override long Length => file.Length;
    public override long Position { get => file.Position; set => file.Position = value; }
    public override void Flush() => file.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => file.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => file.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        file.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => file.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => file.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        file.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);
    public override void SetLength(long value) => file.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && Interlocked.Exchange(ref _releaseStarted, 1) == 0) { file.Dispose(); }
        }
        finally { base.Dispose(disposing); }
    }

    public override ValueTask DisposeAsync() => Interlocked.Exchange(ref _releaseStarted, 1) == 0
        ? file.DisposeAsync() : ValueTask.CompletedTask;
}
