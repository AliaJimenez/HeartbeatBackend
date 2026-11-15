using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentProfileAndRadiationType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "RadiationDoses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrewSize",
                table: "Missions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "DistanceKm",
                table: "Missions",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentType",
                table: "Missions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "GravityG",
                table: "Missions",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "HabitableVolumeM3",
                table: "Missions",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "EnvironmentReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AstronautId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IndicatorId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<decimal>(type: "TEXT", nullable: false),
                    SecondaryValue = table.Column<decimal>(type: "TEXT", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Simulated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnvironmentReadings_Astronauts_AstronautId",
                        column: x => x.AstronautId,
                        principalTable: "Astronauts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EnvironmentReadings_Missions_MissionId",
                        column: x => x.MissionId,
                        principalTable: "Missions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentReadings_AstronautId",
                table: "EnvironmentReadings",
                column: "AstronautId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentReadings_MissionId",
                table: "EnvironmentReadings",
                column: "MissionId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentReadings_RecordedAt",
                table: "EnvironmentReadings",
                column: "RecordedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnvironmentReadings");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "RadiationDoses");

            migrationBuilder.DropColumn(
                name: "CrewSize",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "DistanceKm",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "EnvironmentType",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "GravityG",
                table: "Missions");

            migrationBuilder.DropColumn(
                name: "HabitableVolumeM3",
                table: "Missions");
        }
    }
}
