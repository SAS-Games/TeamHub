namespace TeamHub.Application.Interfaces;

public interface IClock
{
    DateTime UtcNow { get; }
}
