using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileNameCollation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_Name",
                table: "Profiles");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Profiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                collation: "und-ci",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Name",
                table: "Profiles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Profiles_Name",
                table: "Profiles");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Profiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldCollation: "und-ci");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Name",
                table: "Profiles",
                column: "Name",
                unique: true)
                .Annotation("Relational:Collation", new[] { "und-ci" });
        }
    }
}
