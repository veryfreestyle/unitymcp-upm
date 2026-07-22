using System;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    public sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
