using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Voca.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Voca.Services;

/// <summary>Applies a <see cref="PillStyle"/> to the taskbar word (and to the preview in Settings).</summary>
public static class PillPainter
{
    private static readonly PillStyle Defaults = new();

    /// <summary>Height the pill window needs so the chosen font size is not cut off.</summary>
    public static double WindowHeight(PillStyle style) => Math.Max(48, Math.Ceiling(Size(style) * 1.45) + 16);

    public static double Size(PillStyle style) => Math.Clamp(style.FontSize, PillStyle.MinFontSize, PillStyle.MaxFontSize);

    public static double Width(PillStyle style) => Math.Clamp(style.MaxWidth, PillStyle.NarrowestWidth, PillStyle.WidestWidth);

    public static void Apply(PillStyle style, Border pill, TextBlock text, bool showBack)
    {
        text.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? Defaults.FontFamily : style.FontFamily);
        text.FontSize = Size(style);
        text.FontWeight = style.FontWeight switch
        {
            "Normal" => FontWeights.Normal,
            "Bold" => FontWeights.Bold,
            _ => FontWeights.SemiBold
        };
        // The answer stays italic so it reads differently from the word, unless the word itself is italic.
        text.FontStyle = style.Italic != showBack ? FontStyles.Italic : FontStyles.Normal;
        text.Foreground = Brush(showBack ? style.RevealColor : style.TextColor, showBack ? Defaults.RevealColor : Defaults.TextColor, 100);
        text.Effect = style.Shadow ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 0.8 } : null;

        var hasBackground = TryParse(style.Background, out _);
        pill.Background = hasBackground ? Brush(style.Background, "#000000", style.BackgroundOpacity) : System.Windows.Media.Brushes.Transparent;
        pill.CornerRadius = new CornerRadius(hasBackground ? 10 : 0);
        pill.Padding = new Thickness(hasBackground ? 12 : 8, 4, hasBackground ? 12 : 8, 4);
        pill.VerticalAlignment = hasBackground ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        text.MaxWidth = Width(style) - pill.Padding.Left - pill.Padding.Right;
    }

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7) return false;
        try
        {
            color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>"#RRGGBB" with a leading # and upper case, or null when it is not a colour.</summary>
    public static string? Normalize(string? value) =>
        TryParse(value, out var c) ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null;

    private static Brush Brush(string value, string fallback, int opacity)
    {
        if (!TryParse(value, out var color)) TryParse(fallback, out color);
        color.A = (byte)Math.Round(Math.Clamp(opacity, 0, 100) * 2.55);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
