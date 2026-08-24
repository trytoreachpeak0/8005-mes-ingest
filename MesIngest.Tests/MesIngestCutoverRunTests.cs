using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Infrastructure.SqlServer;
using MesIngest.ReferenceConsumer;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

[Collection("Ticket01SqlServer")]
public sealed class MesIngestCutoverRunTests
{
    [Fact]
    public void Tombstone_proof_is_versioned_and_order_independent_for_a_large_set()
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "CutoverSqlTools.ps1");
        var script = $$"""
            . '{{tools}}'
            $rows = [Collections.Generic.List[object]]::new()
            foreach ($index in 0..10000) {
                $rows.Add([pscustomobject]@{
                    KeyToken = Get-CutoverTransportDemandKeyToken `
                        -WorkType "WORK-$($index % 7)" `
                        -Sublot "SL-$index "
                    WorkType = "WORK-$($index % 7)"
                    Sublot = "SL-$index "
                    OriginalSeriesId = "series-$index"
                    ArchivedAt = [DateTimeOffset]::Parse('2026-08-01T00:00:00+00:00').AddSeconds($index)
                    ArchiveConclusion = 'ARCHIVED'
                    TombstoneVersion = 1
                })
            }
            $forward = Get-CutoverTombstoneProof -Tombstones $rows
            $rows.Reverse()
            $reverse = Get-CutoverTombstoneProof -Tombstones $rows
            [pscustomobject]@{ Forward = $forward; Reverse = $reverse } |
                ConvertTo-Json -Depth 5 -Compress
            """;

        var result = RunPowerShell(script);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var forward = json.RootElement.GetProperty("Forward");
        var reverse = json.RootElement.GetProperty("Reverse");
        Assert.Equal("ARCHIVED_DEMAND_KEY_TOMBSTONE_SHA256_V1", forward.GetProperty("AlgorithmVersion").GetString());
        Assert.Equal(
            "TRANSPORT_DEMAND_KEY_SHA256_LENGTH_PREFIXED_V1",
            forward.GetProperty("KeyTokenAlgorithmVersion").GetString());
        Assert.Equal(10_001, forward.GetProperty("Count").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", forward.GetProperty("Sha256").GetString()!);
        Assert.Equal(forward.GetRawText(), reverse.GetRawText());
    }

    [Fact]
    public void Tombstone_proof_rejects_a_key_token_that_does_not_match_its_identity()
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "CutoverSqlTools.ps1");
        var authoritativeToken = TransportDemandKeyIdentity.CreateToken("CUT", "SUBLOT-A");
        var corruptedToken = (authoritativeToken[0] == '0' ? "1" : "0") + authoritativeToken[1..];
        var script = $$"""
            . '{{tools}}'
            $row = [pscustomobject]@{
                KeyToken = '{{corruptedToken}}'; WorkType = 'CUT'; Sublot = 'SUBLOT-A';
                OriginalSeriesId = 'series-a'; ArchivedAt = [DateTimeOffset]::Parse('2026-08-01T00:00:00+00:00');
                ArchiveConclusion = 'ARCHIVED'; TombstoneVersion = 1
            }
            Get-CutoverTombstoneProof -Tombstones @($row) | ConvertTo-Json -Compress
            """;

        var result = RunPowerShell(script);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("CUTOVER_TOMBSTONE_KEY_TOKEN_MISMATCH", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Tombstone_identity_and_proof_match_the_frozen_worked_example()
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "CutoverSqlTools.ps1");
        var script = $$"""
            . '{{tools}}'
            $row = [pscustomobject]@{
                KeyToken = Get-CutoverTransportDemandKeyToken -WorkType 'WORK' -Sublot 'SUB'
                WorkType = 'WORK'; Sublot = 'SUB'; OriginalSeriesId = 'series'
                ArchivedAt = [DateTimeOffset]::Parse('2026-08-01T00:00:00+00:00')
                ArchiveConclusion = 'ARCHIVED'; TombstoneVersion = 1
            }
            [pscustomobject]@{
                KeyToken = $row.KeyToken
                Proof = Get-CutoverTombstoneProof -Tombstones @($row)
            } | ConvertTo-Json -Depth 4 -Compress
            """;

        var result = RunPowerShell(script);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(
            TransportDemandKeyIdentity.CreateToken("WORK", "SUB"),
            json.RootElement.GetProperty("KeyToken").GetString());
        Assert.Equal(
            "0e8ea2c4f6adf9360aa2d63c8fa5021570ba4cc01322a240f01db66f1fd5645d",
            json.RootElement.GetProperty("Proof").GetProperty("Sha256").GetString());
        Assert.Equal(
            "TRANSPORT_DEMAND_KEY_SHA256_LENGTH_PREFIXED_V1",
            json.RootElement.GetProperty("Proof").GetProperty("KeyTokenAlgorithmVersion").GetString());
    }

    [Fact]
    public void Cutover_gate_policy_requires_one_successful_result_per_gate_from_the_same_run()
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "CutoverSqlTools.ps1");
        var script = $$"""
            . '{{tools}}'
            $runId = '11111111-1111-4111-8111-111111111111'
            $required = @(Get-CutoverRequiredGateNames)
            $passing = @($required | ForEach-Object {
                [pscustomobject]@{ Name = $_; CutoverRunId = $runId; Passed = $true; Detail = 'ok' }
            })
            $summary = Assert-CutoverGateSet -CutoverRunId $runId -GateResults $passing

            $failures = [ordered]@{}
            foreach ($mutation in @('wrong-run', 'failed', 'missing', 'duplicate')) {
                $candidate = @($passing | ForEach-Object { $_ | Select-Object * })
                switch ($mutation) {
                    'wrong-run' { $candidate[0].CutoverRunId = '22222222-2222-4222-8222-222222222222' }
                    'failed' { $candidate[0].Passed = $false }
                    'missing' { $candidate = @($candidate | Select-Object -Skip 1) }
                    'duplicate' { $candidate += $candidate[0] }
                }
                try {
                    Assert-CutoverGateSet -CutoverRunId $runId -GateResults $candidate | Out-Null
                    $failures[$mutation] = 'UNEXPECTED_PASS'
                } catch {
                    $failures[$mutation] = $_.Exception.Message
                }
            }
            [pscustomobject]@{ Summary = $summary; Failures = $failures } |
                ConvertTo-Json -Depth 6 -Compress
            """;

        var result = RunPowerShell(script);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var summary = json.RootElement.GetProperty("Summary");
        Assert.Equal(11, summary.GetProperty("Count").GetInt32());
        Assert.Equal(
            "11111111-1111-4111-8111-111111111111",
            summary.GetProperty("CutoverRunId").GetString());
        var failures = json.RootElement.GetProperty("Failures");
        Assert.Contains("CUTOVER_GATE_RUN_ID_MISMATCH", failures.GetProperty("wrong-run").GetString());
        Assert.Contains("CUTOVER_GATE_FAILED", failures.GetProperty("failed").GetString());
        Assert.Contains("CUTOVER_GATE_SET_INCOMPLETE", failures.GetProperty("missing").GetString());
        Assert.Contains("CUTOVER_GATE_DUPLICATE", failures.GetProperty("duplicate").GetString());
    }

    [Fact]
    public void Exact_database_identity_policy_rejects_ambiguous_or_delete_capable_targets()
    {
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "CutoverSqlTools.ps1");
        var script = $$"""
            . '{{tools}}'
            $runId = '33333333-3333-4333-8333-333333333333'
            $epoch = '44444444-4444-4444-8444-444444444444'
            $old = [pscustomobject]@{
                ServerIdentity = 'plant\MSSQLSERVER'; DatabaseName = 'MesIngestOld';
                DatabaseId = 7; IsSystemDatabase = $false; SchemaVersion = 27;
                ContractVersion = 'old-contract'; HistoryEpoch = '55555555-5555-4555-8555-555555555555';
                DataDirectories = @('D:\SqlData'); ForeignSessionCount = 0;
                HasDeletePermission = $false; HasGlobalSessionVisibility = $true
            }
            $new = [pscustomobject]@{
                ServerIdentity = 'plant\MSSQLSERVER'; DatabaseName = 'MesIngestNew';
                DatabaseId = 8; IsSystemDatabase = $false; SchemaVersion = 28;
                ContractVersion = 'new-contract'; HistoryEpoch = $epoch;
                DataDirectories = @('D:\SqlData'); ForeignSessionCount = 1;
                HasDeletePermission = $false; HasGlobalSessionVisibility = $true
            }
            $parameters = @{
                CutoverRunId = $runId; OldIdentity = $old; NewIdentity = $new;
                ExpectedOldDatabaseName = 'MesIngestOld'; ExpectedNewDatabaseName = 'MesIngestNew';
                ExpectedOldSchemaVersion = 27; ExpectedOldContractVersion = 'old-contract';
                ExpectedNewSchemaVersion = 28; ExpectedNewContractVersion = 'new-contract';
                ExpectedHistoryEpoch = $epoch; ExpectedSqlDataDirectory = 'D:\SqlData'
            }
            $passing = @(Assert-CutoverDatabaseIdentityPolicy @parameters)

            $failures = [ordered]@{}
            foreach ($mutation in @(
                'system', 'same-database', 'wrong-contract', 'wrong-directory',
                'old-connection', 'delete-permission', 'blind-session-query')) {
                $oldCopy = $old | Select-Object *
                $newCopy = $new | Select-Object *
                switch ($mutation) {
                    'system' { $oldCopy.IsSystemDatabase = $true }
                    'same-database' { $oldCopy.DatabaseName = 'MesIngestNew'; $oldCopy.DatabaseId = 8 }
                    'wrong-contract' { $newCopy.ContractVersion = 'wrong' }
                    'wrong-directory' { $oldCopy.DataDirectories = @('E:\Other') }
                    'old-connection' { $oldCopy.ForeignSessionCount = 1 }
                    'delete-permission' { $newCopy.HasDeletePermission = $true }
                    'blind-session-query' { $oldCopy.HasGlobalSessionVisibility = $false }
                }
                $candidateParameters = @{} + $parameters
                $candidateParameters.OldIdentity = $oldCopy
                $candidateParameters.NewIdentity = $newCopy
                try {
                    Assert-CutoverDatabaseIdentityPolicy @candidateParameters | Out-Null
                    $failures[$mutation] = 'UNEXPECTED_PASS'
                } catch {
                    $failures[$mutation] = $_.Exception.Message
                }
            }
            [pscustomobject]@{ Gates = $passing; Failures = $failures } |
                ConvertTo-Json -Depth 6 -Compress
            """;

        var result = RunPowerShell(script);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(4, json.RootElement.GetProperty("Gates").GetArrayLength());
        foreach (var failure in json.RootElement.GetProperty("Failures").EnumerateObject())
        {
            Assert.StartsWith("CUTOVER_", failure.Value.GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("UNEXPECTED_PASS", failure.Value.GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Reference_consumer_probe_requires_the_history_epoch_and_excludes_tombstone_keys()
    {
        var epoch = HistoryEpoch.FromGuid(Guid.Parse("66666666-6666-4666-8666-666666666666"));
        using var http = new HttpClient(new CutoverCatalogHandler(epoch))
        {
            BaseAddress = new Uri("http://127.0.0.1:5088"),
        };

        var passed = await CutoverReferenceConsumerProbe.VerifyAsync(http, epoch, new HashSet<string>());

        Assert.Equal(epoch.Value, passed.HistoryEpoch);
        Assert.Equal("commit-3", passed.ProjectionCommitId);
        Assert.Equal(1, passed.ItemCount);
        Assert.True(passed.TombstoneKeysExcluded);

        var forbidden = TransportDemandKeyIdentity.CreateToken("CUT", "SUBLOT-A");
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => CutoverReferenceConsumerProbe.VerifyAsync(http, epoch, new HashSet<string> { forbidden }));
        Assert.StartsWith("CUTOVER_TOMBSTONE_KEY_EXTERNALLY_READABLE", error.Message, StringComparison.Ordinal);

        var wrongEpoch = HistoryEpoch.FromGuid(Guid.Parse("77777777-7777-4777-8777-777777777777"));
        var epochError = await Assert.ThrowsAsync<InvalidDataException>(
            () => CutoverReferenceConsumerProbe.VerifyAsync(http, wrongEpoch, new HashSet<string>()));
        Assert.StartsWith("CUTOVER_REFERENCE_CONSUMER_HISTORY_EPOCH_MISMATCH", epochError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_consumer_probe_command_refuses_an_incomplete_cutover_invocation()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(CutoverReferenceConsumerProbe).Assembly.Location);
        startInfo.ArgumentList.Add("verify-cutover");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Reference consumer probe command timed out.");

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("--base-url", output, StringComparison.Ordinal);
        Assert.Contains("--expected-history-epoch", output, StringComparison.Ordinal);
        Assert.Contains("--forbidden-key-token-file", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cutover_evidence_is_json_markdown_non_overwritable_and_safe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MesIngestCutoverEvidence", Guid.NewGuid().ToString("N"));
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "CutoverSqlTools.ps1");
        var script = $$"""
            . '{{tools}}'
            $evidence = [ordered]@{
                SchemaVersion = 1
                CutoverRunId = '88888888-8888-4888-8888-888888888888'
                Status = 'PASSED_WITHOUT_DELETE_AUTHORIZATION'
                HistoryEpoch = '99999999-9999-4999-8999-999999999999'
                TombstoneProof = [ordered]@{
                    AlgorithmVersion = 'V1'
                    KeyTokenAlgorithmVersion = 'TRANSPORT_DEMAND_KEY_SHA256_LENGTH_PREFIXED_V1'
                    Count = 2
                    Sha256 = ('a' * 64)
                }
                GateCount = 11
                DatabaseDeletion = 'NOT_AUTHORIZED_TICKET_23'
            }
            $first = Write-CutoverEvidence -EvidenceDirectory '{{directory}}' -Evidence $evidence
            try {
                Write-CutoverEvidence -EvidenceDirectory '{{directory}}' -Evidence $evidence | Out-Null
                $second = 'UNEXPECTED_OVERWRITE'
            } catch {
                $second = $_.Exception.Message
            }
            [pscustomobject]@{ First = $first; Second = $second } | ConvertTo-Json -Compress
            """;

        try
        {
            var result = RunPowerShell(script);

            Assert.Equal(0, result.ExitCode);
            using var output = JsonDocument.Parse(result.Output);
            Assert.Contains("CUTOVER_EVIDENCE_EXISTS", output.RootElement.GetProperty("Second").GetString());
            var jsonPath = output.RootElement.GetProperty("First").GetProperty("JsonPath").GetString()!;
            var markdownPath = output.RootElement.GetProperty("First").GetProperty("MarkdownPath").GetString()!;
            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(markdownPath));
            var combined = File.ReadAllText(jsonPath) + File.ReadAllText(markdownPath);
            Assert.Contains("88888888-8888-4888-8888-888888888888", combined, StringComparison.Ordinal);
            Assert.Contains("NOT_AUTHORIZED_TICKET_23", combined, StringComparison.Ordinal);
            Assert.Contains("TRANSPORT_DEMAND_KEY_SHA256_LENGTH_PREFIXED_V1", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkType", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sublot", combined, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void No_delete_cutover_entrypoint_and_reference_consumer_probe_ship_in_the_release_package()
    {
        var cutover = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "Invoke-MesIngestCutoverRun.ps1"));
        Assert.DoesNotContain("DROP DATABASE", cutover, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BACKUP DATABASE", cutover, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RESTORE DATABASE", cutover, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-CutoverDropDatabase", cutover, StringComparison.Ordinal);
        Assert.Contains("NOT_AUTHORIZED_TICKET_23", cutover, StringComparison.Ordinal);
        Assert.Contains("-KeyTokenAlgorithmVersion $tombstoneProof.KeyTokenAlgorithmVersion", cutover, StringComparison.Ordinal);
        Assert.Contains("post-seed high-water", cutover, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-AfterPollTraceSequence $postSeedPollTraceHighWater", cutover, StringComparison.Ordinal);
        Assert.Contains("$finalOldIdentity", cutover, StringComparison.Ordinal);
        Assert.True(
            cutover.Split("Assert-OldCutoverHostStopped", StringSplitOptions.None).Length - 1 >= 3,
            "The entry point must define and invoke the old-Host gate both initially and immediately before evidence.");

        var publish = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "Publish-MesIngest.ps1"));
        Assert.Contains("Invoke-MesIngestCutoverRun.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("MesIngest.ReferenceConsumer.csproj", publish, StringComparison.Ordinal);
        Assert.Contains("reference-consumer", publish, StringComparison.Ordinal);

        var validator = File.ReadAllText(Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "Test-ReleasePackage.ps1"));
        Assert.Contains("Invoke-MesIngestCutoverRun.ps1", validator, StringComparison.Ordinal);
        Assert.Contains("reference-consumer\\MesIngest.ReferenceConsumer.exe", validator, StringComparison.Ordinal);
        Assert.Contains("validation\\Invoke-ScaleAndQueryEvidence.ps1", validator, StringComparison.Ordinal);
    }

    [Ticket01SqlServerFact]
    public async Task Tombstone_seed_is_atomic_idempotent_and_rejects_a_conflicting_identity()
    {
        await using var oldDatabase = await Ticket01SqlServerDatabase.CreateAsync();
        await using var newDatabase = await Ticket01SqlServerDatabase.CreateAsync();
        var oldProjection = new SqlServerMesIngestProjection(oldDatabase.ConnectionString);
        var newProjection = new SqlServerMesIngestProjection(newDatabase.ConnectionString);
        await oldProjection.BeginHostSessionAsync();
        await newProjection.BeginHostSessionAsync();
        var expectedHistoryEpoch = await ReadHistoryEpochAsync(newDatabase.ConnectionString);
        var tombstones = Enumerable.Range(1, 3)
            .Select(index => new TombstoneFixture(
                TransportDemandKeyIdentity.CreateToken($"WORK-{index}", $"SL-{index}"),
                $"WORK-{index}",
                $"SL-{index}",
                $"series-{index}",
                new DateTimeOffset(2026, 8, index, 1, 2, 3, TimeSpan.Zero)))
            .ToArray();
        await InsertTombstonesAsync(oldDatabase.ConnectionString, tombstones);
        var environment = new Dictionary<string, string?>
        {
            ["CUTOVER_TEST_OLD_SQLSERVER"] = ToSystemDataConnectionString(oldDatabase.ConnectionString),
            ["CUTOVER_TEST_NEW_SQLSERVER"] = ToSystemDataConnectionString(newDatabase.ConnectionString),
            ["CUTOVER_TEST_HISTORY_EPOCH"] = expectedHistoryEpoch,
        };
        var tools = Path.Combine(
            RepositoryPaths.CSharpRoot,
            "pack",
            "cutover",
            "CutoverSqlTools.ps1");
        var seedScript = $$"""
            . '{{tools}}'
            $old = [System.Data.SqlClient.SqlConnection]::new($env:CUTOVER_TEST_OLD_SQLSERVER)
            $new = [System.Data.SqlClient.SqlConnection]::new($env:CUTOVER_TEST_NEW_SQLSERVER)
            try {
                $old.Open(); $new.Open()
                $rows = @(Get-CutoverArchivedTombstones -Connection $old)
                $exportProof = Get-CutoverTombstoneProof -Tombstones $rows
                $first = Invoke-CutoverTombstoneSeed -Connection $new -Tombstones $rows -ExpectedProof $exportProof
                $second = Invoke-CutoverTombstoneSeed -Connection $new -Tombstones $rows -ExpectedProof $exportProof
                $importProof = Get-CutoverTombstoneProof -Tombstones @(
                    Get-CutoverArchivedTombstones -Connection $new)
                $newIdentity = Get-CutoverDatabaseIdentityEvidence -Connection $new
                [pscustomobject]@{
                    ExportProof = $exportProof
                    First = $first
                    Second = $second
                    ImportProof = $importProof
                    NewIdentity = $newIdentity
                } | ConvertTo-Json -Depth 6 -Compress
            } finally {
                $old.Dispose(); $new.Dispose()
            }
            """;

        var success = RunPowerShell(seedScript, environment);

        Assert.True(success.ExitCode == 0, success.Output);
        using var json = JsonDocument.Parse(success.Output);
        Assert.Equal(
            json.RootElement.GetProperty("ExportProof").GetRawText(),
            json.RootElement.GetProperty("ImportProof").GetRawText());
        Assert.Equal(3, json.RootElement.GetProperty("First").GetProperty("InsertedCount").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("First").GetProperty("ExistingCount").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("Second").GetProperty("InsertedCount").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("Second").GetProperty("ExistingCount").GetInt32());
        Assert.Equal(newDatabase.DatabaseName, json.RootElement.GetProperty("NewIdentity").GetProperty("DatabaseName").GetString());
        Assert.Equal(NewMesIngestContract.SchemaVersion, json.RootElement.GetProperty("NewIdentity").GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(expectedHistoryEpoch, json.RootElement.GetProperty("NewIdentity").GetProperty("HistoryEpoch").GetString());
        Assert.True(json.RootElement.GetProperty("NewIdentity").GetProperty("HasGlobalSessionVisibility").GetBoolean());

        var postSeedBaseline = await ReadPollTraceHighWaterAsync(newDatabase.ConnectionString);
        var roundStart = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);
        for (var index = 1; index <= 3; index++)
        {
            await newProjection.CommitRoundAsync(new MesTaskUnionRound(
                $"cutover-round-{index}",
                "MES_TASK_UNION/sha256:cutover-test",
                MesTaskUnionRoundOutcome.Success,
                roundStart.AddMinutes(index),
                roundStart.AddMinutes(index).AddSeconds(1),
                []));
        }
        environment["CUTOVER_TEST_POLL_BASELINE"] = postSeedBaseline.ToString();
        var projectionScript = $$"""
            . '{{tools}}'
            $new = [System.Data.SqlClient.SqlConnection]::new($env:CUTOVER_TEST_NEW_SQLSERVER)
            try {
                $new.Open()
                Get-CutoverThreeProjectionGate `
                    -Connection $new `
                    -CutoverRunId 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' `
                    -ExpectedHistoryEpoch $env:CUTOVER_TEST_HISTORY_EPOCH `
                    -AfterPollTraceSequence ([long]$env:CUTOVER_TEST_POLL_BASELINE) |
                    ConvertTo-Json -Depth 6 -Compress
            } finally { $new.Dispose() }
            """;
        var projection = RunPowerShell(projectionScript, environment);
        Assert.True(projection.ExitCode == 0, projection.Output);
        using var projectionJson = JsonDocument.Parse(projection.Output);
        Assert.Equal(3, projectionJson.RootElement.GetProperty("Rounds").GetArrayLength());
        Assert.Equal(
            "THREE_CONSECUTIVE_PROJECTIONS",
            projectionJson.RootElement.GetProperty("Gate").GetProperty("Name").GetString());

        environment["CUTOVER_TEST_POLL_BASELINE"] =
            (await ReadPollTraceHighWaterAsync(newDatabase.ConnectionString)).ToString();
        var staleProjection = RunPowerShell(projectionScript, environment);
        Assert.NotEqual(0, staleProjection.ExitCode);
        Assert.Contains("CUTOVER_THREE_PROJECTIONS_NOT_PROVEN", staleProjection.Output, StringComparison.Ordinal);

        var conflictingKeyToken = TransportDemandKeyIdentity.CreateToken(
            "DIFFERENT-WORK",
            tombstones[0].Sublot);
        var conflictingSeed = $$"""
            . '{{tools}}'
            $new = [System.Data.SqlClient.SqlConnection]::new($env:CUTOVER_TEST_NEW_SQLSERVER)
            try {
                $new.Open()
                $conflict = [pscustomobject]@{
                    KeyToken = '{{conflictingKeyToken}}'
                    WorkType = 'DIFFERENT-WORK'
                    Sublot = '{{tombstones[0].Sublot}}'
                    OriginalSeriesId = '{{tombstones[0].OriginalSeriesId}}'
                    ArchivedAt = [DateTimeOffset]::Parse('2026-08-20T00:00:00+00:00')
                    ArchiveConclusion = 'ARCHIVED'
                    TombstoneVersion = 1
                }
                $proof = Get-CutoverTombstoneProof -Tombstones @($conflict)
                Invoke-CutoverTombstoneSeed -Connection $new -Tombstones @($conflict) -ExpectedProof $proof
            } finally {
                $new.Dispose()
            }
            """;
        var conflict = RunPowerShell(conflictingSeed, environment);

        Assert.NotEqual(0, conflict.ExitCode);
        Assert.Contains("CUTOVER_TOMBSTONE_CONFLICT", conflict.Output, StringComparison.Ordinal);
        Assert.Equal(3, await CountTombstonesAsync(newDatabase.ConnectionString));
    }

    private static PowerShellResult RunPowerShell(
        string script,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.CSharpRoot,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$ErrorActionPreference = 'Stop'; " + script);
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "PowerShell test process timed out.");
        return new PowerShellResult(process.ExitCode, stdout + stderr);
    }

    private static async Task InsertTombstonesAsync(
        string connectionString,
        IReadOnlyList<TombstoneFixture> tombstones)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var tombstone in tombstones)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mesingest.ArchivedDemandKeyTombstones
                    (KeyToken, WorkType, Sublot, OriginalSeriesId, ArchivedAt,
                     ArchiveConclusion, TombstoneVersion)
                VALUES
                    (@keyToken, @workType, @sublot, @seriesId, @archivedAt, N'ARCHIVED', 1);
                """;
            command.Parameters.AddWithValue("@keyToken", tombstone.KeyToken);
            command.Parameters.AddWithValue("@workType", tombstone.WorkType);
            command.Parameters.AddWithValue("@sublot", tombstone.Sublot);
            command.Parameters.AddWithValue("@seriesId", tombstone.OriginalSeriesId);
            command.Parameters.AddWithValue("@archivedAt", tombstone.ArchivedAt);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> CountTombstonesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mesingest.ArchivedDemandKeyTombstones;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadHistoryEpochAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HistoryEpoch FROM mesingest.SchemaInfo WHERE Id = 1;";
        return ((Guid)(await command.ExecuteScalarAsync())!).ToString("D");
    }

    private static async Task<long> ReadPollTraceHighWaterAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(PollTraceSequence), 0) FROM mesingest.PollTraces;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string ToSystemDataConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.Remove("Trust Server Certificate");
        builder.Remove("Encrypt");
        return builder.ConnectionString;
    }

    private sealed record PowerShellResult(int ExitCode, string Output);

    private sealed record TombstoneFixture(
        string KeyToken,
        string WorkType,
        string Sublot,
        string OriginalSeriesId,
        DateTimeOffset ArchivedAt);

    private sealed class CutoverCatalogHandler(HistoryEpoch historyEpoch) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == HttpExternallyReadableDemandCatalogClient.ContractPath)
            {
                return Task.FromResult(JsonResponse(new
                {
                    contractVersion = NewMesIngestContract.Version,
                    schemaVersion = NewMesIngestContract.SchemaVersion,
                    capabilities = NewMesIngestContract.Capabilities.Select(capability => new
                    {
                        id = capability.Id,
                        version = capability.Version,
                    }),
                }));
            }

            var response = JsonResponse(new
            {
                contractVersion = NewMesIngestContract.Version,
                historyEpoch = historyEpoch.Value.ToString("D"),
                catalogRevision = 3,
                projectionCommitId = "commit-3",
                projectionSequence = 3,
                projectionCommittedAt = "2026-08-25T00:03:00+00:00",
                count = 1,
                items = new[]
                {
                    new
                    {
                        demandId = "demand-a",
                        seriesId = "series-a",
                        transportDemandKey = new { workType = "CUT", sublot = "SUBLOT-A" },
                        generation = 1,
                        demandRevision = 1,
                        createdAt = "2026-08-25T00:00:00+00:00",
                        valueObservedAt = "2026-08-25T00:03:00+00:00",
                        valuePollTraceId = "poll-3",
                        valueProjectionCommitId = "commit-3",
                        liveMesFields = new
                        {
                            area = "N3-3",
                            eqp = "WB-03",
                            step = "STEP-2",
                            mesSourceDate = "2026-08-25T00:02:00+00:00",
                            package = "QFN-G1",
                        },
                    },
                },
            });
            response.Headers.ETag = new EntityTagHeaderValue(
                $"\"catalog-h{historyEpoch.Value:N}-r3\"",
                isWeak: true);
            return Task.FromResult(response);
        }

        private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };
    }
}
