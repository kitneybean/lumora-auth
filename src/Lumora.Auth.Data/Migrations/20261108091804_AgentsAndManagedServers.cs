using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentsAndManagedServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "Servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoRestart",
                table: "Servers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoStart",
                table: "Servers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupTimes",
                table: "Servers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupsToKeep",
                table: "Servers",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EulaAcceptedAt",
                table: "Servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EulaAcceptedBy",
                table: "Servers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtraJvmArgs",
                table: "Servers",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MemoryMb",
                table: "Servers",
                type: "integer",
                nullable: false,
                defaultValue: 4096);

            migrationBuilder.AddColumn<string>(
                name: "RestartTimes",
                table: "Servers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServerProperties",
                table: "Servers",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedServerManifestJson",
                table: "Builds",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentJoinTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentJoinTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Os = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Hostname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CpuCount = table.Column<int>(type: "integer", nullable: false),
                    MemoryMb = table.Column<long>(type: "bigint", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Servers_AgentId",
                table: "Servers",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentJoinTokens_TokenHash",
                table: "AgentJoinTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_KeyHash",
                table: "Agents",
                column: "KeyHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Servers_Agents_AgentId",
                table: "Servers",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Servers_Agents_AgentId",
                table: "Servers");

            migrationBuilder.DropTable(
                name: "AgentJoinTokens");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Servers_AgentId",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "AutoRestart",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "AutoStart",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "BackupTimes",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "BackupsToKeep",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "EulaAcceptedAt",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "EulaAcceptedBy",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "ExtraJvmArgs",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "MemoryMb",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "RestartTimes",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "ServerProperties",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "PublishedServerManifestJson",
                table: "Builds");
        }
    }
}
