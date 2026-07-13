namespace TLJExplorer.Core.FileSystem;

/// <summary>
/// A read-only <see cref="Stream"/> that exposes a byte-range window <c>[offset, offset+length)</c> of
/// an underlying file as if it were a standalone, 0-based stream.
/// </summary>
/// <remarks>
/// Used so every file-format converter can read through one abstraction whether the "file" is really a
/// slice of a shared <c>.xarc</c> archive blob, or a standalone file sitting loose on disk (in which case
/// the window simply spans the whole file: <c>offset = 0</c>, <c>length = file length</c>).
/// </remarks>
public sealed class ArchiveWindowStream : Stream
{
    private readonly FileStream _underlying;
    private readonly long _windowOffset;
    private readonly long _windowLength;
    private long _position;

    public ArchiveWindowStream(string underlyingPath, long offset, long length)
    {
        ArgumentException.ThrowIfNullOrEmpty(underlyingPath);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _underlying = new FileStream(underlyingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _windowOffset = offset;
        _windowLength = length;
        _position = 0;

        _underlying.Position = _windowOffset;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _windowLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        long remaining = _windowLength - _position;
        if (remaining <= 0)
            return 0;

        int toRead = (int)Math.Min(count, remaining);
        if (toRead <= 0)
            return 0;

        SyncUnderlyingPosition();
        int read = _underlying.Read(buffer, offset, toRead);
        _position += read;
        return read;
    }

    /// <summary>
    /// Fast per-byte read: overridden to skip the base <see cref="Stream.ReadByte"/> implementation, which
    /// allocates a fresh <c>byte[1]</c> on every call. Decoders like <c>XmgDecoder</c> read hundreds of
    /// thousands of bytes one at a time, so the allocation churn dominated decode time.
    /// </summary>
    public override int ReadByte()
    {
        if (_position >= _windowLength)
            return -1;

        SyncUnderlyingPosition();
        int b = _underlying.ReadByte();
        if (b >= 0)
            _position++;
        return b;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _windowLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _windowLength)
            throw new IOException("Attempted to seek outside the bounds of the archive window stream.");

        _position = target;
        return _position;
    }

    public override void Flush()
    {
        // Read-only stream; nothing to flush.
    }

    public override int Read(Span<byte> buffer)
    {
        long remaining = _windowLength - _position;
        if (remaining <= 0)
            return 0;

        int toRead = (int)Math.Min(buffer.Length, remaining);
        if (toRead <= 0)
            return 0;

        SyncUnderlyingPosition();
        int read = _underlying.Read(buffer[..toRead]);
        _position += read;
        return read;
    }

    /// <summary>
    /// Nudge the underlying <see cref="FileStream"/> to the position this window logically points at, but
    /// only when it's actually drifted -- sequential reads keep both positions in lockstep, so the common
    /// case is a no-op. Skipping the redundant <c>Position</c> setter matters when a decoder hammers this
    /// stream with millions of tiny reads.
    /// </summary>
    private void SyncUnderlyingPosition()
    {
        long target = _windowOffset + _position;
        if (_underlying.Position != target)
            _underlying.Position = target;
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("ArchiveWindowStream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("ArchiveWindowStream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _underlying.Dispose();

        base.Dispose(disposing);
    }
}
