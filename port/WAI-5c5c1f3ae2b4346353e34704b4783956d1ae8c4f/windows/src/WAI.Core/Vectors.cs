using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Numerics;

namespace WAI.Core;

/// <summary>Vectors are stored as 768 × float16 (1.5 KB each), kept that way in memory
/// too, and scored as float32. Ports Vectors.swift.</summary>
public static class Vectors
{
    private static int _dimension = 768;

    /// <summary>Active vector dimension. Defaults to 768 (Gemma 2), dynamically set per loaded model (e.g. 1024 for BGE-M3 / Perplexity v2).</summary>
    public static int Dimension
    {
        get => _dimension;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Dimension must be positive");
            _dimension = value;
        }
    }

    public static int BytesPerVector => Dimension * 2;

    /// <summary>L2-normalized float vector → float16 blob for the <c>vec</c> column.</summary>
    public static byte[] ToHalfBlob(IReadOnlyList<float> vector, int? dimension = null)
    {
        int dim = dimension ?? Dimension;
        if (vector.Count != dim)
            throw new ArgumentOutOfRangeException(nameof(vector), $"expected {dim} floats, got {vector.Count}");
        int bytesPerVector = dim * 2;
        var bytes = new byte[bytesPerVector];
        for (int i = 0; i < dim; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2),
                BitConverter.HalfToUInt16Bits((Half)vector[i]));
        return bytes;
    }

    public static float[] FromHalfBlob(byte[] blob, int? dimension = null)
    {
        int dim = dimension ?? Dimension;
        int bytesPerVector = dim * 2;
        if (blob.Length != bytesPerVector)
            throw new ArgumentOutOfRangeException(nameof(blob), $"expected {bytesPerVector} bytes, got {blob.Length}");
        var vector = new float[dim];
        for (int i = 0; i < dim; i++)
            vector[i] = (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(i * 2, 2)));
        return vector;
    }

    /// <summary>
    /// Hardware-accelerated (SIMD) dot product. Uses Vector&lt;float&gt; with 4-way unrolling
    /// mapping to AVX2, AVX-512, or ARM64 Neon.
    /// Standard vector dimensions (768, 1024, 512) are multiples of all standard SIMD widths (4, 8, 16).
    /// </summary>
    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = Math.Min(a.Length, b.Length);
        if (len == 0)
            return 0f;

        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count * 4)
        {
            int vCount = Vector<float>.Count;
            int step = vCount * 4;
            int i = 0;
            int unrollLimit = len - step;

            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var acc2 = Vector<float>.Zero;
            var acc3 = Vector<float>.Zero;

            while (i <= unrollLimit)
            {
                acc0 += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
                acc1 += new Vector<float>(a.Slice(i + vCount)) * new Vector<float>(b.Slice(i + vCount));
                acc2 += new Vector<float>(a.Slice(i + vCount * 2)) * new Vector<float>(b.Slice(i + vCount * 2));
                acc3 += new Vector<float>(a.Slice(i + vCount * 3)) * new Vector<float>(b.Slice(i + vCount * 3));
                i += step;
            }

            var totalAcc = (acc0 + acc1) + (acc2 + acc3);
            while (i <= len - vCount)
            {
                totalAcc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
                i += vCount;
            }

            float sum = Vector.Dot(totalAcc, Vector<float>.One);
            while (i < len)
            {
                sum += a[i] * b[i];
                i++;
            }
            return sum;
        }

        double dot = 0;
        for (int i = 0; i < len; i++)
            dot += (double)a[i] * b[i];
        return (float)dot;
    }

    /// <summary>Cosine similarity. Stored vectors are L2-normalized, so for a
    /// normalized query this is the dot product.</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => DotProduct(a, b);
}

/// <summary>The vectors search scores, as stored (float16), plus room to turn
/// <see cref="BlockRows"/> of them into float32 at a time. Backed by a pagefile
/// MemoryMappedFile so releasing it really frees it (the Swift original uses mmap
/// for the same reason: freed malloc blocks stayed in the footprint).</summary>
public sealed class VectorStore : IDisposable
{
    public const int BlockRows = 1024;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly int _dim;
    private readonly Half[] _scratch;
    private bool _disposed;

    public int Rows { get; }
    public int Dimension => _dim;
    public int BytesPerRow => _dim * 2;

    private VectorStore(int rows, MemoryMappedFile file, MemoryMappedViewAccessor accessor, int dim)
    {
        Rows = rows;
        _file = file;
        _accessor = accessor;
        _dim = dim;
        _scratch = new Half[BlockRows * dim];
    }

