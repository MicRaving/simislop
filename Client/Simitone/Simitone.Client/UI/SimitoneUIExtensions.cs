using FSO.Client.UI.Controls;
using FSO.SimAntics;
using FSO.SimAntics.Model.TS1Platform;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Simitone.Client.UI
{
    /// <summary>
    /// Simantics helpers that read better as an extension on the lot state than as a
    /// hand-unrolled loop at the (very few) call sites.
    /// </summary>
    internal static class SimitoneSimAnticsVMExtensions
    {
        /// <summary>
        /// Removes every avatar standing on the lot.
        /// </summary>
        /// <remarks>
        /// Used when a lot is being emptied rather than played - moving a family out, or
        /// resetting a template lot before it is saved - so that whoever stands on the lot
        /// next starts from a genuinely empty one. The list is re-read on every pass
        /// because <c>RemoveEntity</c> mutates it.
        /// </remarks>
        /// <param name="State">The TS1 lot state. Unused: the entities are taken from the VM.</param>
        /// <param name="vm">The VM whose lot is to be emptied.</param>
        public static void CleanUpAllPeople(this VMTS1LotState State, VM vm)
        {
            List<VMEntity> avatars = vm.Context.ObjectQueries.Avatars;

            while (avatars.Any())
                vm.RemoveEntity(avatars[0]);
        }
    }

    /// <summary>
    /// Small shared helpers for Simitone's own UI controls.
    /// </summary>
    internal static class SimitoneUIExtensions
    {
        /// <summary>
        /// Shrinks a label's caption until it fits the horizontal space it was given,
        /// accounting for an optional margin.
        /// </summary>
        /// <remarks>
        /// The font is stepped down one point at a time from the label's current size: the
        /// width saved by dropping one point is measured, and that is divided into the
        /// overage to work out how many points have to go. A single measurement up front is
        /// not enough, because the text style's scale is quantised to whichever font
        /// rasterises nearest the requested size, so the width does not shrink linearly with
        /// the requested size.
        ///
        /// <para>
        /// Only ever shrinks. A caption that already fits is left completely alone, so this
        /// is safe to call every frame on a label whose size is not under this method's
        /// control.
        /// </para>
        /// </remarks>
        /// <param name="Label">The label to fit. Its <see cref="UILabel.CaptionStyle"/> is
        /// modified in place, so the caller has to own the style (clone it first, as the rest
        /// of the UI does, if the style is a shared static).</param>
        /// <param name="AvailableSpace">The space available for the caption, in pixels. Only
        /// X is used.</param>
        /// <param name="Margin">Space to keep clear, subtracted from
        /// <paramref name="AvailableSpace"/>. Only X is used.</param>
        public static void FitAvailableSpace(this UILabel Label, Point AvailableSpace, Point Margin = default)
        {
            Vector2 desiredSize = Label.CaptionStyle.MeasureString(Label.Caption);
            int available = AvailableSpace.X - Margin.X;
            if (desiredSize.X <= available) return;

            // the size the fit is measured from, so a label that was already shrunk by an
            // earlier call keeps shrinking from where it is rather than from its original size.
            int baseSize = Label.CaptionStyle.Size;

            Label.CaptionStyle.Size = Math.Max(1, baseSize - 1); // find delta size for this font
            Vector2 scaledSize = Label.CaptionStyle.MeasureString(Label.Caption);

            float delta = desiredSize.X - scaledSize.X;
            float overage = desiredSize.X - available;

            // a font whose width does not respond to a point of size (delta == 0) cannot be
            // fitted this way; leave it at the smallest size we tried rather than dividing by zero.
            int fontSize = (delta > 0) ? Math.Max(1, baseSize - (int)(overage / delta)) : 1;
            Label.CaptionStyle.Size = fontSize; // new scaled size
        }
    }
}
