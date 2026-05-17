namespace NohddX.Storage.CoW;

/// <summary>
/// A Stream implementation that reads from a base VHD image and writes to a Copy-on-Write overlay.
/// Reads check the block map to determine whether to serve data from the overlay or the base image.
/// All writes go to the overlay and mark the corresponding blocks as written.
/// </summary>
public sealed class CowDiskStream : Stream
{
    private readonly Stream _baseStream;
    private readonly FileStream _overlayStream;
    private readonly BlockMap _blockMap;
    private readonly int _blockSize;
    private readonly string _blockMapPath;
    private long _position;
    private bool _disposed;

    public CowDiskStream(
        Stream baseStream,
        FileStream overlayStream,
        BlockMap blockMap,
        int blockSize,
        string blockMapPath)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _overlayStream = overlayStream ?? throw new ArgumentNullException(nameof(overlayStream));
        _blockMap = blockMap ?? throw new ArgumentNullException(nameof(blockMap));
        _blockMapPath = blockMapPath ?? throw new ArgumentNullException(nameof(blockMapPath));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        _blockSize = blockSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _baseStream.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);

        int totalRead = 0;
        while (count > 0 && _position < Length)
        {
            long blockIndex = _position / _blockSize;
            int blockOffset = (int)(_position % _blockSize);
            int bytesToRead = Math.Min(count, _blockSize - blockOffset);
            bytesToRead = (int)Math.Min(bytesToRead, Length - _position);

            if (_blockMap.IsBlockWritten(blockIndex))
            {
                // Read from overlay
                _overlayStream.Position = _position;
                int read = _overlayStream.Read(buffer, offset, bytesToRead);
                if (read == 0) break;
                bytesToRead = read;
            }
            else
            {
                // Read from base image
                _baseStream.Position = _position;
                int read = _baseStream.Read(buffer, offset, bytesToRead);
                if (read == 0) break;
                bytesToRead = read;
            }

            _position += bytesToRead;
            offset += bytesToRead;
            count -= bytesToRead;
            totalRead += bytesToRead;
        }

        return totalRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArgs(buffer, offset, count);

        int totalRead = 0;
        while (count > 0 && _position < Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long blockIndex = _position / _blockSize;
            int blockOffset = (int)(_position % _blockSize);
            int bytesToRead = Math.Min(count, _blockSize - blockOffset);
            bytesToRead = (int)Math.Min(bytesToRead, Length - _position);

            if (_blockMap.IsBlockWritten(blockIndex))
            {
                _overlayStream.Position = _position;
                int read = await _overlayStream.ReadAsync(buffer.AsMemory(offset, bytesToRead), cancellationToken);
                if (read == 0) break;
                bytesToRead = read;
            }
            else
            {
                _baseStream.Position = _position;
                int read = await _baseStream.ReadAsync(buffer.AsMemory(offset, bytesToRead), cancellationToken);
                if (read == 0) break;
                bytesToRead = read;
            }

            _position += bytesToRead;
            offset += bytesToRead;
            count -= bytesToRead;
            totalRead += bytesToRead;
        }

        return totalRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArgs(buffer, offset, count);

        while (count > 0)
        {
            long blockIndex = _position / _blockSize;
            int blockOffset = (int)(_position % _blockSize);
            int bytesToWrite = Math.Min(count, _blockSize - blockOffset);

            // If writing a partial block that hasn't been written yet,
            // we need to read the base data for the rest of the block first
            if (blockOffset != 0 || bytesToWrite < _blockSize)
            {
                if (!_blockMap.IsBlockWritten(blockIndex))
                {
                    CopyBaseBlockToOverlay(blockIndex);
                }
            }

            _overlayStream.Position = _position;
            _overlayStream.Write(buffer, offset, bytesToWrite);
            _blockMap.SetBlockWritten(blockIndex);

            _position += bytesToWrite;
            offset += bytesToWrite;
            count -= bytesToWrite;
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArgs(buffer, offset, count);

        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long blockIndex = _position / _blockSize;
            int blockOffset = (int)(_position % _blockSize);
            int bytesToWrite = Math.Min(count, _blockSize - blockOffset);

            if (blockOffset != 0 || bytesToWrite < _blockSize)
            {
                if (!_blockMap.IsBlockWritten(blockIndex))
                {
                    await CopyBaseBlockToOverlayAsync(blockIndex, cancellationToken);
                }
            }

            _overlayStream.Position = _position;
            await _overlayStream.WriteAsync(buffer.AsMemory(offset, bytesToWrite), cancellationToken);
            _blockMap.SetBlockWritten(blockIndex);

            _position += bytesToWrite;
            offset += bytesToWrite;
            count -= bytesToWrite;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        ArgumentOutOfRangeException.ThrowIfNegative(newPosition);
        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("CowDiskStream does not support SetLength.");
    }

    public override void Flush()
    {
        _overlayStream.Flush();
        _blockMap.SaveAsync(_blockMapPath).GetAwaiter().GetResult();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _overlayStream.FlushAsync(cancellationToken);
        await _blockMap.SaveAsync(_blockMapPath);
    }

    private void CopyBaseBlockToOverlay(long blockIndex)
    {
        long blockStart = blockIndex * _blockSize;
        int bytesToCopy = (int)Math.Min(_blockSize, Length - blockStart);
        if (bytesToCopy <= 0) return;

        byte[] blockBuffer = new byte[bytesToCopy];
        _baseStream.Position = blockStart;
        int read = _baseStream.Read(blockBuffer, 0, bytesToCopy);

        if (read > 0)
        {
            _overlayStream.Position = blockStart;
            _overlayStream.Write(blockBuffer, 0, read);
        }
    }

    private async Task CopyBaseBlockToOverlayAsync(long blockIndex, CancellationToken ct)
    {
        long blockStart = blockIndex * _blockSize;
        int bytesToCopy = (int)Math.Min(_blockSize, Length - blockStart);
        if (bytesToCopy <= 0) return;

        byte[] blockBuffer = new byte[bytesToCopy];
        _baseStream.Position = blockStart;
        int read = await _baseStream.ReadAsync(blockBuffer.AsMemory(0, bytesToCopy), ct);

        if (read > 0)
        {
            _overlayStream.Position = blockStart;
            await _overlayStream.WriteAsync(blockBuffer.AsMemory(0, read), ct);
        }
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (offset + count > buffer.Length)
        {
            throw new ArgumentException("The sum of offset and count exceeds the buffer length.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            // Flush block map before closing
            try
            {
                _blockMap.SaveAsync(_blockMapPath).GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort save on dispose
            }

            _overlayStream.Dispose();
            _baseStream.Dispose();
            _blockMap.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
