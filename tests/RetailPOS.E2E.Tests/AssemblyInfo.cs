using Xunit;

// Disable parallel test collection execution for E2E tests to prevent concurrent
// in-process Kestrel servers and SQLite WAL file locks from competing for OS resources.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
