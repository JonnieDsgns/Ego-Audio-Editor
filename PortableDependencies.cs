using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace RegularAudioTool;

internal static class PortableDependencies
{
    // IncludeAllContentForSelfExtract places assemblies and helper tools together.
    // AppContext.BaseDirectory still points at the distributed EXE, not that cache.
    public static string DirectoryPath
    {
        get
        {
#pragma warning disable IL3000 // This profile extracts managed assemblies as well.
            string location = typeof(PortableDependencies).Assembly.Location;
#pragma warning restore IL3000
            return string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : Path.GetDirectoryName(location)!;
        }
    }
}
