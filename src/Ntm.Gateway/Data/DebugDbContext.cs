using Microsoft.EntityFrameworkCore;
using Ntm.Gateway.Debugging;

namespace Ntm.Gateway.Data;

/// <summary>
/// <c>ntm-debug.db</c>：调试原文（Q153）。单独一个库，才能按 24 小时狠狠清、还能 VACUUM，
/// 不至于把请求记录那个库撑大。
/// </summary>
public class DebugDbContext(DbContextOptions<DebugDbContext> options) : DbContext(options)
{
    public DbSet<DebugPayload> DebugPayloads => Set<DebugPayload>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        var timestamp = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));

        modelBuilder.Entity<DebugPayload>(entity =>
        {
            entity.ToTable("debug_payloads");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CapturedAt).HasConversion(timestamp);
            entity.Property(e => e.Headers);
            entity.Property(e => e.Body);

            // 详情页按 Request 取，清理按时间取。
            entity.HasIndex(e => e.RequestId);
            entity.HasIndex(e => e.CapturedAt);
        });
    }
}
