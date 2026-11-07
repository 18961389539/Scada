using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddAssetsAndMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Asset",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: ""),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    Manufacturer = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    SerialNumber = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    Location = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CommissionedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    WarrantyUntilTs = table.Column<long>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asset", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Asset_Device_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Device",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SparePart",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Specification = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: ""),
                    Stock = table.Column<double>(type: "REAL", nullable: false),
                    MinStock = table.Column<double>(type: "REAL", nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    Supplier = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    UnitPrice = table.Column<double>(type: "REAL", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SparePart", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWorkOrder",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false, defaultValue: ""),
                    Assignee = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    Reporter = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    PlannedStartTs = table.Column<long>(type: "INTEGER", nullable: true),
                    PlannedEndTs = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    DowntimeMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false, defaultValue: ""),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWorkOrder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceWorkOrder_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MaintenanceWorkOrder_Device_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Device",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SparePartMovement",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PartId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<double>(type: "REAL", nullable: false),
                    BalanceAfter = table.Column<double>(type: "REAL", nullable: false),
                    WorkOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    Operator = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SparePartMovement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SparePartMovement_SparePart_PartId",
                        column: x => x.PartId,
                        principalTable: "SparePart",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Asset_AreaId",
                table: "Asset",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_Asset_Code",
                table: "Asset",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Asset_DeviceId",
                table: "Asset",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWorkOrder_AreaId_Status",
                table: "MaintenanceWorkOrder",
                columns: new[] { "AreaId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWorkOrder_AssetId",
                table: "MaintenanceWorkOrder",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWorkOrder_DeviceId",
                table: "MaintenanceWorkOrder",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWorkOrder_Number",
                table: "MaintenanceWorkOrder",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWorkOrder_Status",
                table: "MaintenanceWorkOrder",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SparePart_Code",
                table: "SparePart",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SparePart_Enabled",
                table: "SparePart",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_SparePartMovement_PartId_Ts",
                table: "SparePartMovement",
                columns: new[] { "PartId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_SparePartMovement_WorkOrderId",
                table: "SparePartMovement",
                column: "WorkOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceWorkOrder");

            migrationBuilder.DropTable(
                name: "SparePartMovement");

            migrationBuilder.DropTable(
                name: "Asset");

            migrationBuilder.DropTable(
                name: "SparePart");
        }
    }
}
