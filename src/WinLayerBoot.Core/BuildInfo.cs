using System.Linq;
using System.Reflection;

namespace WinLayerBoot.Core;

/// <summary>Which commit this exe was built from (dotnet publish writes the git hash into the version number).</summary>
public static class BuildInfo
{
    public static string Hash
    {
        get
        {
            var version = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var hash = version.Split('+').ElementAtOrDefault(1) ?? "nogit";
            return hash[..System.Math.Min(7, hash.Length)];
        }
    }

    /// <summary>Window title.</summary>
    public static string AppTitle => $"WinLayerBoot {Hash}";
}
