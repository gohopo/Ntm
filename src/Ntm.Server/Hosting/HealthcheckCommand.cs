using System.Text.RegularExpressions;

namespace Ntm.Server.Hosting;

/// <summary>
/// <c>healthcheck</c>：请求本机的 <c>/healthz</c>，好就退出 0，否则退出 1（Q338）。
/// 镜像里没有 shell 和 curl，Dockerfile 的 HEALTHCHECK 用它。不建主机、不碰数据库，几十毫秒就结束。
/// </summary>
internal static partial class HealthcheckCommand
{
    public const string Name = "healthcheck";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        Func<string, string?>? environment = null,
        HttpMessageHandler? handler = null)
    {
        Uri? uri = ResolveUri(args, environment ?? Environment.GetEnvironmentVariable);
        if (uri is null)
        {
            await output.WriteLineAsync("不知道网关监听在哪个地址，用 --url http://地址:端口 指定。").ConfigureAwait(false);
            return 1;
        }

        using HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            await output.WriteLineAsync($"{(int)response.StatusCode} {body}").ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await output.WriteLineAsync($"连不上 {uri}：{ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// 先看 <c>--url</c>，再看 ASPNETCORE_URLS 的第一个，再看 ASPNETCORE_HTTP_PORTS，都没有就是 Kestrel 默认的 5000。
    /// 监听所有地址的写法（<c>+</c>、<c>*</c>、<c>0.0.0.0</c>、<c>[::]</c>）换成本机回环地址。
    /// </summary>
    internal static Uri? ResolveUri(string[] args, Func<string, string?> environment)
    {
        int flag = Array.IndexOf(args, "--url");
        if (flag >= 0)
        {
            return flag + 1 < args.Length ? Health(args[flag + 1]) : null;
        }

        string? urls = environment("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls))
        {
            return Health(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]);
        }

        string? ports = environment("ASPNETCORE_HTTP_PORTS");
        if (!string.IsNullOrWhiteSpace(ports))
        {
            string port = ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            return Health($"http://127.0.0.1:{port}");
        }

        return Health("http://127.0.0.1:5000");
    }

    private static Uri? Health(string baseUrl)
    {
        string local = AnyHost().Replace(baseUrl.Trim(), match =>
            match.Groups["scheme"].Value + (match.Groups["host"].Value == "[::]" ? "[::1]" : "127.0.0.1"));
        return Uri.TryCreate(local.TrimEnd('/') + "/healthz", UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    [GeneratedRegex(@"^(?<scheme>https?://)(?<host>\+|\*|0\.0\.0\.0|\[::\])(?=[:/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AnyHost();
}
