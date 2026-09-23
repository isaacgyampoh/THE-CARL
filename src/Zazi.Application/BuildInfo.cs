using System.Reflection;

namespace Zazi.Application;

/// <summary>
/// Which build is running.
/// </summary>
/// <remarks>
/// An incident begins with "what is actually deployed?". Reading it from the running process
/// answers that without trusting a deployment log, a dashboard, or somebody's memory. Render
/// sets RENDER_GIT_COMMIT on every deploy; locally there is none, and "local" is the honest
/// answer rather than a fabricated hash.
/// </remarks>
public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "unknown";

    /// <summary>The first seven characters of the deployed commit, or "local".</summary>
    public static string Commit { get; } =
        Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") is { Length: >= 7 } commit
            ? commit[..7]
            : "local";
}
