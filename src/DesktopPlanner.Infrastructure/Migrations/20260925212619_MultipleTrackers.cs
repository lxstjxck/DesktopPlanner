using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DesktopPlanner.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MultipleTrackers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Layouts_WidgetType",
                table: "Layouts");

            migrationBuilder.DropIndex(
                name: "IX_HabitDayMarks_Date",
                table: "HabitDayMarks");

            migrationBuilder.AddColumn<string>(
                name: "TrackerColorHex",
                table: "Layouts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TrackerId",
                table: "Layouts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TrackerTitle",
                table: "Layouts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TrackerId",
                table: "HabitDayMarks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Existing data belongs to the original tracker and must remain visible
            // after the feature is upgraded.
            migrationBuilder.Sql("UPDATE Layouts SET TrackerId = '00000000000000000000000000000000', TrackerTitle = 'Трекер', TrackerColorHex = '#5CC8FF' WHERE WidgetType = 5;");
            migrationBuilder.Sql("UPDATE HabitDayMarks SET TrackerId = '00000000000000000000000000000000';");

            migrationBuilder.CreateIndex(
                name: "IX_Layouts_WidgetType_TrackerId",
                table: "Layouts",
                columns: new[] { "WidgetType", "TrackerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HabitDayMarks_TrackerId_Date",
                table: "HabitDayMarks",
                columns: new[] { "TrackerId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Layouts_WidgetType_TrackerId",
                table: "Layouts");

            migrationBuilder.DropIndex(
                name: "IX_HabitDayMarks_TrackerId_Date",
                table: "HabitDayMarks");

            migrationBuilder.DropColumn(
                name: "TrackerColorHex",
                table: "Layouts");

            migrationBuilder.DropColumn(
                name: "TrackerId",
                table: "Layouts");

            migrationBuilder.DropColumn(
                name: "TrackerTitle",
                table: "Layouts");

            migrationBuilder.DropColumn(
                name: "TrackerId",
                table: "HabitDayMarks");

            migrationBuilder.CreateIndex(
                name: "IX_Layouts_WidgetType",
                table: "Layouts",
                column: "WidgetType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HabitDayMarks_Date",
                table: "HabitDayMarks",
                column: "Date",
                unique: true);
        }
    }
}
