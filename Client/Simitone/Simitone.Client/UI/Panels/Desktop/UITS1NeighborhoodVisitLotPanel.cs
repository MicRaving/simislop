using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.Utils;
using System;

namespace Simitone.Client.UI.Panels.Desktop
{
    /// <summary>
    /// The neighbourhood map in the context of <em>visiting</em> a lot rather than playing in
    /// one.
    /// </summary>
    /// <remarks>
    /// This is the same <see cref="UINeighborhoodSelectionPanel"/> the play flow uses, dressed
    /// up as a destination picker: the lot panel's primary button reads "Visit Lot" instead of
    /// "Enter House", the "More" options (evict, rezone, bulldoze...) are not offered because
    /// none of them mean anything to somebody who is only visiting, and the residential lots
    /// are taken off the map because you cannot walk into a neighbour's house.
    ///
    /// The panel does not visit anything itself: it raises <see cref="OnHouseSelect"/> and
    /// leaves it to the screen that owns it to actually open the lot.
    /// </remarks>
    internal class UITS1NeighborhoodVisitLotPanel : UIContainer
    {
        /// <summary>
        /// The "Go Downtown" caption strings. Three of them, all of which read sensibly as a
        /// heading for "pick somewhere to go".
        /// </summary>
        private const string Title_StringTable = "212";

        /// <summary>Zone 0 is residential, which is exactly the set of lots you cannot visit.</summary>
        private const short ResidentialZone = 0;

        private readonly ushort neighborhoodMode;
        private UIDiagonalStripe captionBg;
        private UILabel caption;

        private UINeighborhoodSelectionPanel neighborhoodView { get; set; }

        /// <summary>Raised with the lot the player picked.</summary>
        public event Action<int> OnHouseSelect;

        public UITS1NeighborhoodVisitLotPanel(ushort neighborhoodMode) : base()
        {
            this.neighborhoodMode = neighborhoodMode;
            create();
        }

        void create()
        {
            //neighborhood panel
            neighborhoodView = new UINeighborhoodSelectionPanel(neighborhoodMode, new UINeighborhoodSelectionPanel.UINeighborhoodSelectionOptions()
            {
                // id 49 of Simitone's own string table, with the English it replaced. Read here
                // rather than as a default on the options object, which would freeze whatever the
                // string tables happened to hold at type initialisation.
                PrimaryButtonText = SimitoneStrings.Get("49", "Visit Lot"),
                MoreButtonEnabled = false,
                LotZoneExclusionFilter = { ResidentialZone } // exclude residential lots since you can't visit those
            });
            neighborhoodView.OnHouseSelect += (id) => OnHouseSelect?.Invoke(id);
            Add(neighborhoodView);

            captionBg = new UIDiagonalStripe(new Point(0, 50), UIDiagonalStripeSide.RIGHT, Color.Black * 0.5f)
            {
                Position = new Vector2(0, 10)
            };
            Add(captionBg);

            caption = new UILabel()
            {
                Caption = getTitle(),
                Position = new Vector2(20, 20),
                // UILabel hands out the shared default style, so it has to be cloned before it
                // is restyled or every other label in the game inherits this size and colour
                CaptionStyle = TextStyle.DefaultLabel.Clone()
            };
            caption.CaptionStyle.Size = 18;
            caption.CaptionStyle.Color = Color.White;
            Add(caption);
        }

        string getTitle()
        {
            int id = Random.Shared.Next(0, 3); // three strings in this table .. assume any new ones may be for another dialog added later
            return GameFacade.Strings.GetString(Title_StringTable, id.ToString()) ?? "***MISSING***"; // should not be null, but just in case ;)
        }

        public void Awake()
        {
            //do initial control placement now ... Awake() will start all tween animations
            GameResized();
        }

        public override void GameResized()
        {
            base.GameResized();

            // position when sizing changes

            var gameScr = GameFacade.Screens.CurrentUIScreen;
            captionBg.BodySize = new Point(gameScr.ScreenWidth, captionBg.BodySize.Y);
            // The title is picked once, in create(): re-rolling it here changed the heading on
            // every window resize.
            var desiredSize = caption.CaptionStyle.MeasureString(caption.Caption);
            caption.Position = new Vector2((gameScr.ScreenWidth / 2) - (desiredSize.X / 2), 20);
        }
    }
}
