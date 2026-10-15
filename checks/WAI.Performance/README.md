# WAI search performance check

Run from the repository root with the local .NET 10 SDK:

```powershell
./tools/dotnet/dotnet.exe run --project checks/WAI.Performance -c Release -- --counts 10000,100000 --repetitions 12 --report state/perf/report.json
```

Add `--million` to include one million fragments. The generator streams normalized,
seeded 768-dimensional vectors as fp16 into real IndexStore SQLite tables. Prepared
inserts, reusable buffers, and 1,000-row transactions bound generation memory and WAL
size. Generated indexes and manifests live under ignored `state/perf` by default.
The million-fragment vector payload is approximately 1.43 GiB, plus SQLite overhead;
allow several GiB of disk space. The process stops if private or working memory
exceeds 3 GiB. Do not run alongside another copy against the same state directory.

Three queries are known indexed vectors. During generation, the reference records
the exact ten highest-cosine files across every fp16 row, using the same deterministic
SIMD dot product and per-file maximum as Searcher. Every measured result must retain
the reference order and cosine values; a recall regression returns exit code 1.
The tool reports top-1/top-10 recall, initial Searcher load, warm p50/p95 timings,
process peak working/private bytes, and timings with a background SQLite writer.
Initial verification warms the JIT; cold load means unloaded application vectors,
not a flushed operating-system disk cache. The background writer updates file
metadata every 25 ms; it measures contention with committed writes, not model
inference or extraction throughput. No ANN index is enabled. Large matrices use one parallel partition schedule per
query with separate pooled conversion buffers, capped at eight workers and half
the logical CPU count; small scans remain sequential.

Optional machine-specific gates:

```powershell
./tools/dotnet/dotnet.exe run --project checks/WAI.Performance -c Release -- --max-p95-ms 500 --max-private-mb 2500
```

These thresholds are examples to configure for the target machine, not universal
release requirements. Errors return exit code 2. Reports contain measurements only
from that actual run. An interrupted dataset is never treated as complete; choose
a new `--state` directory or manually remove its generated files before rerunning.
