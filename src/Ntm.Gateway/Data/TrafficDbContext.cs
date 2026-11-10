using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Data;

/// <summary>
/// <c>ntm-traffic.db</c>：请求记录、尝试记录、小时汇总和操作记录（ADR-0004、Q358）。
/// 汇总和记录在同一个事务里写，崩溃最多丢最后一个批次，而且两者一起丢。
/// 名字故意不叫 logs——它是账，不是可以随手删的垃圾。
/// 索引刻意少：ccLoad 在 logs 上挂了 11 个索引，每条记录的写入成本都摊在上面。
/// </summary>
public sealed class TrafficDbContext(DbContextOptions<TrafficDbContext> options) : DbContext(options)
{
    public DbSet<RequestRecord> Requests => Set<RequestRecord>();

    public DbSet<AttemptRecord> Attempts => Set<AttemptRecord>();

    public DbSet<HourlyAggregate> HourlyAggregates => Set<HourlyAggregate>();

    /// <summary>上游 id 落在哪个渠道密钥上（Q269，ADR-0020）。</summary>
    public DbSet<Routing.ResponseLanding> ResponseLandings => Set<Routing.ResponseLanding>();

    /// <summary>管理员的操作记录（Q358）。</summary>
    public DbSet<Audit.AuditEntry> AuditEntries => Set<Audit.AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var timestamp = new UnixMillisecondsConverter();
        var nullableTimestamp = new NullableUnixMillisecondsConverter();

        modelBuilder.Entity<RequestRecord>(entity =>
        {
            entity.ToTable("requests");
            entity.HasKey(e => e.Id);

            // 日志列表、按时间区间过滤、按保留期删除都走这个索引。
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.ApiKeyId, e.StartedAt });
            entity.Property(e => e.StartedAt).HasConversion(timestamp);
            entity.Property(e => e.FinishedAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.CommittedAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.ApiKeyName).HasMaxLength(200);
            entity.Property(e => e.PublishedModel).HasMaxLength(300);
            entity.Property(e => e.BlockDetail).HasMaxLength(RequestRecord.MaxBlockDetailLength);
            entity.OwnsMany(e => e.Skipped, skipped => skipped.ToJson());
            entity.HasMany(e => e.Attempts)
                .WithOne(e => e.Request!)
                .HasForeignKey(e => e.RequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AttemptRecord>(entity =>
        {
            entity.ToTable("attempts");
            entity.HasKey(e => e.Id);

            // 展开一条请求的链路。
            entity.HasIndex(e => new { e.RequestId, e.Ordinal });

            // 渠道健康统计只读已有的尝试记录，不额外探测。
            entity.HasIndex(e => new { e.ChannelId, e.StartedAt });
            entity.Property(e => e.StartedAt).HasConversion(timestamp);
            entity.Property(e => e.FinishedAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.FirstEventAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.FirstContentEventAt).HasConversion(nullableTimestamp);
            entity.Property(e => e.CooldownUntil).HasConversion(nullableTimestamp);
            entity.Property(e => e.ChannelName).HasMaxLength(200);
            entity.Property(e => e.ChannelKeyFingerprint).HasMaxLength(100);
            entity.Property(e => e.ChannelKeyPrefix).HasMaxLength(50);
            entity.Property(e => e.UpstreamModel).HasMaxLength(300);
            entity.Property(e => e.RequestUri).HasMaxLength(1000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.ResponseId).HasMaxLength(Routing.ResponseLanding.MaxIdLength);
        });

        modelBuilder.Entity<HourlyAggregate>(entity =>
        {
            entity.ToTable("hourly_aggregates");
            entity.HasKey(e => e.Id);

            // upsert 的目标键。ApiKeyId 用 0 表示没有 API 密钥，不用 null：
            // SQLite 的唯一索引里 null 彼此不相等，每行都会算成新行。
            entity.HasIndex(e => new
            {
                e.HourUtc,
                e.ChannelId,
                e.PublishedModel,
                e.UpstreamModel,
                e.ApiKeyId,
            }).IsUnique();
            entity.Property(e => e.HourUtc).HasConversion(timestamp);
            entity.Property(e => e.ChannelName).HasMaxLength(200);
            entity.Property(e => e.PublishedModel).HasMaxLength(300);
            entity.Property(e => e.UpstreamModel).HasMaxLength(300);
            entity.Property(e => e.ApiKeyName).HasMaxLength(200);
        });

        modelBuilder.Entity<Routing.ResponseLanding>(entity =>
        {
            entity.ToTable("response_landings");
            entity.HasKey(e => e.Id);

            // 过期清理按最后使用时间删（Q285）。
            entity.HasIndex(e => e.LastUsedAt);

            // 后台轮询每轮只找还在等用量的（Q305）：部分索引，只收那几条。
            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("IX_response_landings_AwaitingUsage")
                .HasFilter("\"AwaitingUsage\" = 1");
            entity.Property(e => e.Id).HasMaxLength(Routing.ResponseLanding.MaxIdLength);
            entity.Property(e => e.KeyFingerprint).HasMaxLength(100);
            entity.Property(e => e.UpstreamModel).HasMaxLength(300);
            entity.Property(e => e.PublishedModel).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasConversion(timestamp);
            entity.Property(e => e.LastUsedAt).HasConversion(timestamp);
        });

        modelBuilder.Entity<Audit.AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(e => e.Id);

            // 列表按 id 倒序翻页；保留期清理按时间删。按动作过滤的量很小，不单独建索引。
            entity.HasIndex(e => e.OccurredAt);
            entity.Property(e => e.OccurredAt).HasConversion(timestamp);
            entity.Property(e => e.RemoteAddress).HasMaxLength(64);
            entity.Property(e => e.Action).HasMaxLength(Audit.AuditEntry.MaxActionLength);
            entity.Property(e => e.TargetType).HasMaxLength(64);
            entity.Property(e => e.TargetId).HasMaxLength(64);
            entity.Property(e => e.TargetName).HasMaxLength(Audit.AuditEntry.MaxNameLength);
            entity.OwnsMany(e => e.Changes, changes => changes.ToJson());
        });

        base.OnModelCreating(modelBuilder);
    }
}
