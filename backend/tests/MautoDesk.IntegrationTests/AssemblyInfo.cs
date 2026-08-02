using Xunit;

// Integration tests share one PostgreSQL database, one process-wide environment,
// and in-process rate-limiter partitions. Running collections in parallel makes
// all three racy: two hosts fight over the same environment variable while a
// third seeds fixtures against the same rows.
//
// Sequential is slower and correct. A test suite that fails intermittently gets
// ignored, and an ignored security suite is worse than none.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
