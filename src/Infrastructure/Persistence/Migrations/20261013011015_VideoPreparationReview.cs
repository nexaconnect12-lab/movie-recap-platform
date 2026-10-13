using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VideoPreparationReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreparationAudits",
                schema: "video",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BeforeVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    AfterVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ValidationId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Binding_ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Binding_AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_InventoryVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_SelectionVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_VideoIndex = table.Column<int>(type: "integer", nullable: true),
                    Binding_AudioIndex = table.Column<int>(type: "integer", nullable: true),
                    Binding_SubtitleIndex = table.Column<int>(type: "integer", nullable: true),
                    Binding_SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Binding_SourceSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Binding_SceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_AudioId = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_FrameId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PreparationReviews",
                schema: "video",
                columns: table => new
                {
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastValidationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ValidationSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ValidationPassed = table.Column<bool>(type: "boolean", nullable: true),
                    DecisionValidationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparationReviews", x => x.ImportId);
                    table.CheckConstraint("CK_Review_Decision", "(\"DecidedAt\" IS NULL AND \"DecidedBy\" IS NULL AND \"Notes\" IS NULL AND \"DecisionSnapshotHash\" IS NULL) OR (\"DecidedAt\" IS NOT NULL AND \"DecidedBy\" IS NOT NULL AND \"Notes\" IS NOT NULL AND \"DecisionSnapshotHash\" IS NOT NULL AND \"DecisionSnapshotHash\" ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("CK_Review_Status", "\"Status\" IN ('Pending','Approved','Rejected','Stale')");
                    table.CheckConstraint("CK_Review_Validation", "(\"LastValidationId\" IS NULL AND \"ValidationSnapshotHash\" IS NULL AND \"ValidationPassed\" IS NULL) OR (\"LastValidationId\" IS NOT NULL AND \"ValidationSnapshotHash\" IS NOT NULL AND \"ValidationSnapshotHash\" ~ '^[0-9a-f]{64}$' AND \"ValidationPassed\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "PreparationValidations",
                schema: "video",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Binding_ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Binding_AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_InventoryVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_SelectionVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_VideoIndex = table.Column<int>(type: "integer", nullable: true),
                    Binding_AudioIndex = table.Column<int>(type: "integer", nullable: true),
                    Binding_SubtitleIndex = table.Column<int>(type: "integer", nullable: true),
                    Binding_SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Binding_SourceSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Binding_SceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_AudioId = table.Column<Guid>(type: "uuid", nullable: true),
                    Binding_FrameId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: true),
                    IssuesJson = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparationValidations", x => x.Id);
                    table.CheckConstraint("CK_Validation_Binding", "\"SnapshotHash\" ~ '^[0-9a-f]{64}$' AND \"MaxAttempts\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Validation_Completion", "(\"CompletedAt\" IS NULL AND \"Passed\" IS NULL AND \"IssuesJson\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"Passed\" IS NOT NULL AND \"IssuesJson\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PreparationValidations_PreparationReviews_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "video",
                        principalTable: "PreparationReviews",
                        principalColumn: "ImportId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreparationAudits_ImportId_Sequence",
                schema: "video",
                table: "PreparationAudits",
                columns: new[] { "ImportId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_PreparationAudits_Sequence",
                schema: "video",
                table: "PreparationAudits",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreparationAudits_ValidationId",
                schema: "video",
                table: "PreparationAudits",
                column: "ValidationId");

            migrationBuilder.CreateIndex(
                name: "IX_PreparationReviews_DecisionValidationId",
                schema: "video",
                table: "PreparationReviews",
                column: "DecisionValidationId");

            migrationBuilder.CreateIndex(
                name: "IX_PreparationReviews_LastValidationId",
                schema: "video",
                table: "PreparationReviews",
                column: "LastValidationId");

            migrationBuilder.CreateIndex(
                name: "IX_PreparationValidations_ImportId_CreatedAt",
                schema: "video",
                table: "PreparationValidations",
                columns: new[] { "ImportId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PreparationValidations_ImportId_IdempotencyKey",
                schema: "video",
                table: "PreparationValidations",
                columns: new[] { "ImportId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreparationValidations_JobId",
                schema: "video",
                table: "PreparationValidations",
                column: "JobId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PreparationAudits_PreparationReviews_ImportId",
                schema: "video",
                table: "PreparationAudits",
                column: "ImportId",
                principalSchema: "video",
                principalTable: "PreparationReviews",
                principalColumn: "ImportId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PreparationAudits_PreparationValidations_ValidationId",
                schema: "video",
                table: "PreparationAudits",
                column: "ValidationId",
                principalSchema: "video",
                principalTable: "PreparationValidations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PreparationReviews_PreparationValidations_DecisionValidatio~",
                schema: "video",
                table: "PreparationReviews",
                column: "DecisionValidationId",
                principalSchema: "video",
                principalTable: "PreparationValidations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PreparationReviews_PreparationValidations_LastValidationId",
                schema: "video",
                table: "PreparationReviews",
                column: "LastValidationId",
                principalSchema: "video",
                principalTable: "PreparationValidations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PreparationValidations_PreparationReviews_ImportId",
                schema: "video",
                table: "PreparationValidations");

            migrationBuilder.DropTable(
                name: "PreparationAudits",
                schema: "video");

            migrationBuilder.DropTable(
                name: "PreparationReviews",
                schema: "video");

            migrationBuilder.DropTable(
                name: "PreparationValidations",
                schema: "video");
        }
    }
}
