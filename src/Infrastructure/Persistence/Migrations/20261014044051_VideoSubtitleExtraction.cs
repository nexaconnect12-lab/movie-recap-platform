using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VideoSubtitleExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Binding_SchemaVersion",
                schema: "video",
                table: "PreparationValidations",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "Binding_SubtitleId",
                schema: "video",
                table: "PreparationValidations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Binding_SchemaVersion",
                schema: "video",
                table: "PreparationAudits",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "Binding_SubtitleId",
                schema: "video",
                table: "PreparationAudits",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SubtitleExtractions",
                schema: "video",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    SubtitleIndex = table.Column<int>(type: "integer", nullable: false),
                    SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    Profile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArtifactToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ArtifactSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    CueCount = table.Column<int>(type: "integer", nullable: true),
                    FfmpegVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubtitleExtractions", x => x.Id);
                    table.CheckConstraint("CK_Subtitle_Binding", "\"SubtitleIndex\" >= 0 AND \"SourceSizeBytes\" > 0 AND \"SourceSha256\" ~ '^[0-9a-f]{64}$' AND \"MaxAttempts\" BETWEEN 1 AND 5 AND \"Profile\" = 'srt-utf8-source-timestamps-v1'");
                    table.CheckConstraint("CK_Subtitle_Completion", "(\"CompletedAt\" IS NULL AND \"ArtifactToken\" IS NULL AND \"ArtifactSha256\" IS NULL AND \"ArtifactSizeBytes\" IS NULL AND \"CueCount\" IS NULL AND \"FfmpegVersion\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"ArtifactToken\" IS NOT NULL AND \"ArtifactSha256\" IS NOT NULL AND \"ArtifactSha256\" ~ '^[0-9a-f]{64}$' AND \"CueCount\" IS NOT NULL AND \"CueCount\" BETWEEN 1 AND 100000 AND \"ArtifactSizeBytes\" BETWEEN 1 AND 16777216 AND \"ArtifactSizeBytes\" IS NOT NULL AND \"FfmpegVersion\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "SubtitleCues",
                schema: "video",
                columns: table => new
                {
                    ExtractionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    StartMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    EndMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubtitleCues", x => new { x.ExtractionId, x.Ordinal });
                    table.CheckConstraint("CK_Subtitle_Cue", "\"Ordinal\" BETWEEN 0 AND 99999 AND \"StartMilliseconds\" >= 0 AND \"EndMilliseconds\" > \"StartMilliseconds\" AND \"EndMilliseconds\" < 360000000 AND length(\"Text\") BETWEEN 1 AND 4096");
                    table.ForeignKey(
                        name: "FK_SubtitleCues_SubtitleExtractions_ExtractionId",
                        column: x => x.ExtractionId,
                        principalSchema: "video",
                        principalTable: "SubtitleExtractions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleExtractions_ImportId_CreatedAt",
                schema: "video",
                table: "SubtitleExtractions",
                columns: new[] { "ImportId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleExtractions_ImportId_IdempotencyKey",
                schema: "video",
                table: "SubtitleExtractions",
                columns: new[] { "ImportId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleExtractions_JobId",
                schema: "video",
                table: "SubtitleExtractions",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubtitleCues",
                schema: "video");

            migrationBuilder.DropTable(
                name: "SubtitleExtractions",
                schema: "video");

            migrationBuilder.DropColumn(
                name: "Binding_SchemaVersion",
                schema: "video",
                table: "PreparationValidations");

            migrationBuilder.DropColumn(
                name: "Binding_SubtitleId",
                schema: "video",
                table: "PreparationValidations");

            migrationBuilder.DropColumn(
                name: "Binding_SchemaVersion",
                schema: "video",
                table: "PreparationAudits");

            migrationBuilder.DropColumn(
                name: "Binding_SubtitleId",
                schema: "video",
                table: "PreparationAudits");
        }
    }
}
