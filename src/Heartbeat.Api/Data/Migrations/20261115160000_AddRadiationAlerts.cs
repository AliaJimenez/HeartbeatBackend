using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRadiationAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A radiation alert (SPE event or critical cumulative dose) has no check-up behind it.
            migrationBuilder.AlterColumn<Guid>(
                name: "CheckUpId",
                table: "Alerts",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Alerts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "Alerts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RadiationDoseId",
                table: "Alerts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"Alerts\" WHERE \"CheckUpId\" IS NULL;");

            migrationBuilder.DropColumn(name: "Kind", table: "Alerts");
            migrationBuilder.DropColumn(name: "Message", table: "Alerts");
            migrationBuilder.DropColumn(name: "RadiationDoseId", table: "Alerts");

            migrationBuilder.AlterColumn<Guid>(
                name: "CheckUpId",
                table: "Alerts",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
