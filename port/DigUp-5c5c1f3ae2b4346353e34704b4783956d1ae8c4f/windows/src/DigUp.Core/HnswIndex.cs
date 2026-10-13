using System;
using System.Collections.Generic;
using System.Linq;

namespace DigUp.Core;

/// <summary>
/// Hierarchical Navigable Small World (HNSW) graph prototype for Approximate Nearest Neighbor (ANN) search.
/// Designed for large collections (> 100,000 vectors) where linear SIMD scan exceeds 30ms latency budget.
/// Employs multi-layer skip-list graph structure with SIMD DotProduct metric.
/// </summary>
public sealed class HnswIndex
{
    private readonly int _m;
    private readonly int _m0;
    private readonly int _efConstruction;
    private readonly double _mL;
    private readonly Random _random;
    private readonly List<float[]> _vectors = new();
    private readonly List<List<List<int>>> _graphs = new(); // nodeIndex -> layer -> list of neighbors
    private int _enterNode = -1;
    private int _maxLayer = -1;
    private readonly object _gate = new();

    private int _dim;

    public int Count => _vectors.Count;
    public int MaxLayer => _maxLayer;
    public int Dimension => _dim > 0 ? _dim : Vectors.Dimension;

    public HnswIndex(int m = 16, int efConstruction = 64, int seed = 42, int dimension = 0)
    {
        _m = m;
        _m0 = 2 * m;
        _efConstruction = efConstruction;
        _mL = 1.0 / Math.Log(m);
        _random = new Random(seed);
        _dim = dimension;
    }

    private int GenerateRandomLayer()
    {
        double r = _random.NextDouble();
        if (r == 0) r = 0.0000001;
        return (int)(-Math.Log(r) * _mL);
    }

    public void Add(float[] vector)
    {
        if (_dim == 0) _dim = vector.Length;
        if (vector.Length != _dim)
            throw new ArgumentException($"Vector dimension must be {_dim}", nameof(vector));

        lock (_gate)
        {
            int newNode = _vectors.Count;
            _vectors.Add(vector);

            int nodeLayer = GenerateRandomLayer();
            var nodeGraph = new List<List<int>>();
            for (int l = 0; l <= nodeLayer; l++)
                nodeGraph.Add(new List<int>());
            _graphs.Add(nodeGraph);

            if (_enterNode == -1)
            {
                _enterNode = newNode;
                _maxLayer = nodeLayer;
                return;
            }

            int currObj = _enterNode;
            float currDist = 1.0f - Vectors.DotProduct(vector, _vectors[currObj]);

            // Phase 1: greedy traversal down to nodeLayer
            for (int l = _maxLayer; l > nodeLayer; l--)
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (int neighbor in _graphs[currObj][l])
                    {
                        float d = 1.0f - Vectors.DotProduct(vector, _vectors[neighbor]);
                        if (d < currDist)
                        {
                            currDist = d;
                            currObj = neighbor;
                            changed = true;
                        }
                    }
                }
            }

            // Phase 2: search and connect neighbors on layers min(nodeLayer, _maxLayer) down to 0
            for (int l = Math.Min(nodeLayer, _maxLayer); l >= 0; l--)
            {
                var candidates = SearchLayer(vector, currObj, _efConstruction, l);
                int mMax = l == 0 ? _m0 : _m;
                var neighbors = SelectNeighbors(candidates, mMax);

                foreach (int neighbor in neighbors)
                {
                    _graphs[newNode][l].Add(neighbor);
                    _graphs[neighbor][l].Add(newNode);

                    // Shrink neighbor's edge list if exceeded capacity
                    if (_graphs[neighbor][l].Count > mMax)
                    {
                        var reselected = _graphs[neighbor][l]
                            .OrderBy(n => 1.0f - Vectors.DotProduct(_vectors[neighbor], _vectors[n]))
                            .Take(mMax)
                            .ToList();
                        _graphs[neighbor][l] = reselected;
                    }
                }

                if (candidates.Count > 0)
                    currObj = candidates[0].Node;
            }

            if (nodeLayer > _maxLayer)
            {
                _maxLayer = nodeLayer;
                _enterNode = newNode;
            }
        }
    }

    public List<(int Index, float Similarity)> Search(float[] query, int k, int efSearch = 64)
    {
        lock (_gate)
        {
            if (_enterNode == -1 || _vectors.Count == 0)
                return new List<(int, float)>();

            int currObj = _enterNode;
            float currDist = 1.0f - Vectors.DotProduct(query, _vectors[currObj]);

            for (int l = _maxLayer; l > 0; l--)
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (int neighbor in _graphs[currObj][l])
                    {
                        float d = 1.0f - Vectors.DotProduct(query, _vectors[neighbor]);
                        if (d < currDist)
                        {
                            currDist = d;
                            currObj = neighbor;
                            changed = true;
                        }
                    }
                }
            }

            var candidates = SearchLayer(query, currObj, Math.Max(efSearch, k), 0);
            return candidates
                .Take(k)
                .Select(c => (c.Node, 1.0f - c.Dist))
                .ToList();
        }
    }

    private List<(int Node, float Dist)> SearchLayer(float[] query, int enterNode, int ef, int layer)
    {
        var visited = new HashSet<int> { enterNode };
        var candidateQueue = new PriorityQueue<int, float>(); // min-heap by distance
        var resultList = new List<(int Node, float Dist)>();

        float d0 = 1.0f - Vectors.DotProduct(query, _vectors[enterNode]);
        candidateQueue.Enqueue(enterNode, d0);
        resultList.Add((enterNode, d0));

        while (candidateQueue.Count > 0)
        {
            candidateQueue.TryDequeue(out int currNode, out float currDist);

            float worstResultDist = resultList[^1].Dist;
            if (currDist > worstResultDist && resultList.Count >= ef)
                break;

            foreach (int neighbor in _graphs[currNode][layer])
            {
                if (visited.Add(neighbor))
                {
                    float d = 1.0f - Vectors.DotProduct(query, _vectors[neighbor]);
                    worstResultDist = resultList[^1].Dist;
                    if (d < worstResultDist || resultList.Count < ef)
                    {
                        candidateQueue.Enqueue(neighbor, d);
                        resultList.Add((neighbor, d));
                        resultList.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                        if (resultList.Count > ef)
                            resultList.RemoveAt(resultList.Count - 1);
                    }
                }
            }
        }

        return resultList;
    }

    private static List<int> SelectNeighbors(List<(int Node, float Dist)> candidates, int m)
    {
        return candidates.Take(m).Select(c => c.Node).ToList();
    }
}
