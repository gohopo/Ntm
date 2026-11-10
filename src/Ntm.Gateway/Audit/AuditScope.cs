using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ntm.Gateway.Audit;

/// <summary>
/// 一次管理操作期间收集配置库的改动（Q358）。管理接口的中间件在请求开始时 <see cref="Begin"/>，
/// <see cref="AuditSaveChangesInterceptor"/> 在每次 SaveChanges 时往里记，请求结束时由中间件写成一条操作记录。
/// 用 <see cref="AsyncLocal{T}"/> 传递：配置库的 DbContext 不知道自己处在哪个 HTTP 请求里。
/// 不在作用域里的写入（/v1 热路径、后台同步）不记。
/// </summary>
public sealed class AuditScope : IDisposable
{
    private static readonly AsyncLocal<AuditScope?> CurrentScope = new();

    private readonly Lock _gate = new();
    private readonly List<AuditChange> _changes = [];
    private readonly AuditScope? _previous;
    private bool _closed;

    private AuditScope()
    {
        _previous = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    public static AuditScope? Current => CurrentScope.Value;

    public static AuditScope Begin() => new();

    /// <summary>到目前为止收集到的改动。调用之后作用域关闭，后台任务再写也不收了。</summary>
    public IReadOnlyList<AuditChange> Complete()
    {
        lock (_gate)
        {
            _closed = true;
            return [.. _changes];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
        }

        if (CurrentScope.Value == this)
        {
            CurrentScope.Value = _previous;
        }
    }

    /// <summary>为绕过 EF 跟踪器的批量更新补记字段名；调用者不能传入字段值。</summary>
    public void RecordChange(AuditChange change) => Add([change]);

    internal void Add(IEnumerable<AuditChange> changes)
    {
        lock (_gate)
        {
            if (!_closed)
            {
                _changes.AddRange(changes);
            }
        }
    }
}

/// <summary>
/// 挂在配置库上：SaveChanges 之前看一眼改动跟踪器，记下哪些行增、删、改了哪些字段。
/// 新增的行这时还没有 id，等保存完再补上。字段只记名字，不记值。
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <summary>不值得记的簿记字段。</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "CreatedAt", "UpdatedAt", "LastUsedAt", "PasswordChangedAt",
    };

    /// <summary>敏感字段合成一个名字，页面上只显示「已更换」。</summary>
    private static readonly Dictionary<string, string> Sensitive = new(StringComparer.Ordinal)
    {
        ["ProtectedSecret"] = "Secret",
        ["SecretFingerprint"] = "Secret",
        ["SecretPrefix"] = "Secret",
        ["Fingerprint"] = "Secret",
        ["Prefix"] = "Secret",
        ["ProtectedProxyUrl"] = "Proxy",
        ["PasswordHash"] = "Password",
    };

    /// <summary>保存前记下、保存后补 id 的改动，按 DbContext 分开：同一个拦截器挂在每个配置库实例上。</summary>
    private readonly ConditionalWeakTable<DbContext, List<(EntityEntry Entry, AuditChange Change)>> _pending = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Forget(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null || AuditScope.Current is null)
        {
            return;
        }

        var pending = new List<(EntityEntry, AuditChange)>();
        var byOwner = new Dictionary<EntityEntry, AuditChange>();
        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            // 存成 JSON 列的子对象（可用时段、改写规则、长上下文单价）算作主人的一个字段变了。
            if (entry.Metadata.IsOwned())
            {
                EntityEntry? owner = FindOwner(context, entry);
                if (owner is not null)
                {
                    string navigation = entry.Metadata.FindOwnership()?.PrincipalToDependent?.Name ?? entry.Metadata.ClrType.Name;
                    if (!byOwner.TryGetValue(owner, out AuditChange? ownerChange))
                    {
                        ownerChange = pending.FirstOrDefault(p => p.Item1.Entity == owner.Entity).Item2;
                        if (ownerChange is null)
                        {
                            ownerChange = Describe(owner, EntityState.Modified);
                            pending.Add((owner, ownerChange));
                        }

                        byOwner[owner] = ownerChange;
                    }

                    if (ownerChange.State == "modified")
                    {
                        ownerChange.Fields = Join(Split(ownerChange.Fields).Append(navigation));
                    }
                }

                continue;
            }

            if (pending.Any(p => p.Item1.Entity == entry.Entity))
            {
                AuditChange existing = pending.First(p => p.Item1.Entity == entry.Entity).Item2;
                if (entry.State == EntityState.Modified)
                {
                    existing.Fields = Join(Split(existing.Fields).Concat(ModifiedFields(entry)));
                }

                continue;
            }

            AuditChange change = Describe(entry, entry.State);
            if (change.State == "modified" && string.IsNullOrEmpty(change.Fields))
            {
                // 只动了簿记字段（例如只刷新了 UpdatedAt）。
                continue;
            }

            pending.Add((entry, change));
        }

        _pending.AddOrUpdate(context, pending);
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }

    private void Flush(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out List<(EntityEntry Entry, AuditChange Change)>? pending))
        {
            return;
        }

        _pending.Remove(context);
        if (AuditScope.Current is not { } scope)
        {
            return;
        }

        foreach ((EntityEntry entry, AuditChange change) in pending)
        {
            // 新增的行保存之后才有 id。
            change.EntityId ??= KeyOf(entry);
        }

        scope.Add(pending.Select(p => p.Change).Where(c => !(c.State == "modified" && string.IsNullOrEmpty(c.Fields))));
    }

    private static AuditChange Describe(EntityEntry entry, EntityState state)
    {
        bool deleted = state == EntityState.Deleted;
        string? name = entry.Metadata.FindProperty("Name") is { } nameProperty
            ? (deleted ? entry.Property(nameProperty.Name).OriginalValue : entry.Property(nameProperty.Name).CurrentValue)?.ToString()
            : null;

        return new AuditChange
        {
            Entity = entry.Metadata.ClrType.Name,
            EntityId = state == EntityState.Added ? null : KeyOf(entry),
            Name = name is { Length: > AuditEntry.MaxNameLength } ? name[..AuditEntry.MaxNameLength] : name,
            State = state switch
            {
                EntityState.Added => "added",
                EntityState.Deleted => "deleted",
                _ => "modified",
            },
            Fields = state == EntityState.Modified ? Join(ModifiedFields(entry)) : null,
        };
    }

    private static IEnumerable<string> ModifiedFields(EntityEntry entry) =>
        entry.Properties
            .Where(p => p.IsModified && !p.Metadata.IsPrimaryKey() && !Ignored.Contains(p.Metadata.Name))
            .Select(p => Sensitive.TryGetValue(p.Metadata.Name, out string? group) ? group : p.Metadata.Name);

    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return null;
        }

        string[] values = [.. key.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? "")];
        return values.All(v => v.Length == 0 || v == "0") ? null : string.Join("/", values);
    }

    private static EntityEntry? FindOwner(DbContext context, EntityEntry owned)
    {
        var ownership = owned.Metadata.FindOwnership();
        if (ownership is null)
        {
            return null;
        }

        foreach (EntityEntry candidate in context.ChangeTracker.Entries())
        {
            if (candidate.Metadata != ownership.PrincipalEntityType)
            {
                continue;
            }

            NavigationEntry navigation = candidate.Navigation(ownership.PrincipalToDependent!.Name);
            object? value = navigation.CurrentValue;
            if (value == owned.Entity
                || (value is System.Collections.IEnumerable items && items.Cast<object>().Contains(owned.Entity)))
            {
                return candidate;
            }

            // 删掉的子对象已经不在集合里了：按外键对回主人。
            if (owned.State == EntityState.Deleted && SameKey(owned, ownership, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool SameKey(EntityEntry owned, Microsoft.EntityFrameworkCore.Metadata.IForeignKey ownership, EntityEntry owner)
    {
        for (int i = 0; i < ownership.Properties.Count; i++)
        {
            object? foreign = owned.Property(ownership.Properties[i].Name).OriginalValue;
            object? principal = owner.Property(ownership.PrincipalKey.Properties[i].Name).CurrentValue;
            if (!Equals(foreign, principal))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Split(string? fields) =>
        string.IsNullOrEmpty(fields) ? [] : fields.Split(',');

    private static string? Join(IEnumerable<string> fields)
    {
        string[] distinct = [.. fields.Distinct(StringComparer.Ordinal)];
        return distinct.Length == 0 ? null : string.Join(",", distinct);
    }
}
