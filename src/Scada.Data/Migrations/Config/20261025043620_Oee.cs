using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class Oee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DowntimeReason",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: true),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPlanned = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DowntimeReason", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DowntimeReason_Area_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Area",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OeeConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ThresholdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OeeRollupState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WatermarkTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunTs = table.Column<long>(type: "INTEGER", nullable: true),
                    RebuildFromTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeRollupState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionProduct",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: true),
                    IdealCycleSec = table.Column<double>(type: "REAL", nullable: false),
                    RatedUnitsPerHour = table.Column<double>(type: "REAL", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionProduct", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionProduct_Area_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Area",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionShift",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    EndMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionShift", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionShift_Area_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Area",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionLine",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StateTagId = table.Column<int>(type: "INTEGER", nullable: true),
                    StateMapJson = table.Column<string>(type: "TEXT", nullable: false),
                    GoodCountTagId = table.Column<int>(type: "INTEGER", nullable: true),
                    RejectCountTagId = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalCountTagId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true),
                    IdealCycleSec = table.Column<double>(type: "REAL", nullable: false),
                    RatedUnitsPerHour = table.Column<double>(type: "REAL", nullable: true),
                    TargetUnitsPerHour = table.Column<double>(type: "REAL", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionLine_Area_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Area",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionLine_ProductionProduct_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ProductionProduct",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProductionLine_Tag_GoodCountTagId",
                        column: x => x.GoodCountTagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProductionLine_Tag_RejectCountTagId",
                        column: x => x.RejectCountTagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProductionLine_Tag_StateTagId",
                        column: x => x.StateTagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProductionLine_Tag_TotalCountTagId",
                        column: x => x.TotalCountTagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DowntimeEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTs = table.Column<long>(type: "INTEGER", nullable: true),
                    ReasonId = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DowntimeEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DowntimeEvent_DowntimeReason_ReasonId",
                        column: x => x.ReasonId,
                        principalTable: "DowntimeReason",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DowntimeEvent_ProductionLine_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OeeLineSample",
                columns: table => new
                {
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    GoodCount = table.Column<double>(type: "REAL", nullable: true),
                    RejectCount = table.Column<double>(type: "REAL", nullable: true),
                    TotalCount = table.Column<double>(type: "REAL", nullable: true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeLineSample", x => x.LineId);
                    table.ForeignKey(
                        name: "FK_OeeLineSample_ProductionLine_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OeeRollup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Granularity = table.Column<int>(type: "INTEGER", nullable: false),
                    BucketTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlannedMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RunMs = table.Column<long>(type: "INTEGER", nullable: false),
                    DownMs = table.Column<long>(type: "INTEGER", nullable: false),
                    PlannedDownMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FaultMs = table.Column<long>(type: "INTEGER", nullable: false),
                    SetupMs = table.Column<long>(type: "INTEGER", nullable: false),
                    GoodCount = table.Column<double>(type: "REAL", nullable: false),
                    RejectCount = table.Column<double>(type: "REAL", nullable: false),
                    ScrapCount = table.Column<double>(type: "REAL", nullable: false),
                    IdealCycleSec = table.Column<double>(type: "REAL", nullable: false),
                    TargetCount = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeRollup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OeeRollup_ProductionLine_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductionCountCorrection",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true),
                    PeriodFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodTo = table.Column<long>(type: "INTEGER", nullable: false),
                    GoodDelta = table.Column<double>(type: "REAL", nullable: false),
                    RejectDelta = table.Column<double>(type: "REAL", nullable: false),
                    ScrapDelta = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionCountCorrection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionCountCorrection_ProductionLine_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductionCountCorrection_ProductionProduct_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ProductionProduct",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeEvent_AlarmEventId",
                table: "DowntimeEvent",
                column: "AlarmEventId");

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeEvent_LineId_EndTs",
                table: "DowntimeEvent",
                columns: new[] { "LineId", "EndTs" });

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeEvent_LineId_StartTs",
                table: "DowntimeEvent",
                columns: new[] { "LineId", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeEvent_ReasonId",
                table: "DowntimeEvent",
                column: "ReasonId");

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeReason_AreaId",
                table: "DowntimeReason",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeReason_Code",
                table: "DowntimeReason",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OeeRollup_Granularity_BucketTs_LineId_ProductId",
                table: "OeeRollup",
                columns: new[] { "Granularity", "BucketTs", "LineId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OeeRollup_LineId_Granularity_BucketTs",
                table: "OeeRollup",
                columns: new[] { "LineId", "Granularity", "BucketTs" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionCountCorrection_LineId_PeriodFrom",
                table: "ProductionCountCorrection",
                columns: new[] { "LineId", "PeriodFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionCountCorrection_ProductId",
                table: "ProductionCountCorrection",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLine_AreaId",
                table: "ProductionLine",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLine_GoodCountTagId",
                table: "ProductionLine",
                column: "GoodCountTagId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLine_Name",
                table: "ProductionLine",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLine_ProductId",
                table: "ProductionLine",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLine_RejectCountTagId",
                table: "ProductionLine",
                column: "RejectCountTagId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLine_StateTagId",
                table: "ProductionLine",
                column: "StateTagId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLine_TotalCountTagId",
                table: "ProductionLine",
                column: "TotalCountTagId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionProduct_AreaId",
                table: "ProductionProduct",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionProduct_Name",
                table: "ProductionProduct",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionShift_AreaId_Name",
                table: "ProductionShift",
                columns: new[] { "AreaId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DowntimeEvent");

            migrationBuilder.DropTable(
                name: "OeeConfig");

            migrationBuilder.DropTable(
                name: "OeeLineSample");

            migrationBuilder.DropTable(
                name: "OeeRollup");

            migrationBuilder.DropTable(
                name: "OeeRollupState");

            migrationBuilder.DropTable(
                name: "ProductionCountCorrection");

            migrationBuilder.DropTable(
                name: "ProductionShift");

            migrationBuilder.DropTable(
                name: "DowntimeReason");

            migrationBuilder.DropTable(
                name: "ProductionLine");

            migrationBuilder.DropTable(
                name: "ProductionProduct");
        }
    }
}
