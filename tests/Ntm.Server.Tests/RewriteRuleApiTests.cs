using System.Net;
using System.Text.Json;

namespace Ntm.Server.Tests;

/// <summary>请求改写规则的管理接口（Q144–Q152）。</summary>
public sealed class RewriteRuleApiTests
{
    private static object Rule(string target, string operation, string path, string? value = null) =>
        new { target, operation, path, value };

    [Fact]
    public async Task RulesRoundTripThroughTheChannelDetail()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");

        (HttpStatusCode status, JsonElement saved) = await admin.PutAsync($"/api/channels/{id}/rewrite-rules", new
        {
            rules = new[]
            {
                Rule("body", "set", "thinking.type", "\"disabled\""),
                Rule("body", "remove", "top_k"),
                Rule("header", "default", "x-ntm-tag", "probe"),
            },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, saved.GetArrayLength());

        JsonElement detail = await admin.GetJsonAsync($"/api/channels/{id}");
        JsonElement rules = detail.GetProperty("rewriteRules");
        Assert.Equal(3, rules.GetArrayLength());
        Assert.Equal("body", rules[0].GetProperty("target").GetString());
        Assert.Equal("set", rules[0].GetProperty("operation").GetString());
        Assert.Equal("thinking.type", rules[0].GetProperty("path").GetString());
        Assert.Equal("\"disabled\"", rules[0].GetProperty("value").GetString());

        // 删除规则不留值。
        Assert.Equal(JsonValueKind.Null, rules[1].GetProperty("value").ValueKind);
        Assert.Equal("header", rules[2].GetProperty("target").GetString());

        // 空列表清空。
        (HttpStatusCode cleared, JsonElement empty) = await admin.PutAsync($"/api/channels/{id}/rewrite-rules", new { rules = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared);
        Assert.Equal(0, empty.GetArrayLength());
    }

    [Fact]
    public async Task ProtectedPlacesAndBadValuesAreRejectedByField()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");

        (HttpStatusCode status, JsonElement body) = await admin.PutAsync($"/api/channels/{id}/rewrite-rules", new
        {
            rules = new[]
            {
                Rule("body", "set", "model", "\"sneaky\""),
                Rule("header", "set", "Authorization", "Bearer x"),
                Rule("body", "set", "thinking", "disabled"),
                Rule("body", "set", "a..b", "1"),
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        JsonElement errors = body.GetProperty("errors");
        Assert.Contains("不能改写", errors.GetProperty("rules[0].path")[0].GetString());
        Assert.Contains("不能改写", errors.GetProperty("rules[1].path")[0].GetString());
        Assert.Contains("JSON", errors.GetProperty("rules[2].value")[0].GetString());
        Assert.Contains("点号", errors.GetProperty("rules[3].path")[0].GetString());
    }

    [Fact]
    public async Task TwoRulesOnTheSamePlaceAreRejected()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");

        (HttpStatusCode status, JsonElement body) = await admin.PutAsync($"/api/channels/{id}/rewrite-rules", new
        {
            rules = new[]
            {
                Rule("header", "set", "X-Tag", "a"),
                Rule("header", "remove", "x-tag"),
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("已经有一条规则", body.GetProperty("errors").GetProperty("rules[1].path")[0].GetString());
    }

    [Fact]
    public async Task TooManyRulesAreRejectedAndAMissingChannelIs404()
    {
        await using var host = new ServerHost();
        using AdminClient admin = await AdminClient.SignInAsync(host);
        int id = await admin.CreateChannelAsync("alpha");

        object[] many = [.. Enumerable.Range(0, 21).Select(i => Rule("body", "set", "f" + i, "1"))];
        (HttpStatusCode status, JsonElement body) = await admin.PutAsync($"/api/channels/{id}/rewrite-rules", new { rules = many });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("最多 20 条", body.GetProperty("errors").GetProperty("rules")[0].GetString());

        (HttpStatusCode missing, _) = await admin.PutAsync("/api/channels/999/rewrite-rules", new { rules = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    [Fact]
    public async Task TheRouteNeedsASession()
    {
        await using var host = new ServerHost();
        using HttpClient client = host.CreateClient();

        using var content = new StringContent("{\"rules\":[]}", System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PutAsync("/api/channels/1/rewrite-rules", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
