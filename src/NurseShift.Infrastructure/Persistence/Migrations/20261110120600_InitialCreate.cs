using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NurseShift.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Patients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    HistoryExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportSessions_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessingState = table.Column<int>(type: "INTEGER", nullable: false),
                    SafeErrorCode = table.Column<int>(type: "INTEGER", nullable: true),
                    RemoteFileId = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteDeletePending = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetainLocal = table.Column<bool>(type: "INTEGER", nullable: false),
                    UnparsedLinesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceImages_ImportSessions_ImportSessionId",
                        column: x => x.ImportSessionId,
                        principalTable: "ImportSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PatientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PatientName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Room = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Bed = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    DueLocal = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Procedure = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Dosage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ReviewState = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutionStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    NeedsReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReviewReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SourceImageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceExcerpt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    StatusChangedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AdvanceLeadMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Assignments_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Assignments_SourceImages_SourceImageId",
                        column: x => x.SourceImageId,
                        principalTable: "SourceImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AndroidRequestCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduleState = table.Column<int>(type: "INTEGER", nullable: false),
                    SafeErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reminders_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_PatientId",
                table: "Assignments",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_ShiftId_DueAtUtc",
                table: "Assignments",
                columns: new[] { "ShiftId", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_ShiftId_ExecutionStatus",
                table: "Assignments",
                columns: new[] { "ShiftId", "ExecutionStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_SourceImageId",
                table: "Assignments",
                column: "SourceImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportSessions_ShiftId_Status",
                table: "ImportSessions",
                columns: new[] { "ShiftId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Patients_FullName_LastUsedAtUtc",
                table: "Patients",
                columns: new[] { "FullName", "LastUsedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_AndroidRequestCode",
                table: "Reminders",
                column: "AndroidRequestCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_AssignmentId_Kind",
                table: "Reminders",
                columns: new[] { "AssignmentId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_Status",
                table: "Shifts",
                column: "Status",
                unique: true,
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SourceImages_ImportSessionId",
                table: "SourceImages",
                column: "ImportSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceImages_RemoteDeletePending",
                table: "SourceImages",
                column: "RemoteDeletePending");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reminders");

            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "Patients");

            migrationBuilder.DropTable(
                name: "SourceImages");

            migrationBuilder.DropTable(
                name: "ImportSessions");

            migrationBuilder.DropTable(
                name: "Shifts");
        }
    }
}
