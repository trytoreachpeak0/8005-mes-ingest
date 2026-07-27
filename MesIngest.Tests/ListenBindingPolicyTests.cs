using MesIngest.Host;

namespace MesIngest.Tests;

public class ListenBindingPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5088")]
    [InlineData("http://localhost:5088")]
    [InlineData("http://[::1]:5088")]
    [InlineData("http://127.0.0.1:5088;http://localhost:5089")]
    public void Localhost_urls_do_not_require_shared_secret(string urls)
    {
        Assert.True(ListenBindingPolicy.IsLocalhostOnly(urls));
        Assert.False(ListenBindingPolicy.RequiresSharedSecret(urls));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5088")]
    [InlineData("http://*:5088")]
    [InlineData("http://+:5088")]
    [InlineData("http://192.168.1.10:5088")]
    [InlineData("http://10.0.0.5:5088")]
    [InlineData("http://127.0.0.1:5088;http://0.0.0.0:5089")]
    public void Non_localhost_urls_require_shared_secret(string urls)
    {
        Assert.False(ListenBindingPolicy.IsLocalhostOnly(urls));
        Assert.True(ListenBindingPolicy.RequiresSharedSecret(urls));
    }

    [Fact]
    public void Empty_urls_treated_as_localhost_only_default()
    {
        Assert.True(ListenBindingPolicy.IsLocalhostOnly(""));
        Assert.True(ListenBindingPolicy.IsLocalhostOnly("   "));
    }
}
