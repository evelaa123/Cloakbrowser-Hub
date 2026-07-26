using System.Reflection;

namespace CloakHub.Core.Branding;

/// <summary>
/// The Hub's own icon, as bytes, for the badge renderer to draw on.
/// <para>
/// Loaded from an embedded resource and cached. Read lazily rather than in a
/// static initialiser so that a packaging mistake surfaces as a missing badge
/// with a log line, not as a <c>TypeInitializationException</c> that takes the
/// whole application down before the first window appears.
/// </para>
/// </summary>
public static class AppIcon
{
    public const string ResourceName = "CloakHub.Core.Assets.app-icon.png";

    private static byte[]? _cached;
    private static bool _attempted;
    private static readonly object Gate = new();

    /// <summary>
    /// The base icon PNG, or null when the resource is unavailable.
    /// <para>
    /// Null is a supported answer, not an error: every consumer of a base icon in
    /// this codebase — <see cref="BadgeRenderer.RenderPng"/>,
    /// <see cref="IcnsWriter.Build(byte[], string)"/>,
    /// <see cref="BadgeAssetWriter"/> — accepts null and draws the badge alone.
    /// A numbered badge on a blank field still tells the user which window is
    /// which, so degrading is strictly better than refusing.
    /// </para>
    /// </summary>
    public static byte[]? Bytes
    {
        get
        {
            lock (Gate)
            {
                if (_attempted) return _cached;
                _attempted = true;
                try
                {
                    using var stream = typeof(AppIcon).Assembly
                        .GetManifestResourceStream(ResourceName);
                    if (stream is null) return _cached = null;

                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    var bytes = buffer.ToArray();
                    return _cached = bytes.Length > 0 ? bytes : null;
                }
                catch
                {
                    return _cached = null;
                }
            }
        }
    }

    /// <summary>All resource names in this assembly, for diagnostics.</summary>
    public static IReadOnlyList<string> AvailableResources()
    {
        try
        {
            return typeof(AppIcon).Assembly.GetManifestResourceNames();
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
        {
            return [];
        }
    }
}
