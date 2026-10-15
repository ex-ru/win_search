using System;
using WAI.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WAI.Tests;

public class MultiModelTests
{
    [Fact]
    public void ModelCatalog_ExposesExpectedModels()
    {
        var all = ModelCatalog.All;
        Assert.Equal(new[] { "gemma2", "bge-m3" }, all.Select(model => model.Id));

        var gemma = ModelCatalog.Get("gemma2");
        Assert.Equal("gemma2", gemma.Id);
        Assert.Equal(768, gemma.Dimension);
        Assert.True(gemma.HasMediaProjection);
        Assert.DoesNotContain("2.6B", gemma.GetDisplayName(isRu: true));

        var bge = ModelCatalog.Get("bge-m3");
        Assert.Equal("bge-m3", bge.Id);
        Assert.Equal(1024, bge.Dimension);
        Assert.False(bge.HasMediaProjection);
        Assert.DoesNotContain("568M", bge.GetDisplayName(isRu: true));

        Assert.Contains("Текст + изображения + аудио", gemma.GetDisplayName(isRu: true));
        Assert.Contains("Text + images + audio", gemma.GetDisplayName(isRu: false));
        Assert.Contains("поиска по тексту, изображениям и аудио", gemma.GetDescription(isRu: true));
        Assert.Contains("searching text, images, and audio", gemma.GetDescription(isRu: false));

        Assert.Contains("многоязычный текстовый поиск", bge.GetDisplayName(isRu: true));
        Assert.Contains("multilingual text search", bge.GetDisplayName(isRu: false));
        Assert.Contains("Многоязычная модель текстовых эмбеддингов", bge.GetDescription(isRu: true));
        Assert.Contains("multilingual text embedding model", bge.GetDescription(isRu: false));
    }

    [Fact]
    public void Vectors_Dimension_DynamicSwitchingWorks()
    {
        int initialDim = Vectors.Dimension;
        try
        {
            // Test 1024 dimension (BGE-M3)
            Vectors.Dimension = 1024;
            Assert.Equal(1024, Vectors.Dimension);
            Assert.Equal(2048, Vectors.BytesPerVector);

            var v1024 = new float[1024];
            for (int i = 0; i < 1024; i++) v1024[i] = 0.5f;

            byte[] blob = Vectors.ToHalfBlob(v1024);
            Assert.Equal(2048, blob.Length);

            float[] restored = Vectors.FromHalfBlob(blob);
            Assert.Equal(1024, restored.Length);
            Assert.Equal(0.5f, restored[0], precision: 3);

            // Test 768 dimension (Gemma 2)
            Vectors.Dimension = 768;
            Assert.Equal(768, Vectors.Dimension);
            Assert.Equal(1536, Vectors.BytesPerVector);

            var v768 = new float[768];
            for (int i = 0; i < 768; i++) v768[i] = 0.25f;

            byte[] blob768 = Vectors.ToHalfBlob(v768);
            Assert.Equal(1536, blob768.Length);

            float[] restored768 = Vectors.FromHalfBlob(blob768);
            Assert.Equal(768, restored768.Length);
            Assert.Equal(0.25f, restored768[0], precision: 3);
        }
        finally
        {
            Vectors.Dimension = initialDim;
        }
    }

    [Fact]
    public void VectorStore_Handles1024DimensionCleanly()
    {
        int initialDim = Vectors.Dimension;
        try
        {
            Vectors.Dimension = 1024;
            using var store = VectorStore.Create(5, dimension: 1024);
            Assert.NotNull(store);
            Assert.Equal(1024, store.Dimension);
            Assert.Equal(2048, store.BytesPerRow);

            var v = new float[1024];
            for (int i = 0; i < 1024; i++) v[i] = (float)i / 1024f;
            byte[] blob = Vectors.ToHalfBlob(v);

            store.SetRow(0, blob);

            var dest = new float[1024];
            store.GetRow(0, dest);
            Assert.Equal(v[10], dest[10], precision: 2);
        }
        finally
        {
            Vectors.Dimension = initialDim;
        }
    }

    [Fact]
    public void HnswIndex_HandlesDynamicDimension()
    {
        int initialDim = Vectors.Dimension;
        try
        {
            Vectors.Dimension = 1024;
            var hnsw = new HnswIndex(dimension: 1024);
            Assert.Equal(1024, hnsw.Dimension);

            var v1 = new float[1024];
            v1[0] = 1.0f;
            var v2 = new float[1024];
            v2[1] = 1.0f;

            hnsw.Add(v1);
            hnsw.Add(v2);

            Assert.Equal(2, hnsw.Count);
            var results = hnsw.Search(v1, k: 1, efSearch: 16);
            Assert.Single(results);
            Assert.Equal(0, results[0].Index);
        }
        finally
        {
            Vectors.Dimension = initialDim;
        }
    }
}
