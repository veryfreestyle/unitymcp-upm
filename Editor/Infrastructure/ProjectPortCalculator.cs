using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// Calculates a stable port number for a Unity project based on its path.
    /// Uses SHA256 hash to ensure the same project always gets the same port.
    /// </summary>
    public static class ProjectPortCalculator
    {
        private const int PortRangeStart = 17000;
        private const int PortRangeSize = 2000;

        /// <summary>
        /// Calculates the port number for the given Unity project path.
        /// </summary>
        /// <param name="projectPath">Absolute path to the Unity project (typically Application.dataPath parent)</param>
        /// <returns>Port number in range 17000-18999</returns>
        public static int GetPort(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                throw new ArgumentException("Project path cannot be null or empty", nameof(projectPath));
            }

            return ComputePortForNormalizedPath(NormalizePath(projectPath));
        }

        /// <summary>
        /// Computes the port for an already-normalized path. This is the stable
        /// cross-implementation contract: the Go server (and any other tooling)
        /// MUST reproduce this exact algorithm to derive the same port —
        /// SHA256 of the UTF-8 bytes, first 4 bytes read as a little-endian
        /// uint32, then 17000 + (value % 2000). The little-endian read is
        /// explicit here so the result never depends on the host's byte order.
        /// </summary>
        /// <param name="normalizedPath">A path already normalized via <see cref="NormalizePath"/>.</param>
        /// <returns>Port number in range 17000-18999</returns>
        public static int ComputePortForNormalizedPath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
            {
                throw new ArgumentException("Normalized path cannot be null or empty", nameof(normalizedPath));
            }

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(normalizedPath);
                byte[] hashBytes = sha256.ComputeHash(pathBytes);

                // Read the first 4 bytes as a little-endian uint32, independent
                // of the running machine's endianness.
                uint hashValue =
                    (uint)hashBytes[0] |
                    ((uint)hashBytes[1] << 8) |
                    ((uint)hashBytes[2] << 16) |
                    ((uint)hashBytes[3] << 24);

                return PortRangeStart + (int)(hashValue % PortRangeSize);
            }
        }

        /// <summary>
        /// Normalizes a path for consistent hashing across platforms.
        /// </summary>
        private static string NormalizePath(string path)
        {
            // Convert to absolute path
            string absolutePath = Path.GetFullPath(path);

            // Replace backslashes with forward slashes
            absolutePath = absolutePath.Replace('\\', '/');

            // Remove trailing slash
            absolutePath = absolutePath.TrimEnd('/');

            return absolutePath;
        }
    }
}
