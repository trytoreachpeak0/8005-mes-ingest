using MesIngest.Watch;

namespace MesIngest.Tests;

public class WatchConnectionPreferencesTests
{
    [Fact]
    public void Safe_connection_preferences_round_trip_without_a_plaintext_credential()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"watch-connection-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "connection.json");
        var expected = WatchConnectionPreferences.FromOptions(new WatchOptions
        {
            BaseUrl = "https://mes-watch.factory.test:5088",
            RequestTimeoutSeconds = 45,
            SharedSecret = "plain-secret",
        });

        try
        {
            WatchConnectionPreferencesStore.Save(path, expected);

            Assert.Equal(expected, WatchConnectionPreferencesStore.Load(path, WatchConnectionPreferences.Default));
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("plain-secret", json, StringComparison.Ordinal);
            Assert.Contains("external-configuration", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":99,\"baseUrl\":\"http://other\",\"requestTimeoutSeconds\":20,\"credentialReference\":\"external-configuration\"}")]
    [InlineData("{\"version\":1,\"baseUrl\":\"file:///tmp/secret\",\"requestTimeoutSeconds\":20,\"credentialReference\":\"external-configuration\"}")]
    [InlineData("{\"version\":1,\"baseUrl\":\"http://other\",\"requestTimeoutSeconds\":301,\"credentialReference\":\"external-configuration\"}")]
    [InlineData("{\"version\":1,\"baseUrl\":\"http://other\",\"requestTimeoutSeconds\":20,\"credentialReference\":\"plaintext\"}")]
    public void Corrupt_incompatible_or_unsafe_connection_preferences_fall_back(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"watch-connection-{Guid.NewGuid():N}.json");
        var fallback = new WatchConnectionPreferences(
            "http://configured-host:5088",
            30,
            WatchCredentialReference.ExternalConfiguration);
        File.WriteAllText(path, json);

        try
        {
            Assert.Equal(fallback, WatchConnectionPreferencesStore.Load(path, fallback));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
