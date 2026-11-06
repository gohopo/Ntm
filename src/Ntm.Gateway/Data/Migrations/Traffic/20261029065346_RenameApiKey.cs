using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class RenameApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ApiTokenName",
                table: "requests",
                newName: "ApiKeyName");

            migrationBuilder.RenameColumn(
                name: "ApiTokenId",
                table: "requests",
                newName: "ApiKeyId");

            migrationBuilder.RenameIndex(
                name: "IX_requests_ApiTokenId_StartedAt",
                table: "requests",
                newName: "IX_requests_ApiKeyId_StartedAt");

            migrationBuilder.RenameColumn(
                name: "ApiTokenName",
                table: "hourly_aggregates",
                newName: "ApiKeyName");

            migrationBuilder.RenameColumn(
                name: "ApiTokenId",
                table: "hourly_aggregates",
                newName: "ApiKeyId");

            migrationBuilder.RenameIndex(
                name: "IX_hourly_aggregates_HourUtc_ChannelId_PublishedModel_UpstreamModel_ApiTokenId",
                table: "hourly_aggregates",
                newName: "IX_hourly_aggregates_HourUtc_ChannelId_PublishedModel_UpstreamModel_ApiKeyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ApiKeyName",
                table: "requests",
                newName: "ApiTokenName");

            migrationBuilder.RenameColumn(
                name: "ApiKeyId",
                table: "requests",
                newName: "ApiTokenId");

            migrationBuilder.RenameIndex(
                name: "IX_requests_ApiKeyId_StartedAt",
                table: "requests",
                newName: "IX_requests_ApiTokenId_StartedAt");

            migrationBuilder.RenameColumn(
                name: "ApiKeyName",
                table: "hourly_aggregates",
                newName: "ApiTokenName");

            migrationBuilder.RenameColumn(
                name: "ApiKeyId",
                table: "hourly_aggregates",
                newName: "ApiTokenId");

            migrationBuilder.RenameIndex(
                name: "IX_hourly_aggregates_HourUtc_ChannelId_PublishedModel_UpstreamModel_ApiKeyId",
                table: "hourly_aggregates",
                newName: "IX_hourly_aggregates_HourUtc_ChannelId_PublishedModel_UpstreamModel_ApiTokenId");
        }
    }
}
