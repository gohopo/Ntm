using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class Restrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DailyCostLimitNanoUsd",
                table: "channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeWindows",
                table: "channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllowedModels",
                table: "channel_keys",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrency",
                table: "channel_keys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "channel_keys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllowedChannelIds",
                table: "api_keys",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AllowedModels",
                table: "api_keys",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "BlockedChannelIds",
                table: "api_keys",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrency",
                table: "api_keys",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DailyCostLimitNanoUsd",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "TimeWindows",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "AllowedModels",
                table: "channel_keys");

            migrationBuilder.DropColumn(
                name: "MaxConcurrency",
                table: "channel_keys");

            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "channel_keys");

            migrationBuilder.DropColumn(
                name: "AllowedChannelIds",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "AllowedModels",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "BlockedChannelIds",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "MaxConcurrency",
                table: "api_keys");
        }
    }
}
