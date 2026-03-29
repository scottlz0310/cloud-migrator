namespace CloudMigrator.Providers.Abstractions;

/// <summary>
/// 一時ファイルを背後に持つ読み取り専用ストリーム。
/// Dispose 時にファイルを自動削除する。
/// </summary>
internal sealed class TempFileBackedReadStream : Stream
{
    private readonly FileStream _inner;
    private readonly string _tempFilePath;
    private int _disposed;

    private TempFileBackedReadStream(FileStream inner, string tempFilePath)
    {
        _inner = inner;
        _tempFilePath = tempFilePath;
    }

    public static Task<TempFileBackedReadStream> CreateAsync(
        string tempFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(
            tempFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        return Task.FromResult(new TempFileBackedReadStream(stream, tempFilePath));
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) =>
        _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        _inner.Seek(offset, origin);

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (disposing)
            _inner.Dispose();

        TryDeleteTempFile();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _inner.DisposeAsync().ConfigureAwait(false);
        TryDeleteTempFile();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void TryDeleteTempFile()
    {
        try
        {
            File.Delete(_tempFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ベストエフォート削除。
        }
    }
}
