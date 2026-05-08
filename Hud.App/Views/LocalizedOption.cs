using Hud.App.Services;

namespace Hud.App.Views
{
    public sealed record LocalizedOption(string Value, string Label)
    {
        public override string ToString() => Label;

        public static LocalizedOption Key(string value, string resourceKey) =>
            new(value, LocalizationManager.Text(resourceKey));

        public static LocalizedOption Raw(string value) =>
            new(value, value);
    }
}
