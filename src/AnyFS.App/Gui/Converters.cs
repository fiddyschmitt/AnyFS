using System.Collections.Concurrent;
using AnyFS.Core.Plugins;
using AnyFS.Core.Status;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AnyFS.App.Gui;

/// <summary>Converters used from XAML.</summary>
public static class Converters
{
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#9E9E9E"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34A853"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#F4B400"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#EA4335"));

    /// <summary>The status circle (spec 6.5): grey, green, yellow, red.</summary>
    public static readonly IValueConverter StatusBrush = new FuncValueConverter<StatusColour, IBrush>(colour => colour switch
    {
        StatusColour.Green => Green,
        StatusColour.Yellow => Yellow,
        StatusColour.Red => Red,
        _ => Grey,
    });

    /// <summary>A plugin's icon as an image, or null (then the tile shows its letter badge).</summary>
    public static readonly IValueConverter PluginIcon = new FuncValueConverter<string?, IImage?>(PluginIcons.For);

    public static readonly IValueConverter IsNotNull = new FuncValueConverter<object?, bool>(value => value is not null);
}

/// <summary>
/// Decodes <c>OpenIcon()</c> streams once per plugin. PNG is rendered; SVG needs a renderer that isn't in
/// this build yet (Avalonia.Svg.Skia has no Avalonia 12 release), so SVG icons fall back to the badge.
/// </summary>
public static class PluginIcons
{
    private static readonly ConcurrentDictionary<string, IImage?> Cache = new(StringComparer.Ordinal);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static PluginRegistry? Registry { get; set; }

    /// <param name="key">"source:&lt;type id&gt;" or "mount:&lt;type id&gt;".</param>
    public static IImage? For(string? key)
    {
        if (key is null || Registry is null)
        {
            return null;
        }

        return Cache.GetOrAdd(key, Load);
    }

    private static IImage? Load(string key)
    {
        try
        {
            var separator = key.IndexOf(':');
            if (separator < 0)
            {
                return null;
            }

            var kind = key[..separator];
            var typeId = key[(separator + 1)..];
            using var stream = kind == "mount" ? Registry?.FindMountProvider(typeId)?.OpenIcon() : Registry?.FindSource(typeId)?.OpenIcon();
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (buffer.Length < PngSignature.Length || !buffer.GetBuffer().AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            {
                return null;
            }

            buffer.Position = 0;
            return new Bitmap(buffer);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
