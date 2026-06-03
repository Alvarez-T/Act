using Xunit;

namespace YFex.Messaging.Tests.Fixtures;

/// <summary>
/// Serializes test classes that share the static <see cref="TestDispatcherFixture.Store"/>.
/// Without this, xUnit's default parallel execution causes flaky test failures when two test
/// classes write to and read from the same static dictionary concurrently.
/// </summary>
[CollectionDefinition("DispatcherTests", DisableParallelization = true)]
public sealed class DispatcherTestsCollection { }
