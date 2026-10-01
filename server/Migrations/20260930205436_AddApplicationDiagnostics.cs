using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisionSupport.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "DeviceDiagnosticEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OccurredAt",
                table: "DeviceDiagnosticEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAppVersion",
                table: "DeviceDiagnosticEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePackage",
                table: "DeviceDiagnosticEvents",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Code",
                table: "DeviceDiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "OccurredAt",
                table: "DeviceDiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "SourceAppVersion",
                table: "DeviceDiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "SourcePackage",
                table: "DeviceDiagnosticEvents");
        }
    }
}
