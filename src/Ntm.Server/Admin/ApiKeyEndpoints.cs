using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Data;
using Ntm.Gateway.Restrictions;
using Ntm.Gateway.Security;

namespace Ntm.Server.Admin;

internal sealed record ApiKeyInput(string? Name);

internal sealed record ApiKeyPatch(string? Name, bool? Enabled);

internal sealed record ApiKeyView(
    int Id,
    string Name,
    string Prefix,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);

/// <summary>详情页（Q103）：基本信息加上 Restriction。</summary>
internal sealed record ApiKeyDetailView(ApiKeyView ApiKey, ApiKeyRestrictionsView Restrictions);

/// <summary>新建 API 密钥的回应：<see cref="Secret"/> 是原文，只在这一次出现，库里只存指纹（ADR-0006）。</summary>
internal sealed record CreatedApiKeyView(string Secret, ApiKeyView View);

/// <summary>/api/api-keys：给客户端用的 API 密钥。</summary>
internal static class ApiKeyEndpoints
{
    private const int MaxNameLength = 200;

    public static RouteGroupBuilder MapApiKeyEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapPatch("/{id:int}", UpdateAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        ConfigDbContext db,
        TrafficDbContext traffic,
        CancellationToken cancellationToken)
    {
        List<ApiKey> tokens = await db.ApiKeys
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 最近使用时间从请求记录里算：热路径上不为它多写一次配置库（ADR-0004 的单写者）。
        Dictionary<int, DateTimeOffset> lastUsed = await traffic.Requests
            .AsNoTracking()
            .Where(r => r.ApiKeyId != null)
            .GroupBy(r => r.ApiKeyId!.Value)
            .Select(g => new { ApiKeyId = g.Key, LastUsedAt = g.Max(r => r.StartedAt) })
            .ToDictionaryAsync(x => x.ApiKeyId, x => x.LastUsedAt, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(tokens
            .Select(t => ToView(t, lastUsed.TryGetValue(t.Id, out DateTimeOffset at) ? at : null))
            .ToList());
    }

    private static async Task<IResult> GetAsync(
        int id,
        ConfigDbContext db,
        TrafficDbContext traffic,
        CancellationToken cancellationToken)
    {
        ApiKey? key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return AdminResults.NotFound("API 密钥");
        }

        DateTimeOffset? lastUsed = await traffic.Requests
            .AsNoTracking()
            .Where(r => r.ApiKeyId == id)
            .MaxAsync(r => (DateTimeOffset?)r.StartedAt, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(new ApiKeyDetailView(ToView(key, lastUsed), RestrictionEndpoints.ToView(key)));
    }

    private static async Task<IResult> CreateAsync(
        ApiKeyInput input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        string? name = await ValidateNameAsync(db, input.Name, selfId: 0, errors, cancellationToken).ConfigureAwait(false);
        if (name is null)
        {
            return errors.ToResult();
        }

        string secret = SecretFingerprint.GenerateApiKey();
        var token = new ApiKey
        {
            Name = name,
            Fingerprint = SecretFingerprint.Compute(secret),
            Prefix = SecretFingerprint.Prefix(secret),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.ApiKeys.Add(token);
        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("已经有同名的 API 密钥了。");
        }

        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.Created(
            AdminResults.Location("/api/api-keys", token.Id),
            new CreatedApiKeyView(secret, ToView(token, lastUsedAt: null)));
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        ApiKeyPatch input,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        CancellationToken cancellationToken)
    {
        ApiKey? token = await db.ApiKeys.FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return AdminResults.NotFound("API 密钥");
        }

        var errors = new ValidationErrors();
        if (input.Name is not null)
        {
            string? name = await ValidateNameAsync(db, input.Name, selfId: id, errors, cancellationToken).ConfigureAwait(false);
            if (name is not null)
            {
                token.Name = name;
            }
        }

        if (input.Enabled is bool enabled)
        {
            token.Enabled = enabled;
        }

        if (errors.HasErrors)
        {
            return errors.ToResult();
        }

        if (!await AdminResults.TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return AdminResults.Conflict("已经有同名的 API 密钥了。");
        }

        // 停用或删除后，下一份快照里就没有它了：新请求马上被拒，已经在路上的照常结束。
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        int id,
        ConfigDbContext db,
        ConfigSnapshotProvider snapshots,
        RestrictionChecker restrictions,
        CancellationToken cancellationToken)
    {
        ApiKey? token = await db.ApiKeys.FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return AdminResults.NotFound("API 密钥");
        }

        // 请求记录里冗余存了 API 密钥名，删掉 API 密钥不影响查看历史。
        db.ApiKeys.Remove(token);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        restrictions.Costs.ForgetApiKey(id);
        await snapshots.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>API 密钥名不许重复：请求记录里存的是名字，重名就分不清是哪一个。</summary>
    private static async Task<string?> ValidateNameAsync(
        ConfigDbContext db,
        string? raw,
        int selfId,
        ValidationErrors errors,
        CancellationToken cancellationToken)
    {
        string name = raw?.Trim() ?? "";
        if (name.Length == 0)
        {
            errors.Add("name", "请填写 API 密钥名。");
            return null;
        }

        if (name.Length > MaxNameLength)
        {
            errors.Add("name", "API 密钥名最多 200 个字符。");
            return null;
        }

        bool taken = await db.ApiKeys
            .AnyAsync(t => t.Name == name && t.Id != selfId, cancellationToken)
            .ConfigureAwait(false);
        if (taken)
        {
            errors.Add("name", "已经有同名的 API 密钥了。");
            return null;
        }

        return name;
    }

    private static ApiKeyView ToView(ApiKey token, DateTimeOffset? lastUsedAt) =>
        new(token.Id, token.Name, token.Prefix, token.Enabled, token.CreatedAt, token.ExpiresAt, lastUsedAt ?? token.LastUsedAt);
}
