using BossCam.Service.Security;

namespace BossCam.Tests;

/// <summary>
/// Locks down the loopback-classification rules used by
/// <see cref="BindAddressInspector"/> so the host-aware LAN gate never
/// accidentally leaves a non-loopback bind without auth, nor accidentally
/// engages auth on a loopback bind.
/// </summary>
public sealed class BindAddressInspectorTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5317")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.99:5000")]
    [InlineData("http://127.255.255.255:80")]   // entire 127.0.0.0/8 is loopback per IPAddress.IsLoopback
    [InlineData("http://localhost:5317")]
    [InlineData("http://LOCALHOST:5317")]        // case-insensitive hostname match
    [InlineData("http://[::1]:5317")]            // IPv6 loopback literal
    [InlineData("https://[::1]/")]
    public void ClassifyUrl_Returns_Loopback_For_Loopback_Hosts(string url)
    {
        Assert.Equal(LoopbackClass.Loopback, BindAddressInspector.ClassifyUrl(url));
        Assert.False(BindAddressInspector.IsAnyNonLoopback(url));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5317")]          // explicit LAN-bind wildcard
    [InlineData("http://[::]:5317")]             // IPv6 any
    [InlineData("http://192.168.1.50:5317")]
    [InlineData("http://10.0.0.30:80")]
    [InlineData("http://172.16.0.1:5000")]
    [InlineData("http://172.31.255.255:5000")]   // upper bound of RFC1918 172.16/12
    [InlineData("http://8.8.8.8:80")]            // public IP
    [InlineData("http://bosscam.lan:5317")]      // unknown DNS host, treated as non-loopback
    [InlineData("not-a-url-at-all")]             // unparseable => non-loopback (conservative)
    public void ClassifyUrl_Returns_NonLoopback_For_External_Hosts(string url)
    {
        Assert.Equal(LoopbackClass.NonLoopback, BindAddressInspector.ClassifyUrl(url));
        Assert.True(BindAddressInspector.IsAnyNonLoopback(url));
    }

    [Theory]
    [InlineData("")]                             // empty => loopback (default-safe)
    [InlineData("   ")]                           // whitespace => loopback (default-safe)
    [InlineData(null)]
    public void IsAnyNonLoopback_Treats_Empty_And_Null_As_Loopback(string? urls)
    {
        Assert.False(BindAddressInspector.IsAnyNonLoopback(urls));
        Assert.Equal(LoopbackClass.Loopback, BindAddressInspector.ClassifyUrl(urls));
    }

    [Fact]
    public void IsAnyNonLoopback_Returns_True_When_Any_URL_In_List_Is_NonLoopback()
    {
        const string mixed =
            "http://127.0.0.1:5317," +
            "http://localhost:5318," +
            "http://[::1]:5319," +
            "http://127.0.0.99:5320," +
            "http://0.0.0.0:5317";                // the wildcard makes the whole list non-loopback

        Assert.True(BindAddressInspector.IsAnyNonLoopback(mixed));
    }

    [Fact]
    public void IsAnyNonLoopback_Returns_False_When_All_URLs_Are_Loopback()
    {
        const string allLoopback =
            "http://127.0.0.1:5317," +
            "http://localhost:5318," +
            "http://[::1]:5319";

        Assert.False(BindAddressInspector.IsAnyNonLoopback(allLoopback));
    }

    [Fact]
    public void IsAnyNonLoopback_Order_Matters_NonLoopback_At_Front()
    {
        // First URL is non-loopback, rest are loopback — overall still non-loopback.
        Assert.True(BindAddressInspector.IsAnyNonLoopback("http://0.0.0.0:80,http://127.0.0.1:81"));
    }

    [Fact]
    public void IsAnyNonLoopback_Ignores_Whitespace_And_Empty_Entries()
    {
        // Empty entries from a trailing comma must not flip the verdict.
        const string padded =
            "  http://127.0.0.1:5317 , , http://localhost:5318 , ";
        Assert.False(BindAddressInspector.IsAnyNonLoopback(padded));
    }
}
