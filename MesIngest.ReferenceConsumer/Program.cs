using System.Net.Http.Headers;
using System.Text.Json;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.ReferenceConsumer;

internal static class Program
{
    private const string Usage = "Usage: MesIngest.ReferenceConsumer verify-cutover "
        + "--base-url <url> --expected-history-epoch <guid> "
        + "--forbidden-key-token-file <path> [--shared-secret-env <name>]";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            using var http = new HttpClient
            {
                BaseAddress = new Uri(options["--base-url"], UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(30),
            };
            if (options.TryGetValue("--shared-secret-env", out var environmentName))
            {
                var secret = Environment.GetEnvironmentVariable(environmentName);
                if (string.IsNullOrWhiteSpace(secret))
                {
                    throw new InvalidOperationException(
                        "CUTOVER_REFERENCE_CONSUMER_SECRET_MISSING: the named environment variable is empty.");
                }

                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", secret);
            }

            var expectedEpoch = HistoryEpoch.FromGuid(
                Guid.Parse(options["--expected-history-epoch"]));
            var forbiddenTokens = File.ReadLines(options["--forbidden-key-token-file"])
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToHashSet(StringComparer.Ordinal);
            if (forbiddenTokens.Any(token =>
                    token.Length != 64 || token.Any(character =>
                        character is not (>= '0' and <= '9')
                        and not (>= 'a' and <= 'f'))))
            {
                throw new InvalidDataException(
                    "CUTOVER_TOMBSTONE_KEY_TOKEN_INVALID: the forbidden-key file contains an invalid token.");
            }

            var result = await CutoverReferenceConsumerProbe.VerifyAsync(
                http,
                expectedEpoch,
                forbiddenTokens).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "PASSED",
                historyEpoch = result.HistoryEpoch,
                result.ProjectionCommitId,
                result.ProjectionSequence,
                result.ItemCount,
                result.TombstoneKeysExcluded,
            }));
            return 0;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length < 1 || !string.Equals(args[0], "verify-cutover", StringComparison.Ordinal))
        {
            throw new ArgumentException("The only supported command is verify-cutover.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Every cutover option requires one value.");
            }

            if (!values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"Option '{args[index]}' was specified more than once.");
            }
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--base-url",
            "--expected-history-epoch",
            "--forbidden-key-token-file",
            "--shared-secret-env",
        };
        var unexpected = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unexpected is not null)
        {
            throw new ArgumentException($"Unknown option '{unexpected}'.");
        }

        foreach (var required in new[]
                 {
                     "--base-url",
                     "--expected-history-epoch",
                     "--forbidden-key-token-file",
                 })
        {
            if (!values.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Required option '{required}' is missing.");
            }
        }

        return values;
    }
}
