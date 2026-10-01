using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VisionSupport.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceDiagnosticEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDiagnosticEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceDiagnosticEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceHealthSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatteryPercent = table.Column<int>(type: "integer", nullable: true),
                    IsCharging = table.Column<bool>(type: "boolean", nullable: true),
                    NetworkConnected = table.Column<bool>(type: "boolean", nullable: false),
                    NetworkType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WifiSignalLevel = table.Column<int>(type: "integer", nullable: true),
                    StorageTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    StorageUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                    StorageAvailableBytes = table.Column<long>(type: "bigint", nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AndroidVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHealthSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceHealthSnapshots_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDiagnosticEvents_DeviceId",
                table: "DeviceDiagnosticEvents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHealthSnapshots_DeviceId",
                table: "DeviceHealthSnapshots",
                column: "DeviceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceDiagnosticEvents");

            migrationBuilder.DropTable(
                name: "DeviceHealthSnapshots");
        }
    }
}
