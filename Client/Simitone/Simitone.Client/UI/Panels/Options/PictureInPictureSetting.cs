using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// Names the picture-in-picture Options row for the page's reflection, exactly as
    /// <see cref="SunCycleOption"/> does for Dynamic Sun: no instance is ever created, the row's
    /// reads and writes go through <see cref="SimitoneFlags.PictureInPicture"/>.
    /// </summary>
    public sealed class PictureInPictureOption
    {
        public bool PictureInPicture { get; set; }
    }

    /// <summary>The Gameplay page's picture-in-picture switch.</summary>
    public static class PictureInPictureSetting
    {
        /// <summary>The property name the row is matched by.</summary>
        public const string Key = nameof(PictureInPictureOption.PictureInPicture);

        public const string Title = "Picture-in-Picture";

        public const string Description =
            "Opens a small window with a close-up of a sim elsewhere on the lot when something " +
            "notable happens to them, for a few seconds, at most once every twenty seconds.";

        /// <summary>Whether <paramref name="descriptor"/> is this setting's row.</summary>
        public static bool Describes(UIOptionsSubpanelBase.UIOptionDescriprion descriptor)
        {
            return descriptor?.AttachedProperty?.Name == Key
                && descriptor.AttachedProperty.DeclaringType == typeof(PictureInPictureOption);
        }
    }
}
