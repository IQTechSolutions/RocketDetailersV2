using MudBlazor;

namespace RD.Web.Theme;

/// <summary>
/// Rocket Detailer branding (Req 14): a confident, financial-instrument
/// palette — deep navy chrome, restrained blues, honest status colors.
/// Dark-capable; the layout follows the OS preference and offers a toggle.
/// </summary>
public static class RdTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1E4E8C",
            Secondary = "#0E7490",
            Tertiary = "#5B6B7F",
            AppbarBackground = "#0D1B2A",
            AppbarText = "#F5F7FA",
            Background = "#F4F6F9",
            Surface = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#1A2433",
            Success = "#1B7F4B",
            Warning = "#B45309",
            Error = "#B42318",
            Info = "#1D6FA5",
            TextPrimary = "#1A2433",
            TextSecondary = "#5B6B7F",
            LinesDefault = "#E2E8F0",
            TableLines = "#E2E8F0",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#6EA8FE",
            Secondary = "#2DD4BF",
            Tertiary = "#93A3B8",
            AppbarBackground = "#0B1220",
            AppbarText = "#E6EBF2",
            Background = "#0B1220",
            Surface = "#121C2B",
            DrawerBackground = "#0F1826",
            DrawerText = "#E6EBF2",
            Success = "#4ADE80",
            Warning = "#F59E0B",
            Error = "#F87171",
            Info = "#60A5FA",
            TextPrimary = "#E6EBF2",
            TextSecondary = "#93A3B8",
            LinesDefault = "#1F2C40",
            TableLines = "#1F2C40",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
