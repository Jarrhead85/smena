using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Enums;
using NurseShift.Core.Models;

namespace NurseShift.Infrastructure.Persistence;

public sealed class NurseShiftDbContext(DbContextOptions<NurseShiftDbContext> options) : DbContext(options)
{
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<ImportSession> ImportSessions => Set<ImportSession>();
    public DbSet<SourceImage> SourceImages => Set<SourceImage>();
    public DbSet<Reminder> Reminders => Set<Reminder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TimeZoneId).HasMaxLength(100);
            entity.HasIndex(item => item.Status).IsUnique().HasFilter($"[{nameof(Shift.Status)}] = 0");
            entity.Property(item => item.RowVersion).IsConcurrencyToken();
        });
        modelBuilder.Entity<Patient>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FullName).HasMaxLength(200).IsRequired();
            entity.HasIndex(item => new { item.FullName, item.LastUsedAtUtc });
        });
        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PatientName).HasMaxLength(200);
            entity.Property(item => item.Room).HasMaxLength(30);
            entity.Property(item => item.Bed).HasMaxLength(30);
            entity.Property(item => item.Procedure).HasMaxLength(500);
            entity.Property(item => item.Dosage).HasMaxLength(500);
            entity.Property(item => item.Comment).HasMaxLength(2000);
            entity.Property(item => item.ReviewReason).HasMaxLength(500);
            entity.Property(item => item.SourceExcerpt).HasMaxLength(2000);
            entity.HasOne(item => item.Shift).WithMany(item => item.Assignments).HasForeignKey(item => item.ShiftId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Patient>().WithMany().HasForeignKey(item => item.PatientId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<SourceImage>().WithMany(item => item.Assignments).HasForeignKey(item => item.SourceImageId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => new { item.ShiftId, item.DueAtUtc });
            entity.HasIndex(item => new { item.ShiftId, item.ExecutionStatus });
            entity.HasIndex(item => item.SourceImageId);
            entity.Property(item => item.RowVersion).IsConcurrencyToken();
        });
        modelBuilder.Entity<ImportSession>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasOne(item => item.Shift).WithMany().HasForeignKey(item => item.ShiftId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => new { item.ShiftId, item.Status });
            entity.Property(item => item.RowVersion).IsConcurrencyToken();
        });
        modelBuilder.Entity<SourceImage>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasOne(item => item.ImportSession).WithMany(item => item.Pages).HasForeignKey(item => item.ImportSessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => item.ImportSessionId);
            entity.HasIndex(item => item.RemoteDeletePending);
        });
        modelBuilder.Entity<Reminder>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasOne(item => item.Assignment).WithMany(item => item.Reminders).HasForeignKey(item => item.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => new { item.AssignmentId, item.Kind }).IsUnique();
            entity.HasIndex(item => item.AndroidRequestCode).IsUnique();
        });
    }
}
