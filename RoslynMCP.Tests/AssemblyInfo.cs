using Xunit;

// Test classes run in parallel; the ones sharing process-wide state opt out by joining
// SharedState's collection. See SharedState.cs for why that is one collection and not several.
//
// The cap is deliberate rather than "as many as there are cores": these tests load MSBuild
// workspaces and spawn real processes, so they are heavy on memory, file handles and child
// processes rather than on CPU, and oversubscribing turns a fast run into a thrashing one.
[assembly: CollectionBehavior(MaxParallelThreads = 8)]
