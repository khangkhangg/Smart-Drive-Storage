using System.Security.Cryptography;

namespace Miu.Cloud;

/// <summary>Computes a checksum incrementally, in the same form a provider reports it.</summary>
public interface ICloudHasher
{
    void Append(ReadOnlySpan<byte> data);
    string Finish();
}

/// <summary>
/// The checksums providers report, computed locally while uploading so the stored copy can be verified:
/// md5 (Google Drive, S3 single-part ETag), s3etag:&lt;part size&gt; (S3 multipart ETag), dropbox (content_hash),
/// quickxor (OneDrive).
/// </summary>
public static class CloudHashes
{
    public const string Md5 = "md5", Dropbox = "dropbox", QuickXor = "quickxor", S3EtagPrefix = "s3etag:";

    public static ICloudHasher? Create(string? kind) => kind switch
    {
        Md5 => new Md5Hasher(),
        Dropbox => new DropboxHasher(),
        QuickXor => new QuickXorHasher(),
        { } k when k.StartsWith(S3EtagPrefix, StringComparison.Ordinal) && long.TryParse(k[S3EtagPrefix.Length..], out var part) && part > 0 => new S3EtagHasher(part),
        _ => null,
    };

    public static bool Equal(string? kind, string? a, string? b) =>
        a is not null && b is not null &&
        (kind == QuickXor ? string.Equals(a, b, StringComparison.Ordinal) : string.Equals(a.Trim('"'), b.Trim('"'), StringComparison.OrdinalIgnoreCase));

    public static async Task<string?> ComputeAsync(string? kind, Stream s, CancellationToken ct)
    {
        if (Create(kind) is not { } h) return null;
        var buf = new byte[1 << 20];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0) h.Append(buf.AsSpan(0, n));
        return h.Finish();
    }

    private sealed class Md5Hasher : ICloudHasher
    {
        private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        public void Append(ReadOnlySpan<byte> data) => _h.AppendData(data);
        public string Finish() => Convert.ToHexStringLower(_h.GetHashAndReset());
    }

    /// <summary>S3 multipart ETag: md5 of the concatenated part md5s, then "-" and the part count.</summary>
    private sealed class S3EtagHasher(long partSize) : ICloudHasher
    {
        private readonly List<byte> _partHashes = [];
        private IncrementalHash _part = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private long _inPart;
        private int _parts;

        public void Append(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var take = (int)Math.Min(data.Length, partSize - _inPart);
                _part.AppendData(data[..take]);
                _inPart += take;
                data = data[take..];
                if (_inPart == partSize) EndPart();
            }
        }

        private void EndPart()
        {
            _partHashes.AddRange(_part.GetHashAndReset());
            _parts++;
            _inPart = 0;
        }

        public string Finish()
        {
            if (_inPart > 0 || _parts == 0) EndPart();
            if (_parts == 1) return Convert.ToHexStringLower(_partHashes.ToArray()); // a single part: plain md5
            return Convert.ToHexStringLower(MD5.HashData(_partHashes.ToArray())) + "-" + _parts;
        }
    }

    /// <summary>Dropbox content_hash: SHA-256 of the concatenated SHA-256s of each 4 MiB block.</summary>
    private sealed class DropboxHasher : ICloudHasher
    {
        private const int Block = 4 * 1024 * 1024;
        private readonly IncrementalHash _overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash _block = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _inBlock;

        public void Append(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var take = Math.Min(data.Length, Block - _inBlock);
                _block.AppendData(data[..take]);
                _inBlock += take;
                data = data[take..];
                if (_inBlock == Block)
                {
                    _overall.AppendData(_block.GetHashAndReset());
                    _inBlock = 0;
                }
            }
        }

        public string Finish()
        {
            if (_inBlock > 0) _overall.AppendData(_block.GetHashAndReset());
            return Convert.ToHexStringLower(_overall.GetHashAndReset());
        }
    }

    /// <summary>OneDrive's quickXorHash (Microsoft's published reference algorithm), base64.</summary>
    private sealed class QuickXorHasher : ICloudHasher
    {
        private const int BitsInLastCell = 32, Shift = 11, WidthInBits = 160;
        private readonly ulong[] _data = new ulong[(WidthInBits - 1) / 64 + 1];
        private long _length;
        private int _shiftSoFar;

        public void Append(ReadOnlySpan<byte> array)
        {
            var cbSize = array.Length;
            var vectorArrayIndex = _shiftSoFar / 64;
            var vectorOffset = _shiftSoFar % 64;
            var iterations = Math.Min(cbSize, WidthInBits);
            for (var i = 0; i < iterations; i++)
            {
                var isLastCell = vectorArrayIndex == _data.Length - 1;
                var bitsInVectorCell = isLastCell ? BitsInLastCell : 64;
                if (vectorOffset <= bitsInVectorCell - 8)
                {
                    for (var j = i; j < cbSize; j += WidthInBits) _data[vectorArrayIndex] ^= (ulong)array[j] << vectorOffset;
                }
                else
                {
                    var index1 = vectorArrayIndex;
                    var index2 = isLastCell ? 0 : vectorArrayIndex + 1;
                    var low = bitsInVectorCell - vectorOffset;
                    byte xored = 0;
                    for (var j = i; j < cbSize; j += WidthInBits) xored ^= array[j];
                    _data[index1] ^= (ulong)xored << vectorOffset;
                    _data[index2] ^= (ulong)xored >> low;
                }
                vectorOffset += Shift;
                while (vectorOffset >= bitsInVectorCell)
                {
                    vectorArrayIndex = isLastCell ? 0 : vectorArrayIndex + 1;
                    vectorOffset -= bitsInVectorCell;
                }
            }
            _shiftSoFar = (int)((_shiftSoFar + (long)Shift * (cbSize % WidthInBits)) % WidthInBits);
            _length += cbSize;
        }

        public string Finish()
        {
            var rgb = new byte[(WidthInBits - 1) / 8 + 1];
            for (var i = 0; i < _data.Length - 1; i++) BitConverter.GetBytes(_data[i]).CopyTo(rgb, i * 8);
            BitConverter.GetBytes(_data[^1]).AsSpan(0, rgb.Length - (_data.Length - 1) * 8).CopyTo(rgb.AsSpan((_data.Length - 1) * 8));
            var len = BitConverter.GetBytes(_length);
            for (var i = 0; i < len.Length; i++) rgb[WidthInBits / 8 - len.Length + i] ^= len[i];
            return Convert.ToBase64String(rgb);
        }
    }
}
