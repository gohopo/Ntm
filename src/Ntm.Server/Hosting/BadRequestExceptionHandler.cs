using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;

namespace Ntm.Server.Hosting;

/// <summary>
/// 管理接口绑定参数失败（请求体不是 JSON、枚举取值不认识、类型不对）时回 400，
/// 格式和按字段校验的错误一样，字段名取 JSON 路径（Q304）。开发和生产环境表现一致：
/// 绑定失败一律抛出来（<see cref="RouteHandlerOptions.ThrowOnBadRequest"/>），在这里接住。
/// </summary>
internal sealed partial class BadRequestExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    private const string Title = "提交的内容有问题。";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException bad)
        {
            return false;
        }

        httpContext.Response.StatusCode = bad.StatusCode;
        if (bad.StatusCode != StatusCodes.Status400BadRequest)
        {
            return await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = { Status = bad.StatusCode, Title = "请求的格式不对。" },
            }).ConfigureAwait(false);
        }

        (string field, string message) = Describe(bad);
        Microsoft.AspNetCore.Http.HttpResults.ValidationProblem result = TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] },
            title: Title);
        await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }

    /// <summary>从绑定异常里认出哪个字段、为什么。认不出来时挂在空字段名上，前端显示成整体错误。</summary>
    internal static (string Field, string Message) Describe(BadHttpRequestException bad)
    {
        if (bad.InnerException is not JsonException json)
        {
            return (string.Empty, "请求参数的格式不对。");
        }

        // 只有「值转不成目标类型」才落到具体字段上；其余都是 JSON 本身写坏了。
        if (!json.Message.Contains("could not be converted", StringComparison.Ordinal))
        {
            return (string.Empty, "请求体不是合法的 JSON。");
        }

        string field = FieldOf(json.Path);
        if (EnumOf(json.Message, json.Path) is { } enumType)
        {
            IEnumerable<string> names = Enum.GetNames(enumType).Select(JsonNamingPolicy.CamelCase.ConvertName);
            return (field, $"不认识的取值，可以用：{string.Join("、", names)}。");
        }

        return (field, "类型不对。");
    }

    /// <summary><c>$.rules[0].kind</c> → <c>rules[0].kind</c>；根上（<c>$</c>）是空串。</summary>
    internal static string FieldOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return string.Empty;
        }

        return path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
    }

    /// <summary>
    /// 消息里点名的是转换失败时的目标类型：枚举本身，或者（用构造函数绑定的 record）外层的输入类型。
    /// 后一种按 JSON 路径往下走到那个属性，看它是不是枚举。
    /// </summary>
    private static Type? EnumOf(string message, string? path)
    {
        Match match = ConvertedTo().Match(message);
        if (!match.Success || Resolve(match.Groups["type"].Value) is not { } type)
        {
            return null;
        }

        foreach (string segment in Segments(path))
        {
            if (Unwrap(type).IsEnum)
            {
                break;
            }

            PropertyInfo? property = Unwrap(type).GetProperty(
                segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                return null;
            }

            type = ElementOf(property.PropertyType);
        }

        Type target = Unwrap(type);
        return target.IsEnum ? target : null;
    }

    private static IEnumerable<string> Segments(string? path)
    {
        foreach (string part in FieldOf(path).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int bracket = part.IndexOf('[', StringComparison.Ordinal);
            yield return bracket < 0 ? part : part[..bracket];
        }
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static Type ElementOf(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType()!;
        }

        Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return type != typeof(string) && enumerable is not null ? enumerable.GetGenericArguments()[0] : type;
    }

    private static Type? Resolve(string name)
    {
        Match nullable = NullableOf().Match(name);
        if (nullable.Success)
        {
            name = nullable.Groups["inner"].Value;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name?.StartsWith("Ntm.", StringComparison.Ordinal) == true
                && assembly.GetType(name) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    [GeneratedRegex(@"converted to (?<type>\S+?)\. Path", RegexOptions.CultureInvariant)]
    private static partial Regex ConvertedTo();

    [GeneratedRegex(@"^System\.Nullable`1\[(?:\[)?(?<inner>[\w.+]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NullableOf();
}
