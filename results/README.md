# Validation evidence

These are selected, machine-specific records, not performance or compatibility guarantees. New local output belongs in ignored `state/` or `TestResults/`. Sanitize paths and document the source/build before committing a report.

- `release-final/`: packaged WAI 1.2.0 CPU checks, native PDF/media checks and the frozen 240-query corpus.
- `release-default-model/`: additional default-model CPU checks.
- `update-apply-final/`: real Velopack update in a disposable portable installation, using a lower-version baseline built from the same source.
- `stabilization-performance.json`: exact vector scans on synthetic indexes, excluding query embedding.
- `dependency-audit.json`: dependency audit at the time of the 1.2.0 validation.
- Other reports and TRX files are historical diagnostics. They can include failed runs and are not acceptance evidence for the current branch.

See [the stabilization report](../docs/STABILIZATION.md) for methodology and limitations, and [the current publication review](../docs/PUBLICATION_REVIEW.md) for remaining release work.
