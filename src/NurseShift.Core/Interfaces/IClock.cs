namespace NurseShift.Core.Interfaces;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
