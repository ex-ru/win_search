using System;
using System.Numerics;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class VectorMathTests
{
    [Fact]
    public void DotProduct_MatchesNaiveScalarCalculation()
    {
        var rand = new Random(42);
        var a = new float[Vectors.Dimension];
        var b = new float[Vectors.Dimension];

        for (int i = 0; i < Vectors.Dimension; i++)
        {
            a[i] = (float)(rand.NextDouble() * 2.0 - 1.0);
            b[i] = (float)(rand.NextDouble() * 2.0 - 1.0);
        }

        double scalarExpected = 0;
        for (int i = 0; i < Vectors.Dimension; i++)
            scalarExpected += (double)a[i] * b[i];

        float simdActual = Vectors.DotProduct(a, b);

        Assert.Equal((float)scalarExpected, simdActual, precision: 4);
    }

    [Fact]
    public void DotProduct_IdenticalNormalizedVector_YieldsOne()
    {
        var v = new float[Vectors.Dimension];
        for (int i = 0; i < Vectors.Dimension; i++)
            v[i] = 1.0f / (float)Math.Sqrt(Vectors.Dimension);

        float result = Vectors.DotProduct(v, v);
        Assert.True(Math.Abs(result - 1.0f) < 0.001f, $"Expected close to 1.0, got {result}");
    }

    [Fact]
    public void DotProduct_OrthogonalVectors_YieldsZero()
    {
        var a = new float[Vectors.Dimension];
        var b = new float[Vectors.Dimension];

        // Alternating entries
        for (int i = 0; i < Vectors.Dimension; i++)
        {
            if (i % 2 == 0) a[i] = 1.0f;
            else b[i] = 1.0f;
        }

        float result = Vectors.DotProduct(a, b);
        Assert.Equal(0f, result);
    }

    [Fact]
    public void HalfBlob_RoundtripConversion_PreservesValues()
    {
        var original = new float[Vectors.Dimension];
        for (int i = 0; i < Vectors.Dimension; i++)
            original[i] = (float)Math.Sin(i * 0.1);

        byte[] blob = Vectors.ToHalfBlob(original);
        Assert.Equal(Vectors.BytesPerVector, blob.Length);

        float[] restored = Vectors.FromHalfBlob(blob);
        Assert.Equal(Vectors.Dimension, restored.Length);

        for (int i = 0; i < Vectors.Dimension; i++)
        {
            // float16 precision check (epsilon 0.01)
            Assert.True(Math.Abs(original[i] - restored[i]) < 0.01f, 
                $"Mismatch at index {i}: original {original[i]}, restored {restored[i]}");
        }
    }

    [Fact]
    public void VectorStore_LargeBlock_ParallelScoringMatchesScalar()
    {
        const int rowCount = 200; // Triggers the >= 128 parallel scoring branch
        using var store = VectorStore.Create(rowCount);
        Assert.NotNull(store);

        var rand = new Random(12345);
        var q = new float[Vectors.Dimension];
        for (int i = 0; i < Vectors.Dimension; i++)
            q[i] = (float)(rand.NextDouble() * 2.0 - 1.0);

        var expectedScores = new float[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            var rowVec = new float[Vectors.Dimension];
            for (int i = 0; i < Vectors.Dimension; i++)
                rowVec[i] = (float)(rand.NextDouble() * 2.0 - 1.0);

            store.SetRow(r, Vectors.ToHalfBlob(rowVec));

            // Compute expected dot product using half-precision restored values
            var restored = Vectors.FromHalfBlob(Vectors.ToHalfBlob(rowVec));
            expectedScores[r] = Vectors.DotProduct(restored, q);
        }

        var block = new float[VectorStore.BlockRows * Vectors.Dimension];
        store.CopyBlockAsFloat(0, rowCount, block);

        var actualScores = new float[rowCount];
        System.Threading.Tasks.Parallel.For(0, rowCount, r =>
        {
            ReadOnlySpan<float> rowSpan = block.AsSpan(r * Vectors.Dimension, Vectors.Dimension);
            actualScores[r] = Vectors.DotProduct(rowSpan, q.AsSpan(0, Vectors.Dimension));
        });

        for (int r = 0; r < rowCount; r++)
        {
            Assert.Equal(expectedScores[r], actualScores[r], precision: 4);
        }
    }
}
