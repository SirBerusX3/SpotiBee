using System.Drawing;
using static MusicBeePlugin.Plugin;

namespace SpotiBee.UI
{
    /// <summary>Colours and font pulled from the active MusicBee skin, with safe fallbacks.</summary>
    public sealed class SkinColours
    {
        public Color Background { get; private set; } = SystemColors.Control;
        public Color Foreground { get; private set; } = SystemColors.ControlText;
        public Color Dim { get; private set; } = SystemColors.GrayText;
        public Color Accent { get; private set; } = Color.FromArgb(0xF5, 0xB3, 0x01);
        public Color Track { get; private set; } = SystemColors.ControlDark;
        public Color InputBackground { get; private set; } = SystemColors.Window;
        public Color InputForeground { get; private set; } = SystemColors.WindowText;
        public Font Font { get; private set; } = SystemFonts.MessageBoxFont;

        public static SkinColours FromMusicBee(MusicBeeApiInterface api)
        {
            var skin = new SkinColours();
            try
            {
                skin.Background = Get(api, SkinElement.SkinTrackAndArtistPanel, ElementState.ElementStateDefault, ElementComponent.ComponentBackground);
                skin.Foreground = Get(api, SkinElement.SkinTrackAndArtistPanel, ElementState.ElementStateDefault, ElementComponent.ComponentForeground);
                skin.Dim = Blend(skin.Foreground, skin.Background, 0.55);
                skin.Track = Blend(skin.Foreground, skin.Background, 0.2);
                skin.InputBackground = Get(api, SkinElement.SkinInputControl, ElementState.ElementStateDefault, ElementComponent.ComponentBackground);
                skin.InputForeground = Get(api, SkinElement.SkinInputControl, ElementState.ElementStateDefault, ElementComponent.ComponentForeground);
                var highlight = Get(api, SkinElement.SkinInputPanelLabel, ElementState.ElementStateHighlight, ElementComponent.ComponentForeground);
                if (Distance(highlight, skin.Background) > 80)
                    skin.Accent = highlight;
                var font = api.Setting_GetDefaultFont?.Invoke();
                if (font != null)
                    skin.Font = font;
            }
            catch
            {
                // Older MusicBee or unexpected skin: keep system defaults
            }
            return skin;
        }

        private static Color Get(MusicBeeApiInterface api, SkinElement element, ElementState state, ElementComponent component) =>
            Color.FromArgb(255, Color.FromArgb(api.Setting_GetSkinElementColour(element, state, component)));

        private static Color Blend(Color a, Color b, double amountOfA) =>
            Color.FromArgb(
                (int)(a.R * amountOfA + b.R * (1 - amountOfA)),
                (int)(a.G * amountOfA + b.G * (1 - amountOfA)),
                (int)(a.B * amountOfA + b.B * (1 - amountOfA)));

        private static int Distance(Color a, Color b) =>
            System.Math.Abs(a.R - b.R) + System.Math.Abs(a.G - b.G) + System.Math.Abs(a.B - b.B);
    }
}
