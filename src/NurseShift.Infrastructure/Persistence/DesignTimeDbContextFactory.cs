using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NurseShift.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NurseShiftDbContext>
{
    public NurseShiftDbContext CreateDbContext(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        var options = new DbContextOptionsBuilder<NurseShiftDbContext>()
            .UseSqlite("Data Source=nurseshift-design.db")
            .Options;
        return new NurseShiftDbContext(options);
    }
}
