using System;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }
}
