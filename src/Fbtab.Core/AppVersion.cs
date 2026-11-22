using System.Reflection;

namespace Fbtab;

public static class AppVersion
{
    /// <summary>Version from Directory.Build.props, without the commit suffix the SDK adds.</summary>
    public static string Value { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
