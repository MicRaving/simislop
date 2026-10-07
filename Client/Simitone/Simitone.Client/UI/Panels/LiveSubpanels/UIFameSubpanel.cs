/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.Model;
using FSO.Content;
using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Globalization;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    /// <summary>
    /// The Superstar fame meter: five stars, how full each of them is, and how far the sim is
    /// from the next half star.
    ///
    /// <para>
    /// <b>Why a panel at all.</b> The fame ladder is complete on the engine side -
    /// <see cref="VMTS1FameHelper"/> holds the ten half-star rungs and the promotion/demotion
    /// calls that walk them, and the Studio Town fame objects are script-complete - but until
    /// this panel the only thing in the client that mentioned fame at all was the relationship
    /// panel's *famous friends filter*, which is a list of other sims. A sim could earn every
    /// point the expansion has to give and never find out what they were worth.
    /// </para>
    ///
    /// <para>
    /// <b>What is shown.</b> Star Power, which is the visible rating, as full stars with the
    /// half star drawn as a half-drawn one; the fame-point total behind it; and the progress to
    /// the next rung. Star Power and the watermark are read straight out of the sim's person
    /// data, and the arithmetic is
    /// <see cref="VMTS1FameHelper"/>'s - this panel decides nothing about what a sim is worth.
    /// </para>
    /// </summary>
    public class UIFameSubpanel : UISubpanel
    {
        private const int StarSize = 44;
        private const int StarsX = 18;
        private const int StarsY = 14;

        private UIFameStarRow Stars;
        private UILabel RatingLabel;
        private UILabel RungLabel;
        private UILabel PointsLabel;
        private UIValueBar ProgressBar;
        private UILabel ProgressLabel;
        private UILabel BestLabel;

        public UIFameSubpanel(TS1GameScreen game) : base(game)
        {
            Stars = new UIFameStarRow(StarSize) { Position = new Vector2(StarsX, StarsY) };
            Add(Stars);

            RatingLabel = MakeLabel(StarsX, StarsY + StarSize + 4, 300, 26, 19);
            RungLabel = MakeLabel(StarsX, StarsY + StarSize + 30, 300, 24, 15);
            Add(RatingLabel);
            Add(RungLabel);

            var columnX = StarsX + UIFameStarRow.RowWidth(StarSize) + 40;

            PointsLabel = MakeLabel(columnX, StarsY, 260, 24, 15);
            Add(PointsLabel);
            BestLabel = MakeLabel(columnX, StarsY + 26, 260, 24, 15);
            Add(BestLabel);

            ProgressLabel = MakeLabel(columnX, StarsY + 56, 260, 24, 15);
            Add(ProgressLabel);
            ProgressBar = new UIValueBar(Content.Get().CustomUI.Get("motive_bg.png").Get(GameFacade.GraphicsDevice))
            {
                Width = 200,
                Position = new Vector2(columnX, StarsY + 80)
            };
            Add(ProgressBar);
        }

        private UILabel MakeLabel(int x, int y, int width, int height, int size)
        {
            // UILabel hands out the shared default style, so it has to be cloned before it is
            // restyled or every other label in the game inherits this size.
            var label = new UILabel()
            {
                Position = new Vector2(x, y),
                Size = new Vector2(width, height),
                CaptionStyle = TextStyle.DefaultLabel.Clone(),
            };
            label.CaptionStyle.Size = size;
            label.CaptionStyle.Color = UIStyle.Current.Text;
            label.Alignment = TextAlignment.Middle | TextAlignment.Left;
            return label;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            var sel = Game.SelectedAvatar;
            if (sel == null) return;

            var points = sel.GetPersonData(VMPersonDataVariable.TS1FameScore);
            var starPower = sel.GetPersonData(VMPersonDataVariable.TS1FameStarPower);
            var watermark = sel.GetPersonData(VMPersonDataVariable.TS1FameStarHighWatermark);

            Stars.FilledStars = FameMeter.FilledStars(starPower);
            Stars.HalfStar = FameMeter.HasHalfStar(starPower);

            RatingLabel.Caption = FameMeter.RatingCaption(starPower);
            RungLabel.Caption = FameMeter.RungCaption(starPower);
            PointsLabel.Caption = FameMeter.PointsCaption(points);
            BestLabel.Caption = FameMeter.BestCaption(watermark);
            ProgressLabel.Caption = FameMeter.ProgressCaption(points, starPower);
            ProgressBar.Value = FameMeter.Progress(points, starPower);
        }
    }

    /// <summary>
    /// Five stars drawn as stars, with the current half star drawn half.
    /// </summary>
    /// <remarks>
    /// A row of five rectangles would be the easy way to draw a star meter and would read as a
    /// bar, not as a rating. The glyph is generated in code from the same ten-point outline a
    /// five-pointed star is, and tinted - the panel's active colour for a star that is earned,
    /// its inactive colour for one that is not - so the whole row is one texture and the
    /// colours come from <see cref="UIStyle"/> like the rest of the strip's do.
    /// </remarks>
    public class UIFameStarRow : UIElement
    {
        /// <summary>
        /// Distance between the left edges of two stars, and the width the row occupies.
        /// </summary>
        /// <remarks>
        /// Stated here rather than computed by the caller, because the panel has to place its
        /// right-hand column clear of the row and a row drawn wider than the space left for it
        /// puts the fame points on top of a star.
        /// </remarks>
        public const int StarGap = 12;

        /// <summary>Width the whole row of <see cref="FameMeter.StarCount"/> stars occupies.</summary>
        public static int RowWidth(int starSize)
        {
            return (FameMeter.StarCount * (starSize + StarGap)) - StarGap;
        }

        private readonly int size;
        private Texture2D star;

        public int FilledStars { get; set; }

        /// <summary>True when the sim is on a half star, which is drawn as a half-drawn one.</summary>
        public bool HalfStar { get; set; }

        public UIFameStarRow(int size)
        {
            this.size = size;
        }

        public override void Draw(UISpriteBatch batch)
        {
            if (!Visible) return;
            if (star == null) star = FameStarTexture.Get(GameFacade.GraphicsDevice);

            var pitch = size + StarGap;
            for (int i = 0; i < FameMeter.StarCount; i++)
            {
                var earned = i < FilledStars;
                var color = earned ? UIStyle.Current.BtnActive : UIStyle.Current.SkillInactive;

                if (earned && HalfStar && i == FilledStars - 1)
                {
                    DrawLocalTexture(batch, star, null, new Vector2(i * pitch, 0),
                        new Vector2(size / (float)star.Width, size / (float)star.Height), color);
                    // the second half of the same star, over a dimmer first half
                    DrawLocalTexture(batch, star,
                        new Rectangle(star.Width / 2, 0, star.Width / 2, star.Height),
                        new Vector2(i * pitch, 0),
                        new Vector2(size / (float)star.Width, size / (float)star.Height),
                        UIStyle.Current.SkillInactive);
                }
                else
                {
                    DrawLocalTexture(batch, star, null, new Vector2(i * pitch, 0),
                        new Vector2(size / (float)star.Width, size / (float)star.Height), color);
                }
            }
        }
    }

    /// <summary>
    /// A five-pointed star as a texture, built once.
    /// </summary>
    /// <remarks>
    /// Generated rather than shipped, for the same reason the rest of the live-mode strip is
    /// drawn from Simitone's own art: the retail panel this stands in for had its own star
    /// bitmap, and shipping that would mean shipping Maxis art. Filled by point-in-polygon with
    /// a 3x3 supersample, because a hard-edged star at 44px next to 15pt text reads as a
    /// staircase.
    /// </remarks>
    public static class FameStarTexture
    {
        private const int Samples = 3;

        private static Texture2D star;

        public static Texture2D Get(GraphicsDevice gd)
        {
            if (star != null) return star;

            int size = 48;
            var data = new Color[size * size];

            // ten points: outer radius, inner radius, straight up, clockwise
            var points = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                var radius = (i % 2 == 0) ? 0.46f : 0.20f;
                var angle = (Math.PI / 2f) + (i * Math.PI / 5f);
                points[i] = new Vector2(
                    0.5f + (radius * (float)Math.Cos(angle)),
                    0.5f - (radius * (float)Math.Sin(angle)));
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            var px = (x + ((sx + 0.5f) / Samples)) / (float)size;
                            var py = (y + ((sy + 0.5f) / Samples)) / (float)size;
                            if (Inside(points, px, py)) covered++;
                        }
                    }
                    var alpha = (byte)Math.Round(255f * covered / (Samples * Samples));
                    // The byte overloads are named explicitly: a byte fourth argument makes
                    // Color(x, x, x, alpha) ambiguous between the int and the byte constructor.
                    data[(y * size) + x] = new Color((byte)255, (byte)255, (byte)255, alpha);
                }
            }

            star = new Texture2D(gd, size, size);
            star.SetData(data);
            return star;
        }

        /// <summary>Even-odd point-in-polygon against a closed outline.</summary>
        private static bool Inside(Vector2[] points, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            {
                if (((points[i].Y > y) != (points[j].Y > y)) &&
                    (x < ((points[j].X - points[i].X) * (y - points[i].Y) / (points[j].Y - points[i].Y)) + points[i].X))
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }

    /// <summary>
    /// The fame ladder as the panel draws it: stars, the rung a sim is on, and the climb to the
    /// next one.
    ///
    /// <para>
    /// Star Power is counted in half stars, so a sim on 5 is two and a half stars and a sim on
    /// 10 is five. Everything about the ladder itself belongs to
    /// <see cref="VMTS1FameHelper"/>; what is here is the presentation arithmetic over it, split
    /// out so the rounding and the progress fraction can be pinned without a graphics device.
    /// </para>
    /// </summary>
    public static class FameMeter
    {
        /// <summary>How many full stars the retail rating is shown as.</summary>
        public const int StarCount = 5;

        /// <summary>Ids 500 to 516 of Simitone's own table.</summary>
        public const string NobodyId = "500",
            StarsId = "501",
            PointsId = "502",
            NextRungId = "503",
            BestId = "504",
            ToGoId = "505";

        /// <summary>
        /// First of the eleven consecutive ids the eleven half-star rung names use, so
        /// <c>RungId(7)</c> is 513 and each rung's id is derivable from its Star Power.
        /// </summary>
        private const int RungIdBase = 506;

        /// <summary>
        /// The English name of each half-star rung, in Star Power order.
        /// </summary>
        /// <remarks>
        /// Taken from the ladder table <see cref="VMTS1FameHelper"/> documents - the community
        /// transcription the engine's own promotion/demotion calls are driven by - rather than
        /// invented here. They are Simitone's own strings because The Sims 1 has no string
        /// table for them: the retail panel showed stars and a point count, and the rung names
        /// are this tree's addition.
        /// </remarks>
        public static readonly string[] RungNameFallback =
        {
            "Nobody",             // 0 stars   - nobody
            "Stepping Stone",     // 0.5 stars
            "Insider",            // 1 star
            "Name Dropper",       // 1.5 stars
            "Studio Fly",         // 2 stars
            "Sell Out",           // 2.5 stars
            "Trendsetter",        // 3 stars
            "Player",             // 3.5 stars
            "Talk of the Town",   // 4 stars
            "Celebrity",          // 4.5 stars
            "Superstar",          // 5 stars
        };

        /// <summary>
        /// How many whole stars a Star Power reading is worth, 0..5.
        /// </summary>
        public static int FilledStars(short starPower)
        {
            return VMTS1FameHelper.ClampStarPower(starPower) / 2;
        }

        /// <summary>
        /// Whether a Star Power reading sits on a half star.
        /// </summary>
        public static bool HasHalfStar(short starPower)
        {
            return VMTS1FameHelper.ClampStarPower(starPower) % 2 == 1;
        }

        /// <summary>
        /// The rating as words: "Nobody" at zero, otherwise "2.5 stars".
        /// </summary>
        public static string RatingCaption(short starPower)
        {
            var clamped = VMTS1FameHelper.ClampStarPower(starPower);
            if (clamped == VMTS1FameHelper.NoStars) return SimitoneStrings.Get(NobodyId, "Nobody");
            return SimitoneStrings.Format(StarsId, "%s stars", HalfStarsText(clamped));
        }

        /// <summary>
        /// Half stars written out as a number, so a half star is not truncated away: 5 is "2.5",
        /// 6 is "3".
        /// </summary>
        public static string HalfStarsText(short starPower)
        {
            var clamped = VMTS1FameHelper.ClampStarPower(starPower);
            var whole = clamped / 2;
            if (clamped % 2 == 0) return whole.ToString(CultureInfo.InvariantCulture);
            return whole.ToString(CultureInfo.InvariantCulture) + ".5";
        }

        /// <summary>
        /// The name of the rung a sim is standing on.
        /// </summary>
        public static string RungCaption(short starPower)
        {
            var clamped = VMTS1FameHelper.ClampStarPower(starPower);
            var index = Math.Max(0, Math.Min(clamped, RungNameFallback.Length - 1));
            var id = (RungIdBase + clamped).ToString(CultureInfo.InvariantCulture);
            return SimitoneStrings.Get(id, RungNameFallback[index]);
        }

        /// <summary>The fame-point total, which is the number the lot's scripts actually move.</summary>
        public static string PointsCaption(short famePoints)
        {
            return SimitoneStrings.Format(PointsId, "Fame points: %s",
                famePoints.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The career high watermark, which never falls back down.</summary>
        public static string BestCaption(short highWatermark)
        {
            return SimitoneStrings.Format(BestId, "Career best: %s", HalfStarsText(highWatermark));
        }

        /// <summary>
        /// The line under the progress bar: what the next rung is, or that there is not one.
        /// </summary>
        public static string ProgressCaption(short famePoints, short starPower)
        {
            var clamped = VMTS1FameHelper.ClampStarPower(starPower);
            if (clamped >= VMTS1FameHelper.MaxStarPower)
            {
                return SimitoneStrings.Format(ToGoId, "Top of the ladder - %s points to stay there",
                    famePoints.ToString(CultureInfo.InvariantCulture));
            }

            var wanted = VMTS1FameHelper.FamePointsFor((short)(clamped + 1));
            var togo = Math.Max(0, wanted - famePoints);
            return SimitoneStrings.Format(NextRungId, "%s to go for %s",
                togo.ToString(CultureInfo.InvariantCulture), RungCaption((short)(clamped + 1)));
        }

        /// <summary>
        /// How far the sim is between their current rung and the next, 0..1. A sim on the top
        /// rung reads as 1, since there is nothing left to climb towards.
        /// </summary>
        public static float Progress(short famePoints, short starPower)
        {
            var clamped = VMTS1FameHelper.ClampStarPower(starPower);
            if (clamped >= VMTS1FameHelper.MaxStarPower) return 1f;

            var floor = VMTS1FameHelper.FamePointsFor(clamped);
            var ceiling = VMTS1FameHelper.FamePointsFor((short)(clamped + 1));
            var span = ceiling - floor;
            if (span <= 0) return 1f;

            var value = (famePoints - floor) / (float)span;
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;
            return value;
        }
    }
}
