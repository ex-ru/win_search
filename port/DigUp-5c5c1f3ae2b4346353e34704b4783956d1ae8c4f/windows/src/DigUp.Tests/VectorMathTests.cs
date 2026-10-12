using System;
using System.Numerics;
using DigUp.Core;
using Xunit;

namespace DigUp.Tests;

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
}
