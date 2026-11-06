namespace Ntm.Server.Admin;

/// <summary>
/// 收集按字段分的校验错误，最后一次性回 400。
/// 字段名用 camelCase（列表里的写成 <c>rules[0].pattern</c>），前端直接对到输入框上。
/// </summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    /// <summary>已经收集到的错误条数（不是字段数）。用来判断某一段校验有没有新添错误。</summary>
    public int Count => _errors.Values.Sum(messages => messages.Count);

    public ValidationErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out List<string>? messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
        return this;
    }

    public IResult ToResult() => TypedResults.ValidationProblem(
        _errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
        title: "提交的内容有问题。");
}
