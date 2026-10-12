using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VideoAudioExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudioExtractions",
                schema: "video",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    AudioIndex = table.Column<int>(type: "integer", nullable: false),
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
                    Samples = table.Column<long>(type: "bigint", nullable: true),
                    FfmpegVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioExtractions", x => x.Id);
                    table.CheckConstraint("CK_Audio_Binding", "\"AudioIndex\" >= 0 AND \"SourceSizeBytes\" > 0 AND \"SourceSha256\" ~ '^[0-9a-f]{64}$' AND \"MaxAttempts\" BETWEEN 1 AND 5 AND \"Profile\" = 'wav-pcm-s16le-16000-mono-v1'");
                    table.CheckConstraint("CK_Audio_Completion", "(\"CompletedAt\" IS NULL AND \"ArtifactToken\" IS NULL AND \"ArtifactSha256\" IS NULL AND \"ArtifactSizeBytes\" IS NULL AND \"Samples\" IS NULL AND \"FfmpegVersion\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"ArtifactToken\" IS NOT NULL AND \"ArtifactSha256\" IS NOT NULL AND \"ArtifactSha256\" ~ '^[0-9a-f]{64}$' AND \"Samples\" IS NOT NULL AND \"Samples\" > 0 AND \"Samples\" <= 2147483629 AND \"ArtifactSizeBytes\" IS NOT NULL AND \"ArtifactSizeBytes\" = 44 + 2 * \"Samples\" AND \"FfmpegVersion\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudioExtractions_ImportId_CreatedAt",
                schema: "video",
                table: "AudioExtractions",
                columns: new[] { "ImportId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AudioExtractions_ImportId_IdempotencyKey",
                schema: "video",
                table: "AudioExtractions",
                columns: new[] { "ImportId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AudioExtractions_JobId",
                schema: "video",
                table: "AudioExtractions",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioExtractions",
                schema: "video");
        }
    }
}
