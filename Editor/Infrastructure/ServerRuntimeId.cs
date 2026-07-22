using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Maps the running Editor platform + process architecture to the .NET-style
    /// runtime identifier used to locate the prebuilt Go server binary under
    /// Server~/&lt;rid&gt;/. Kept pure (static Resolve takes its inputs) so the
    /// mapping is unit-testable without spoofing Application.platform.
    /// </summary>
    public static class ServerRuntimeId
    {
        /// <summary>RID for the current Editor process.</summary>
        public static string Current => Resolve(Application.platform, RuntimeInformation.ProcessArchitecture);

        /// <summary>Maps an explicit platform + architecture to an rid string.</summary>
        public static string Resolve(RuntimePlatform platform, Architecture arch)
        {
            switch (platform)
            {
                case RuntimePlatform.OSXEditor:
                    return arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
                case RuntimePlatform.WindowsEditor:
                    return "win-x64";
                case RuntimePlatform.LinuxEditor:
                    return "linux-x64";
                default:
                    throw new PlatformNotSupportedException(
                        "Unity MCP server has no prebuilt binary for platform " + platform + ".");
            }
        }
    }
}
