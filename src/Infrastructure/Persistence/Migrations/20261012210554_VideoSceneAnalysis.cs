using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VideoSceneAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "video");

            migrationBuilder.CreateTable(
                name: "SceneAnalyses",
                schema: "video",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoIndex = table.Column<int>(type: "integer", nullable: false),
                    SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    MinimumSceneFrames = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalFrames = table.Column<int>(type: "integer", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    SourceOffsetSeconds = table.Column<double>(type: "double precision", nullable: true),
                    VariableFrameRate = table.Column<bool>(type: "boolean", nullable: true),
                    TimingMode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SceneDetectVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PyAvVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OpenCvVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PythonVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneAnalyses", x => x.Id);
                    table.CheckConstraint("CK_Analysis_Binding", "\"VideoIndex\" >= 0 AND \"SourceSizeBytes\" > 0 AND \"SourceSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_Analysis_Result", "(\"CompletedAt\" IS NULL AND \"TotalFrames\" IS NULL AND \"DurationSeconds\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"TotalFrames\" IS NOT NULL AND \"TotalFrames\" > 0 AND \"DurationSeconds\" IS NOT NULL AND \"DurationSeconds\" > 0)");
                    table.CheckConstraint("CK_Analysis_Settings", "\"Threshold\" BETWEEN 1 AND 255 AND \"MinimumSceneFrames\" BETWEEN 1 AND 3000 AND \"MaxAttempts\" BETWEEN 1 AND 5");
                });

            migrationBuilder.CreateTable(
                name: "MovieScenes",
                schema: "video",
                columns: table => new
                {
                    AnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    StartFrame = table.Column<int>(type: "integer", nullable: false),
                    EndFrame = table.Column<int>(type: "integer", nullable: false),
                    StartSeconds = table.Column<double>(type: "double precision", nullable: false),
                    EndSeconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieScenes", x => new { x.AnalysisId, x.Ordinal });
                    table.CheckConstraint("CK_Scene_Range", "\"Ordinal\" >= 0 AND \"StartFrame\" >= 0 AND \"EndFrame\" > \"StartFrame\" AND \"StartSeconds\" >= 0 AND \"EndSeconds\" > \"StartSeconds\"");
                    table.ForeignKey(
                        name: "FK_MovieScenes_SceneAnalyses_AnalysisId",
                        column: x => x.AnalysisId,
                        principalSchema: "video",
                        principalTable: "SceneAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SceneAnalyses_ImportId_CreatedAt",
                schema: "video",
                table: "SceneAnalyses",
                columns: new[] { "ImportId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SceneAnalyses_ImportId_IdempotencyKey",
                schema: "video",
                table: "SceneAnalyses",
                columns: new[] { "ImportId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneAnalyses_JobId",
                schema: "video",
                table: "SceneAnalyses",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieScenes",
                schema: "video");

            migrationBuilder.DropTable(
                name: "SceneAnalyses",
                schema: "video");
        }
    }
}
