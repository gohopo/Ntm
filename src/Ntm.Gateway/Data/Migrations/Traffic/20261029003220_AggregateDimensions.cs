using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class AggregateDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_hourly_aggregates_HourUtc_ChannelId_UpstreamModel_ApiTokenId",
                table: "hourly_aggregates");

            migrationBuilder.RenameColumn(
                name: "TtfbSampleCount",
                table: "hourly_aggregates",
                newName: "UnpricedCount");

            migrationBuilder.RenameColumn(
                name: "TtfbMillisecondsSum",
                table: "hourly_aggregates",
                newName: "TimeToFirstContentUnder8s");

            migrationBuilder.AddColumn<string>(
                name: "ApiTokenName",
                table: "hourly_aggregates",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CanceledCount",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "EstimatedCount",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FailedAttemptCount",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PublishedModel",
                table: "hourly_aggregates",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentMillisecondsSum",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentOver64s",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentSampleCount",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder16s",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder1s",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder250ms",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder2s",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder32s",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder4s",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder500ms",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeToFirstContentUnder64s",
                table: "hourly_aggregates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_hourly_aggregates_HourUtc_ChannelId_PublishedModel_UpstreamModel_ApiTokenId",
                table: "hourly_aggregates",
                columns: new[] { "HourUtc", "ChannelId", "PublishedModel", "UpstreamModel", "ApiTokenId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_hourly_aggregates_HourUtc_ChannelId_PublishedModel_UpstreamModel_ApiTokenId",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "ApiTokenName",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "CanceledCount",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "EstimatedCount",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "FailedAttemptCount",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "PublishedModel",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentMillisecondsSum",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentOver64s",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentSampleCount",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder16s",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder1s",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder250ms",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder2s",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder32s",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder4s",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder500ms",
                table: "hourly_aggregates");

            migrationBuilder.DropColumn(
                name: "TimeToFirstContentUnder64s",
                table: "hourly_aggregates");

            migrationBuilder.RenameColumn(
                name: "UnpricedCount",
                table: "hourly_aggregates",
                newName: "TtfbSampleCount");

            migrationBuilder.RenameColumn(
                name: "TimeToFirstContentUnder8s",
                table: "hourly_aggregates",
                newName: "TtfbMillisecondsSum");

            migrationBuilder.CreateIndex(
                name: "IX_hourly_aggregates_HourUtc_ChannelId_UpstreamModel_ApiTokenId",
                table: "hourly_aggregates",
                columns: new[] { "HourUtc", "ChannelId", "UpstreamModel", "ApiTokenId" },
                unique: true);
        }
    }
}
