using TeamHub.Application.Interfaces;

namespace TeamHub.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
