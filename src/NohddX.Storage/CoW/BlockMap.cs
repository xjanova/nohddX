using NohddX.Core;

namespace NohddX.Storage.CoW;

/// <summary>
/// A bitmap that tracks which blocks have been written to the overlay.
/// Each bit represents one block: 1 = written, 0 = not written.
/// Thread-safe via ReaderWriterLockSlim.
/// </summary>
public sealed class BlockMap : IDisposable
{
    private readonly byte[] _bitmap;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _writtenBlockCount;
    private bool _disposed;

    public long TotalBlocks { get; }

    public long WrittenBlockCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _writtenBlockCount;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public BlockMap(long totalBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBlocks);

        TotalBlocks = totalBlocks;
        int byteCount = (int)((totalBlocks + 7) / 8);
        _bitmap = new byte[byteCount];
    }

    private BlockMap(byte[] bitmap, long totalBlocks, long writtenBlockCount)
    {
        _bitmap = bitmap;
        TotalBlocks = totalBlocks;
        _writtenBlockCount = writtenBlockCount;
    }

    public bool IsBlockWritten(long blockIndex)
    {
        ValidateBlockIndex(blockIndex);

        _lock.EnterReadLock();
        try
        {
            int byteIndex = (int)(blockIndex / 8);
            int bitIndex = (int)(blockIndex % 8);
            return (_bitmap[byteIndex] & (1 << bitIndex)) != 0;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void SetBlockWritten(long blockIndex)
    {
        ValidateBlockIndex(blockIndex);

        _lock.EnterWriteLock();
        try
        {
            int byteIndex = (int)(blockIndex / 8);
            int bitIndex = (int)(blockIndex % 8);
            byte mask = (byte)(1 << bitIndex);

            if ((_bitmap[byteIndex] & mask) == 0)
            {
                _bitmap[byteIndex] |= mask;
                _writtenBlockCount++;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            Array.Clear(_bitmap, 0, _bitmap.Length);
            _writtenBlockCount = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public async Task SaveAsync(string filePath)
    {
        byte[] snapshot;
        long totalBlocks;

        _lock.EnterReadLock();
        try
        {
            snapshot = new byte[_bitmap.Length];
            Buffer.BlockCopy(_bitmap, 0, snapshot, 0, _bitmap.Length);
            totalBlocks = TotalBlocks;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        string? directory = Path.GetDirectoryName(filePath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);

        // Header: 8 bytes for totalBlocks
        byte[] header = BitConverter.GetBytes(totalBlocks);
        await fs.WriteAsync(header);

        // Bitmap data
        await fs.WriteAsync(snapshot);
        await fs.FlushAsync();
    }

    public static async Task<BlockMap> LoadAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Block map file not found.", filePath);
        }

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        // Read header: 8 bytes for totalBlocks
        byte[] header = new byte[8];
        int bytesRead = await fs.ReadAsync(header);
        if (bytesRead < 8)
        {
            throw new InvalidDataException("Block map file is corrupt: header too short.");
        }

        long totalBlocks = BitConverter.ToInt64(header, 0);
        int bitmapLength = (int)((totalBlocks + 7) / 8);

        byte[] bitmap = new byte[bitmapLength];
        int totalRead = 0;
        while (totalRead < bitmapLength)
        {
            int read = await fs.ReadAsync(bitmap.AsMemory(totalRead, bitmapLength - totalRead));
            if (read == 0)
            {
                throw new InvalidDataException("Block map file is corrupt: bitmap data truncated.");
            }
            totalRead += read;
        }

        // Count written blocks
        long writtenCount = 0;
        for (int i = 0; i < bitmapLength; i++)
        {
            writtenCount += BitCount(bitmap[i]);
        }

        // Adjust for any trailing bits beyond totalBlocks
        // (they should be zero, but let's be safe)
        long trailingBits = (bitmapLength * 8) - totalBlocks;
        if (trailingBits > 0)
        {
            byte lastByte = bitmap[bitmapLength - 1];
            for (int bit = (int)(8 - trailingBits); bit < 8; bit++)
            {
                if ((lastByte & (1 << bit)) != 0)
                {
                    writtenCount--;
                }
            }
        }

        return new BlockMap(bitmap, totalBlocks, writtenCount);
    }

    private void ValidateBlockIndex(long blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= TotalBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(blockIndex),
                $"Block index {blockIndex} is out of range [0, {TotalBlocks}).");
        }
    }

    private static int BitCount(byte value)
    {
        int count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _disposed = true;
        }
    }
}
