using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VideoFrameSampling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FrameSamplings",
                schema: "video",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneAnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    SamplesPerScene = table.Column<int>(type: "integer", nullable: false),
                    Profile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArtifactToken = table.Column<Guid>(type: "uuid", nullable: true),
                    FfmpegVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrameSamplings", x => x.Id);
                    table.CheckConstraint("CK_Sampling_Completion", "(\"CompletedAt\" IS NULL AND \"ArtifactToken\" IS NULL AND \"FfmpegVersion\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"ArtifactToken\" IS NOT NULL AND \"FfmpegVersion\" IS NOT NULL)");
                    table.CheckConstraint("CK_Sampling_Settings", "\"SamplesPerScene\" BETWEEN 1 AND 3 AND \"MaxAttempts\" BETWEEN 1 AND 5 AND \"Profile\" = 'png-rgb24-320x180-frame-quantiles-v1'");
                    table.ForeignKey(
                        name: "FK_FrameSamplings_SceneAnalyses_SceneAnalysisId",
                        column: x => x.SceneAnalysisId,
                        principalSchema: "video",
                        principalTable: "SceneAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FrameSamples",
                schema: "video",
                columns: table => new
                {
                    SamplingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    SceneOrdinal = table.Column<int>(type: "integer", nullable: false),
                    FrameIndex = table.Column<int>(type: "integer", nullable: false),
                    StartSeconds = table.Column<double>(type: "double precision", nullable: false),
                    EndSeconds = table.Column<double>(type: "double precision", nullable: false),
                    SourceSeconds = table.Column<double>(type: "double precision", nullable: true),
                    NormalizedSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrameSamples", x => new { x.SamplingId, x.Ordinal });
                    table.CheckConstraint("CK_Sample_Plan", "\"Ordinal\" BETWEEN 0 AND 299 AND \"SceneOrdinal\" >= 0 AND \"FrameIndex\" >= 0 AND \"StartSeconds\" >= 0 AND \"EndSeconds\" > \"StartSeconds\"");
                    table.CheckConstraint("CK_Sample_Result", "(\"SourceSeconds\" IS NULL AND \"NormalizedSeconds\" IS NULL AND \"Sha256\" IS NULL AND \"SizeBytes\" IS NULL) OR (\"SourceSeconds\" IS NOT NULL AND \"NormalizedSeconds\" IS NOT NULL AND \"NormalizedSeconds\" >= \"StartSeconds\" - 0.000001 AND \"NormalizedSeconds\" < \"EndSeconds\" AND \"Sha256\" IS NOT NULL AND \"Sha256\" ~ '^[0-9a-f]{64}$' AND \"SizeBytes\" IS NOT NULL AND \"SizeBytes\" BETWEEN 1 AND 4194304)");
                    table.ForeignKey(
                        name: "FK_FrameSamples_FrameSamplings_SamplingId",
                        column: x => x.SamplingId,
                        principalSchema: "video",
                        principalTable: "FrameSamplings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FrameSamplings_JobId",
                schema: "video",
                table: "FrameSamplings",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FrameSamplings_SceneAnalysisId_CreatedAt",
                schema: "video",
                table: "FrameSamplings",
                columns: new[] { "SceneAnalysisId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FrameSamplings_SceneAnalysisId_IdempotencyKey",
                schema: "video",
                table: "FrameSamplings",
                columns: new[] { "SceneAnalysisId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FrameSamples",
                schema: "video");

            migrationBuilder.DropTable(
                name: "FrameSamplings",
                schema: "video");
        }
    }
}
