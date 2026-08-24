using MesIngest.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class FrozenReadCommitConsistencyMatrixTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public FrozenReadCommitConsistencyMatrixTests(
        WebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Ticket01SqlServerFact]
    public Task Frozen_read_matrix_is_commit_consistent_nonblocking_and_cancellation_releases_resources() =>
        new ProjectionCommitAtomicityConcurrencyTests(_factory, _output)
            .RunFrozenReadCommitConsistencyMatrixAsync();
}
