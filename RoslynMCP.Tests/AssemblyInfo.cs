using Xunit;

// The suite exercises process-wide workspace, configuration, cache, and environment state.
// Keep classes serialized until every remaining owner of that state has been made instance-local;
// parallelizing only selected collections allows unrelated tests to invalidate one another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
