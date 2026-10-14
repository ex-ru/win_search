using System;
using System.Linq;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class HnswTests
{
    private static float[] GenerateNormalizedVector(Random rand)
    {
        var v = new float[Vectors.Dimension];
        double sumSq = 0;
        for (int i = 0; i < Vectors.Dimension; i++)
        {
            v[i] = (float)(rand.NextDouble() * 2.0 - 1.0);
            sumSq += v[i] * v[i];
        }

        float norm = (float)Math.Sqrt(sumSq);
        for (int i = 0; i < Vectors.Dimension; i++)
            v[i] /= norm;

        return v;
    }

    [Fact]
    public void HnswIndex_FindsExactNeighbor_WithHighRecall()
    {
        var rand = new Random(1337);
        var hnsw = new HnswIndex(m: 16, efConstruction: 64, seed: 1337);

        const int numVectors = 100;
        var vectors = new float[numVectors][];
        for (int i = 0; i < numVectors; i++)
        {
            vectors[i] = GenerateNormalizedVector(rand);
            hnsw.Add(vectors[i]);
        }

        Assert.Equal(numVectors, hnsw.Count);

        // Query each vector against the index: Top-1 must be the vector itself with similarity ~ 1.0
        int perfectTop1Matches = 0;
        for (int i = 0; i < numVectors; i++)
        {
            var results = hnsw.Search(vectors[i], k: 5, efSearch: 32);
            Assert.NotEmpty(results);

            if (results[0].Index == i && Math.Abs(results[0].Similarity - 1.0f) < 0.001f)
            {
                perfectTop1Matches++;
            }
        }

        // 100% recall on exact vector lookup
        Assert.Equal(numVectors, perfectTop1Matches);
    }

    [Fact]
    public void HnswIndex_EmptyIndex_ReturnsEmptyResults()
    {
        var hnsw = new HnswIndex();
        var q = new float[Vectors.Dimension];
        var results = hnsw.Search(q, k: 5);
        Assert.Empty(results);
    }
}
