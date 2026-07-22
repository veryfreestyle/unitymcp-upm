using System;
using System.Globalization;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    public sealed class UlidLikeIdGenerator : IIdGenerator
    {
        public string NewId(string prefix)
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
                .ToLowerInvariant();
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

            return prefix.ToLowerInvariant() + "-" + timestamp + "-" + suffix;
        }
    }
}
