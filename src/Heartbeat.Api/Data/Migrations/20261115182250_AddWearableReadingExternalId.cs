using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWearableReadingExternalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Readings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readings_ExternalId",
                table: "Readings",
                column: "ExternalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Readings_ExternalId",
                table: "Readings");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Readings");
        }
    }
}
