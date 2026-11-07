using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class SiteSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ServerName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RegistrationOpen = table.Column<bool>(type: "boolean", nullable: false),
                    RequireConfirmedEmail = table.Column<bool>(type: "boolean", nullable: false),
                    TokenLifetimeDays = table.Column<int>(type: "integer", nullable: false),
                    MaxTokensPerUser = table.Column<int>(type: "integer", nullable: false),
                    SmtpEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpSecurity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPasswordProtected = table.Column<string>(type: "text", nullable: true),
                    MailFromAddress = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MailFromName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteSettings");
        }
    }
}
