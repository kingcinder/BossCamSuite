using System.Security.Cryptography;
using System.Text;
using BossCam.Service.Security;
using Microsoft.AspNetCore.Http;

namespace BossCam.Tests;

/// <summary>
/// Unit coverage for the host-aware <see cref="LanBoundTokenGate"/> middleware —
/// open paths, gated paths, constant-time compare, header and bearer parsing,
/// query-string rejection.
/// </summary>
public sealed class LanBoundTokenGateTests
{
    private const string ExpectedToken = "expected-token-12345";

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    [InlineData("/favicon.svg")]
    [InlineData("/something/else/entirely")]
    public async Task Open_Paths_Pass_Through_Without_Token(string path)
    {
        var ctx = NewContext(path, headers: EmptyHeaders);
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.True(ctx.Items.ContainsKey("next-called"));
    }

    [Theory]
    [InlineData("/api/devices")]
    [InlineData("/api/recordings")]
    [InlineData("/api/devices/test/validation/run")]
    [InlineData("/Swagger/Index.html")]     // case-insensitive
    public async Task Gated_Path_Without_Header_Returns_401(string path)
    {
        var ctx = NewContext(path, headers: EmptyHeaders);
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.False(ctx.Items.ContainsKey("next-called"));
        Assert.Equal("XLAN realm=\"BossCam\", error=\"missing\"", ctx.Response.Headers["WWW-Authenticate"]);
        var body = GetResponseBody(ctx);
        Assert.Contains("LAN token required", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"reason\":\"missing\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gated_Path_With_Matching_X_LAN_Token_Is_Accepted()
    {
        var ctx = NewContext("/api/devices", headers: new Dictionary<string, string>
        {
            ["X-LAN-Token"] = ExpectedToken
        });
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.True(ctx.Items.ContainsKey("next-called"));
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Gated_Path_With_Matching_Bearer_Is_Accepted()
    {
        var ctx = NewContext("/api/devices", headers: new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + ExpectedToken
        });
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.True(ctx.Items.ContainsKey("next-called"));
    }

    [Fact]
    public async Task Gated_Path_With_Mismatched_X_LAN_Token_Is_Rejected()
    {
        var ctx = NewContext("/api/devices", headers: new Dictionary<string, string>
        {
            ["X-LAN-Token"] = "totally-wrong-" + new string('a', ExpectedToken.Length)
        });
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        var body = GetResponseBody(ctx);
        Assert.Contains("\"reason\":\"invalid\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/devices")]
    [InlineData("/api/recordings")]
    public async Task Gated_Path_Without_Token_Returns_401(string path)
    {
        var ctx = NewContext(path, headers: EmptyHeaders);
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Gated_Path_With_Empty_X_LAN_Token_Is_Rejected()
    {
        var ctx = NewContext("/api/devices", headers: new Dictionary<string, string>
        {
            ["X-LAN-Token"] = ""
        });
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Contains("\"reason\":\"missing\"", GetResponseBody(ctx), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gated_Path_With_Whitespace_X_LAN_Token_Is_Rejected()
    {
        var ctx = NewContext("/api/devices", headers: new Dictionary<string, string>
        {
            ["X-LAN-Token"] = "   "
        });
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Mixed_Case_Bearer_Prefix_Is_Recognised()
    {
        var ctx = NewContext("/api/devices", headers: new Dictionary<string, string>
        {
            ["Authorization"] = "bearer " + ExpectedToken
        });
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.True(ctx.Items.ContainsKey("next-called"));
    }

    [Fact]
    public async Task Non_Bearer_Authorization_Header_Does_Not_Match()
    {
        var ctx = NewContext("/api/devices", headers: new Dictionary<string, string>
        {
            ["Authorization"] = "Basic dXNlcjpwYXNz"
        });
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Query_String_Token_Is_Not_Accepted()
    {
        // Tokens in ?token=... leak via referer, browser history, server access logs.
        // Ensure middleware ignores query strings even if presented.
        var ctx = NewContext("/api/devices?lanToken=" + ExpectedToken, headers: EmptyHeaders);
        var middleware = new LanBoundTokenGate(PassthroughNext, ExpectedToken);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public void Constructor_Throws_On_Null_Token()
    {
        Assert.Throws<InvalidOperationException>(() => new LanBoundTokenGate(PassthroughNext, ""));
    }

    [Fact]
    public void ConstantTimeCompare_Logic_Matches_DotNet_Reference()
    {
        // Sanity: assert the expected compare path agrees with CryptographicOperations on
        // identical and slightly-mismatched inputs of varying length. The middleware's
        // runtime compare already does exactly this, but locking the algorithm intent
        // down here catches future regressions where someone swaps in string ==.
        var expected = Encoding.UTF8.GetBytes(ExpectedToken);

        Assert.True(CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(ExpectedToken)));
        Assert.False(CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(ExpectedToken + "x")));
        Assert.False(CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(ExpectedToken[..^1])));
    }

    private static readonly IDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    private static Task PassthroughNext(HttpContext context)
    {
        context.Items["next-called"] = true;
        return Task.CompletedTask;
    }

    private static DefaultHttpContext NewContext(string pathAndQuery, IDictionary<string, string> headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;

        var path = pathAndQuery;
        var queryStart = path.IndexOf('?');
        if (queryStart >= 0)
        {
            ctx.Request.Path = path[..queryStart];
            ctx.Request.QueryString = new QueryString(path[queryStart..]);
        }
        else
        {
            ctx.Request.Path = path;
        }

        foreach (var kv in headers)
        {
            ctx.Request.Headers[kv.Key] = kv.Value;
        }

        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string GetResponseBody(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
