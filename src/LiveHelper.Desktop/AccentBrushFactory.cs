using Avalonia.Media;

namespace LiveHelper.Desktop;

internal static class AccentBrushFactory
{
    public static IBrush ForDarkSurface(string hex)
    {
        var color = Color.Parse(hex);
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
        if (luminance >= 0.32)
        {
            return new SolidColorBrush(color);
        }

        const double whiteMix = 0.68;
        return new SolidColorBrush(Color.FromRgb(
            BlendTowardWhite(color.R, whiteMix),
            BlendTowardWhite(color.G, whiteMix),
            BlendTowardWhite(color.B, whiteMix)));
    }

    public static IBrush SoftForDarkSurface(string hex)
    {
        var color = Color.Parse(hex);
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
        if (luminance < 0.32)
        {
            const double whiteMix = 0.68;
            color = Color.FromRgb(
                BlendTowardWhite(color.R, whiteMix),
                BlendTowardWhite(color.G, whiteMix),
                BlendTowardWhite(color.B, whiteMix));
        }

        return new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
    }

    private static byte BlendTowardWhite(byte channel, double amount) =>
        (byte)Math.Round(channel * (1 - amount) + 255 * amount);
}
