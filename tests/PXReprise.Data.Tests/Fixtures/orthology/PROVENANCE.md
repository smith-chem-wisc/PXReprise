# orthology fixture

`views.sql` is copied byte for byte from logs' released snapshot `orthology-compara-116-b63a3331`
(`compara-116.tar`, sha256 `b1d682a51e7e5759cd72719713d4a00a03b07b9ee033b8bdf8f0f54fd7fb747b`). It is written
by mzLib's `OrthologySnapshotWriter` and licensed LGPL-3.0 (logs `LICENSING.md`). The tests build a small
snapshot around it, in the file contract's layout, so the macros run exactly as they do on the release.
