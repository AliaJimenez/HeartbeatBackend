using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReadingSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceId",
                table: "Readings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Readings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "Readings");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Readings");
        }
    }
}