    public static VectorStore? Create(int rows, int? dimension = null)
    {
        if (rows <= 0)
            return null;
        int dim = dimension ?? Vectors.Dimension;
        int bytesPerVector = dim * 2;
        long capacity = (long)rows * bytesPerVector;
        var file = MemoryMappedFile.CreateNew(null, capacity, MemoryMappedFileAccess.ReadWrite);
        try
        {
            var accessor = file.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
            return new VectorStore(rows, file, accessor, dim);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void SetRow(int row, byte[] blob)
    {
        if (blob.Length != BytesPerRow)
            throw new ArgumentOutOfRangeException(nameof(blob), $"expected {BytesPerRow} bytes for dim {_dim}, got {blob.Length}");
        ObjectDisposedException.ThrowIf(_disposed, this);
        _accessor.WriteArray((long)row * BytesPerRow, blob, 0, blob.Length);
    }

    public void GetRow(int row, Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var halves = _scratch;
        _accessor.ReadArray((long)row * BytesPerRow, halves, 0, _dim);
        for (int i = 0; i < _dim; i++)
            destination[i] = (float)halves[i];
    }

    /// <summary>Converts <paramref name="rowCount"/> rows starting at
    /// <paramref name="startRow"/> into float32 in <paramref name="destination"/>
    /// (room for at least <c>rowCount × Dimension</c> floats).</summary>
    public void CopyBlockAsFloat(int startRow, int rowCount, float[] destination) =>
        CopyBlockAsFloat(startRow, rowCount, destination, _scratch);

    // A partition owns its conversion buffers; the accessor supports independent positional reads.
    internal void CopyBlockAsFloat(int startRow, int rowCount, float[] destination, Half[] scratch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(startRow);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        if (startRow > Rows - rowCount) throw new ArgumentOutOfRangeException(nameof(rowCount));
        int values = checked(rowCount * _dim);
        if (destination.Length < values || scratch.Length < values)
            throw new ArgumentException("Conversion buffer is too small");
        _accessor.ReadArray((long)startRow * BytesPerRow, scratch, 0, values);
        for (int i = 0; i < values; i++) destination[i] = (float)scratch[i];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _accessor.Dispose();
        _file.Dispose();
    }
}

/// <summary>Per-modality normalization: each vector's cosine similarity minus the
/// average of its modality (text, image and audio vectors can sit at slightly
/// different levels for the same query), in standard deviations of all of them
/// together. Ports Searcher.meaning / modalityMeans.</summary>
public static class VectorMath
{
    /// <summary>The average score of each modality for this query. Small groups
    /// (&lt; 20 vectors) take the average of all.</summary>
    public static Dictionary<Modality, double> ModalityMeans(
        IReadOnlyList<float> scores, IReadOnlyList<Modality> modality,
        IReadOnlyList<bool>? setsScale = null)
    {
        var sum = new Dictionary<Modality, double>();
        var count = new Dictionary<Modality, int>();
        for (int row = 0; row < scores.Count; row++)
        {
            if (setsScale is not null && !setsScale[row])
                continue;
            sum[modality[row]] = sum.GetValueOrDefault(modality[row]) + scores[row];
            count[modality[row]] = count.GetValueOrDefault(modality[row]) + 1;
        }
        double total = 0;
        int totalCount = 0;
        foreach (var kv in sum)
        {
            total += kv.Value;
            totalCount += count[kv.Key];
        }
        double all = total / Math.Max(totalCount, 1);
        var output = new Dictionary<Modality, double>();
        foreach (var m in ModalityStrings.All)
        {
            if (count.TryGetValue(m, out int n) && n >= 20)
                output[m] = sum[m] / n;
            else
                output[m] = all;
        }
        return output;
    }

    /// <summary>This query's meaning scores: z-scores of modality-centered cosines.
    /// The scale comes from the vectors in <paramref name="setsScale"/> (all by
    /// default); later pages of long files are scored on it but don't move it.</summary>
    public static double[] Meaning(
        IReadOnlyList<float> scores, IReadOnlyList<Modality> modality,
        IReadOnlyList<bool>? setsScale = null)
    {
        var means = ModalityMeans(scores, modality, setsScale);
        var centered = new double[scores.Count];
        for (int i = 0; i < scores.Count; i++)
            centered[i] = scores[i] - means.GetValueOrDefault(modality[i]);
        double count = 0, sum = 0;
        for (int i = 0; i < centered.Length; i++)
        {
            if (setsScale is not null && !setsScale[i])
                continue;
            count++;
            sum += centered[i];
        }
        double mean = sum / Math.Max(count, 1);
        double squares = 0;
        for (int i = 0; i < centered.Length; i++)
        {
            if (setsScale is not null && !setsScale[i])
                continue;
            squares += (centered[i] - mean) * (centered[i] - mean);
        }
        double deviation = Math.Max(Math.Sqrt(squares / Math.Max(count, 1)), 0.02);
        for (int i = 0; i < centered.Length; i++)
            centered[i] = (centered[i] - mean) / deviation;
        return centered;
    }
}
