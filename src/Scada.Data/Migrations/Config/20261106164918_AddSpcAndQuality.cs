using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddSpcAndQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QualityDefectCode",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: ""),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityDefectCode", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpcChart",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChartType = table.Column<int>(type: "INTEGER", nullable: false),
                    SubgroupSize = table.Column<int>(type: "INTEGER", nullable: false),
                    SamplingIntervalSec = table.Column<int>(type: "INTEGER", nullable: false),
                    BaselineSubgroups = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoLimits = table.Column<bool>(type: "INTEGER", nullable: false),
                    CenterLine = table.Column<double>(type: "REAL", nullable: true),
                    UpperControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    LowerControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    RangeCenter = table.Column<double>(type: "REAL", nullable: true),
                    RangeUpperControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    RangeLowerControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    LimitsEstablishedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    SpecUpperLimit = table.Column<double>(type: "REAL", nullable: true),
                    SpecLowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    RuleBeyond3Sigma = table.Column<bool>(type: "INTEGER", nullable: false),
                    RuleTwoOfThreeBeyond2Sigma = table.Column<bool>(type: "INTEGER", nullable: false),
                    RuleFourOfFiveBeyond1Sigma = table.Column<bool>(type: "INTEGER", nullable: false),
                    RuleEightOnOneSide = table.Column<bool>(type: "INTEGER", nullable: false),
                    RuleRangeBeyondLimit = table.Column<bool>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpcChart", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpcChart_Tag_TagId",
                        column: x => x.TagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspection",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: ""),
                    BatchNo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    Product = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    DefectCodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Inspector = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityInspection_Device_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Device",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_QualityInspection_QualityDefectCode_DefectCodeId",
                        column: x => x.DefectCodeId,
                        principalTable: "QualityDefectCode",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_QualityInspection_Tag_TagId",
                        column: x => x.TagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualityDefectCode_Code",
                table: "QualityDefectCode",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityDefectCode_Enabled",
                table: "QualityDefectCode",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_AreaId_Ts",
                table: "QualityInspection",
                columns: new[] { "AreaId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_BatchNo_Ts",
                table: "QualityInspection",
                columns: new[] { "BatchNo", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_DefectCodeId",
                table: "QualityInspection",
                column: "DefectCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_DeviceId",
                table: "QualityInspection",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_Result_Ts",
                table: "QualityInspection",
                columns: new[] { "Result", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_Sn_Ts",
                table: "QualityInspection",
                columns: new[] { "Sn", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_TagId",
                table: "QualityInspection",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_SpcChart_AreaId",
                table: "SpcChart",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_SpcChart_TagId",
                table: "SpcChart",
                column: "TagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityInspection");

            migrationBuilder.DropTable(
                name: "SpcChart");

            migrationBuilder.DropTable(
                name: "QualityDefectCode");
        }
    }
}
