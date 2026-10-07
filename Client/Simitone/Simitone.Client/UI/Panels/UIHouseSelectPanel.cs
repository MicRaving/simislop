using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Utils;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Drivers;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels
{
    public class UIHouseSelectPanel : UIContainer
    {
        // -------------------------------------------------------------------------------------
        // String ids.
        //
        // Everything this panel shows that Simitone invented used to be a literal, which is what
        // the ids in Simitone's own table (_f900_simitone.cst) are for. Each lookup carries the
        // English it replaced, so an install with no table - or one whose table has not been
        // translated yet - shows exactly what it always showed.
        //
        // 40-49 and 60-69 already exist in the table. 70-79 is the next free block in the same
        // ten-per-file grouping and is reserved here for the standalone bulldoze dialogs, which
        // are newer than the table: until the rows are added, Get falls through to the English
        // written beside each id, which is why the ids and the fallbacks must be kept together.
        // -------------------------------------------------------------------------------------

        /// <summary>"Move In" - the primary button while a family is waiting to move in.</summary>
        private const string MoveInCaptionId = "40";

        /// <summary>"More" - the button that opens the options list.</summary>
        private const string MoreCaptionId = "41";

        /// <summary>"Bulldoze" - the first option, on a lot with nobody in it.</summary>
        private const string BulldozeCaptionId = "42";

        /// <summary>"Evict" - the first option, on a lot with a family in it.</summary>
        private const string EvictCaptionId = "43";

        /// <summary>"Edit Family" - opens the family in the Create-a-Sim editor.</summary>
        private const string EditFamilyCaptionId = "44";

        /// <summary>"Rezone" - moves the lot between residential and community.</summary>
        private const string RezoneCaptionId = "45";

        /// <summary>"Export" - still a dead button; see the note in <see cref="Build"/>.</summary>
        private const string ExportCaptionId = "46";

        /// <summary>"Back" - closes the options list.</summary>
        private const string BackCaptionId = "47";

        /// <summary>Dialog title for everything the rezone option says.</summary>
        private const string RezoneTitleId = "60";

        /// <summary>"There is still a family living at %s..."</summary>
        private const string RezoneOccupiedId = "61";

        /// <summary>"...so the lot cannot be rezoned. Use Evict on this panel first."</summary>
        private const string RezoneOccupiedDetailId = "62";

        /// <summary>"Rezone %s as a %s lot?"</summary>
        private const string RezoneConfirmId = "63";

        /// <summary>"Lot %s has no zoning entry, so there is nothing to rezone."</summary>
        private const string RezoneNoEntryId = "64";

        /// <summary>"LotZoning.iff only carries the lots the game shipped with a zone for."</summary>
        private const string RezoneNoEntryDetailId = "65";

        /// <summary>"Lot %s is not decided by its own zoning line..."</summary>
        private const string RezoneShadowedId = "66";

        /// <summary>"...which is the one the game reads."</summary>
        private const string RezoneShadowedDetailId = "67";

        /// <summary>"The neighbourhood's zoning file could not be read..."</summary>
        private const string RezoneUnreadableId = "68";

        /// <summary>"The zoning file could not be written, so lot %s is unchanged."</summary>
        private const string RezoneUnwrittenId = "69";

        /// <summary>Dialog title for the standalone bulldoze.</summary>
        private const string BulldozeTitleId = "70";

        /// <summary>"There is still a family living at %s, so the lot cannot be bulldozed."</summary>
        private const string BulldozeOccupiedId = "71";

        /// <summary>"A bulldoze takes the contents of an empty lot. Use Evict on this panel first."</summary>
        private const string BulldozeOccupiedDetailId = "72";

        /// <summary>"Bulldoze lot %s? Everything a family put in it is removed."</summary>
        private const string BulldozeConfirmId = "73";

        public UIDiagonalStripe Diag;
        public UIDiagonalStripe TitleStripe;

        public UILabel StreetTitle;
        public UILabel LotTitle;
        public UILabel LotDescription;
        public UILabel SecondaryText;
        public UIHouseFamilyList FamilyDisplay;

        public UIBigButton EnterLot;
        public UIBigButton More;

        public event Action<int> OnSelected;
        public int HouseID;

        public List<UIBigButton> OptionButtons = new List<UIBigButton>();

        /// <summary>
        /// The options this panel was built with, kept so <see cref="Refresh"/> can rebuild it the
        /// same way. <c>UINeighborhoodSelectionPanel</c> owns the primary caption, whether the More
        /// button exists at all, and which zones this view refuses to offer, so a rebuild that lost
        /// them would quietly become a different panel.
        /// </summary>
        private readonly UINeighborhoodSelectionPanel.UINeighborhoodSelectionOptions _options;

        private float _MoreTween;
        public float MoreTween
        {
            get
            {
                return _MoreTween;
            }
            set
            {
                var nonOptionButtons = new UIElement[]
                {
                    LotDescription,
                    SecondaryText,
                    EnterLot,
                    More,
                };

                foreach (var btn in nonOptionButtons)
                {
                    btn.Opacity = 1 - value;
                    btn.Visible = btn.Opacity > 0;
                }

                foreach (var btn in OptionButtons)
                {
                    btn.Opacity = value;
                    btn.Visible = btn.Opacity > 0;
                }
                var screen = UIScreen.Current;

                var scale = Math.Max(2 / 3f, Math.Min(1, screen.ScreenWidth / 1704f));
                var space = (96 * scale) - 56;

                if (FamilyDisplay != null)
                    FamilyDisplay.X = (48 + space / 2) - value * screen.ScreenWidth / 2;

                _MoreTween = value;
            }
        }

        /// <summary>
        /// Builds a lot panel for one lot.
        /// </summary>
        /// <remarks>
        /// The contents live in <see cref="Build"/> rather than here so that
        /// <see cref="Refresh"/> can rebuild the panel in place after it has changed the lot
        /// itself - a rezone changes the caption, the price and whether the lot may be entered,
        /// and the only way to show all three correctly is to ask the same questions again.
        /// </remarks>
        /// <param name="houseID">The lot this panel is about.</param>
        /// <param name="Options">How the surrounding view wants the panel to behave.</param>
        public UIHouseSelectPanel(int houseID, UINeighborhoodSelectionPanel.UINeighborhoodSelectionOptions Options)
        {
            HouseID = houseID;
            _options = Options ?? UINeighborhoodSelectionPanel.UINeighborhoodSelectionOptions.Default;

            Build();

            var screen = GameFacade.Screens.CurrentUIScreen;
            X = screen.ScreenWidth / -2;
            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "X", 0f } }, TweenQuad.EaseOut);
            MoreTween = MoreTween;
        }

        /// <summary>
        /// (Re)builds everything the panel shows: the stripes, the captions, the family list, the
        /// primary button and the five option buttons. Must leave the panel in the same shape
        /// whichever entry point called it.
        /// </summary>
        private void Build()
        {
            var screen = GameFacade.Screens.CurrentUIScreen;
            var houseID = HouseID;
            var extra = Math.Max(0, (screen.ScreenHeight - 640) / 128) * 64;

            //the family list is the only member Build() adds conditionally, and a rebuild has to
            //be able to take it away again.
            FamilyDisplay = null;

            var options = _options;

            // The lot panel owns the left half of the screen; everything inside it is measured
            // against that, not against the whole window, so a narrow window shrinks the panel
            // rather than spilling off the side.
            Point controlBoundaries = new Point(screen.ScreenWidth / 2, screen.ScreenHeight);
            Point margin = new Point(30, 30);

            Diag = new UIDiagonalStripe(new Point(controlBoundaries.X, screen.ScreenHeight + 16), UIDiagonalStripeSide.RIGHT, UIStyle.Current.Bg);
            Diag.Y = -16;
            Diag.ListenForMouse(Diag.GetBounds(), (e, s) => { });
            Add(Diag);

            TitleStripe = new UIDiagonalStripe(new Point(controlBoundaries.X, 92 + 8 + 32), UIDiagonalStripeSide.RIGHT, UIStyle.Current.Bg);
            TitleStripe.StartOff = 8 + 32;
            TitleStripe.Y = -16 + extra;
            Add(TitleStripe);

            var neigh = Content.Get().Neighborhood;

            var house = neigh.GetHouse(houseID);

            var street = neigh.StreetNames;
            var assignment = street.Get<STR>(2001).GetString(houseID - 1);

            int streetName;
            if (int.TryParse(assignment, out streetName))
            {
                StreetTitle = new UILabel();
                StreetTitle.Position = new Vector2(30, 94 + extra - 64);
                InitLabel(StreetTitle);
                StreetTitle.CaptionStyle.Color = UIStyle.Current.BtnActive;
                StreetTitle.Caption = street.Get<STR>(2000).GetString(streetName - 1).Replace("%s", houseID.ToString());
            }

            var nameDesc = neigh.GetHouseNameDesc(houseID);
            var name = nameDesc.Item1;
            if (name == "") name = StreetTitle.Caption;

            LotTitle = new UILabel();
            LotTitle.Position = new Vector2(margin.X, 122 + extra - 64);
            InitLabel(LotTitle);
            LotTitle.CaptionStyle.Size = 37; // the size we would like
            LotTitle.Caption = name;
            // lot names are as long as the player wants them to be, so shrink rather than run
            // off under the right hand side of the panel
            LotTitle.FitAvailableSpace(controlBoundaries, margin);

            var family = neigh.GetFamilyForHouse((short)houseID);

            LotDescription = new UILabel();
            LotDescription.Position = new Vector2(margin.X, 206 + extra);
            InitLabel(LotDescription);
            //LotDescription.CaptionStyle.Size = 15;
            LotDescription.Size = new Vector2(controlBoundaries.X - (margin.X * 2), screen.ScreenHeight - 415);
            LotDescription.Wrapped = true;
            LotDescription.Alignment = TextAlignment.Top | TextAlignment.Left;

            SecondaryText = new UILabel();
            SecondaryText.Position = new Vector2(margin.X, screen.ScreenHeight - (165 + extra));
            InitLabel(SecondaryText);
            SecondaryText.Size = new Vector2(controlBoundaries.X - (margin.X * 2), margin.Y);
            SecondaryText.Wrapped = true;
            SecondaryText.Alignment = TextAlignment.Bottom | TextAlignment.Right;
            SecondaryText.CaptionStyle.Color = UIStyle.Current.SecondaryText;

            var moveInID = (UIScreen.Current as Screens.TS1GameScreen).MoveInFamily;
            var moveIn = (moveInID == null) ? null : Content.Get().Neighborhood.GetFamily((ushort)moveInID.Value);
            var buttonValid = true;

            if (family != null)
            {
                var famUI = new UIHouseFamilyList(family);

                var scale = Math.Max(2 / 3f, Math.Min(1, screen.ScreenWidth / 1704f));
                famUI.ScaleX = famUI.ScaleY = scale;
                var space = (96 * scale) - 56;
                LotDescription.Y += space;
                famUI.Position = new Vector2(48 + space / 2, 152 + extra + space / 2);
                Add(famUI);
                FamilyDisplay = famUI;

                LotDescription.Caption = GameFacade.Strings.GetString("134", "0", new string[] {
                    Content.Get().Neighborhood.MainResource.Get<FAMs>(family.ChunkID)?.GetString(0) ?? "?",
                    "§" + (family.ValueInArch + family.Budget).ToString("##,#0"), //should include lot value eventually
                    family.FamilyFriends.ToString()
                    });

                LotDescription.CaptionStyle.Color = UIStyle.Current.SecondaryText;

                if (moveIn != null)
                {
                    SecondaryText.Caption = GameFacade.Strings.GetString("132", "15"); //house occupied
                    buttonValid = false;
                }
            } else
            {
                LotDescription.Y -= 64;
                LotDescription.Size = new Vector2(LotDescription.Size.X, LotDescription.Size.Y + 65);
                //LotDescription.Caption = new string(Enumerable.Range(1, 255).Select(x => 'a').ToArray());
                LotDescription.Caption = nameDesc.Item2;

                //set up the secondary text. CurrentZone is the same question the rezone button asks,
                //so the caption and the button can never disagree about what the lot is.
                var result = CurrentZone();

                if (result > 0)
                {
                    //zone
                    string str;
                    if (moveIn != null)
                    {
                        str = GameFacade.Strings.GetString("134", "18").Substring(8); //is community, can't move in
                        buttonValid = false;
                    }
                    else
                    {
                        str = GameFacade.Strings.GetString("134", "17").Substring(8); //is community
                    }
                    SecondaryText.Caption = str;
                } else
                {
                    //show price
                    var price = house.Get<SIMI>(1)?.PurchaseValue ?? 0;
                    string str;
                    if (houseID >= 90 && houseID <= 92)
                    {
                        //also requires magicoins
                        //magicoins are held by the family that would be moving in, not by the
                        //lot. this whole block only runs when the lot is empty, so the lot's own
                        //family is null here and asking about it always reported 0 - which made
                        //every magic lot read as "no magicoins" and disabled the button.
                        var magicoins = neigh.GetMagicoinsForFamily(moveIn ?? family);
                        var requiredMC = int.Parse(GameFacade.Strings.GetString("134", (32 + Math.Abs(91 - houseID)).ToString()));

                        if (moveIn != null)
                        {
                            if (magicoins >= requiredMC)
                            {
                                if (moveIn.Budget >= price)
                                {
                                    str = GameFacade.Strings.GetString("134", "31", new string[] { "", "",
                                    "§" + price.ToString("##,#0"),
                                    requiredMC.ToString()
                                    });
                                }
                                else
                                {
                                    //missing simoleons
                                    str = GameFacade.Strings.GetString("134", "38", new string[] { "", "",
                                        "§" + price.ToString("##,#0"),
                                        requiredMC.ToString(),
                                        moveIn.ChunkParent.Get<FAMs>(moveIn.ChunkID)?.GetString(0) ?? "",
                                        "§" +moveIn.Budget.ToString("##,#0")
                                    }).Substring(4);
                                    buttonValid = false;
                                }
                            }
                            else
                            {
                                if (moveIn.Budget >= price)
                                {
                                    //missing magicoins
                                    str = GameFacade.Strings.GetString("134", "36", new string[] { "", "",
                                        "§" + price.ToString("##,#0"),
                                        requiredMC.ToString(),
                                        moveIn.ChunkParent.Get<FAMs>(moveIn.ChunkID)?.GetString(0) ?? "",
                                        magicoins.ToString()
                                    }).Substring(4);
                                }
                                else
                                {
                                    //missing both
                                    str = GameFacade.Strings.GetString("134", "37", new string[] { "", "",
                                        "§" + price.ToString("##,#0"),
                                        requiredMC.ToString(),
                                        moveIn.ChunkParent.Get<FAMs>(moveIn.ChunkID)?.GetString(0) ?? "",
                                        magicoins.ToString()
                                    }).Substring(4);
                                    buttonValid = false;
                                }
                                buttonValid = false;
                            }
                        }
                        else
                        {
                            //suggest move in
                            str = GameFacade.Strings.GetString("134", "29", new string[] { "", "",
                                "§" + price.ToString("##,#0"),
                                requiredMC.ToString()
                            }).Substring(4);
                        }
                    }
                    else
                    {
                        if (moveIn != null)
                        {
                            if (moveIn.Budget >= price)
                            {
                                str = GameFacade.Strings.GetString("134", "5", new string[] { "", "",
                                    "§" + price.ToString("##,#0"),
                                    "§" +(moveIn.Budget - price).ToString("##,#0")
                                }).Substring(4);
                            }
                            else
                            {
                                str = GameFacade.Strings.GetString("134", "4", new string[] { "", "",
                                    "§" + price.ToString("##,#0"),
                                    moveIn.ChunkParent.Get<FAMs>(moveIn.ChunkID)?.GetString(0) ?? "",
                                    "§" +moveIn.Budget.ToString("##,#0")
                                }).Substring(4);
                                buttonValid = false;
                            }
                        }
                        else
                        {
                            //suggest move in
                            str = GameFacade.Strings.GetString("134", "1", new string[] { "", "", "§" + price.ToString("##,#0") }).Substring(4);
                        }
                    }
                        
                    SecondaryText.Caption = str;
                    SecondaryText.CaptionStyle.Size = 15;
                }
            }

            // The primary button takes whatever is left of the panel once the margins and the
            // options button are accounted for, so it does not need a width per screen size.
            bool showMoreOptionsButton = options.MoreButtonEnabled && moveIn == null;
            Point MoreButtonSize = new Point(208, 0);
            Point buttonMargin = new Point(10, 0);
            int dockpanelSpace = controlBoundaries.X - ((margin.X * 2) + (showMoreOptionsButton ? MoreButtonSize.X : 0) + (buttonMargin.X * 2));

            EnterLot = new UIBigButton(false)
            {
                // The options' own caption when it has one (the visit flow's "Visit Lot"), and
                // "Enter House" when it has not - see UINeighborhoodSelectionOptions.
                Caption = (moveIn == null)
                    ? (options.PrimaryButtonText ?? SimitoneStrings.Get("48", "Enter House"))
                    : SimitoneStrings.Get(MoveInCaptionId, "Move In"),
                Width = dockpanelSpace,
                Disabled = !buttonValid,
                Position = new Vector2(margin.X, screen.ScreenHeight - (extra + 125)),
            };
            EnterLot.OnButtonClick += (b) => { OnSelected?.Invoke(houseID); Kill(); };
            Add(EnterLot);

            More = new UIBigButton(true);
            More.Caption = SimitoneStrings.Get(MoreCaptionId, "More");
            More.Width = MoreButtonSize.X;
            More.Position = new Vector2(screen.ScreenWidth / 2 - 238, screen.ScreenHeight - (extra + 125));
            More.OnButtonClick += (btn) => { ShowMore(true); };
            if (showMoreOptionsButton) Add(More);

            var optionFunctions = new ButtonClickDelegate[]
            {
                // One physical action, two conversations. Evict is the bulldoze plus a family
                // being refunded and moved out; on an empty lot there is nobody to move, so the
                // same bulldoze is offered on its own rather than as a null delegate that greyed
                // the button out. The button was permanently dead before this - an empty lot had
                // no way to clear its contents at all.
                (family==null)
                    ? (ButtonClickDelegate)((btn) => Bulldoze())
                    : (ButtonClickDelegate)((btn) => Evict(family)),
                //editing is only offered for an occupied lot that is not in the middle of a move-in.
                (family==null || moveIn!=null)?null:(ButtonClickDelegate)((btn) => EditFamily(family)),
                //rezoning is an empty lot's decision, and like editing it is not offered while a
                //move-in is in flight - the lot on screen is the one the waiting family is about
                //to be given, and its zone is part of what they are being given.
                (moveIn!=null)?null:(ButtonClickDelegate)((btn) => Rezone()),
                //Export is still out of scope: exporting a lot writes a .obj of its architecture
                //and buys from a different catalogue, and neither is a zoning change. It stays a
                //dead button on purpose rather than half-wired - see the roadmap's neighbourhood
                //management section, where it is a separate item.
                null,
                (btn) => ShowMore(false)
            };
            var optionNames = new string[]
            {
                (family==null)
                    ? SimitoneStrings.Get(BulldozeCaptionId, "Bulldoze")
                    : SimitoneStrings.Get(EvictCaptionId, "Evict"),
                SimitoneStrings.Get(EditFamilyCaptionId, "Edit Family"),
                SimitoneStrings.Get(RezoneCaptionId, "Rezone"),
                SimitoneStrings.Get(ExportCaptionId, "Export"),
                SimitoneStrings.Get(BackCaptionId, "Back")
            };

            var bY = extra+140;

            for (int i=0; i<optionFunctions.Length; i++)
            {
                var btn = new UIBigButton(i == optionFunctions.Length-1);
                btn.Caption = optionNames[i];
                btn.Position = new Vector2(screen.ScreenWidth / 4 - btn.Width / 2, bY);
                btn.Disabled = optionFunctions[i] == null;
                btn.OnButtonClick += optionFunctions[i];
                Add(btn);
                // pitch from the button's own height: the desktop art is 48px tall, the mobile
                // art 100px, so a fixed 120 leaves a huge gap on desktop and clips the 5th row
                // at 600px-tall windows.
                bY += ((screen as TS1GameScreen)?.Desktop ?? true) ? 62 : 120;
                OptionButtons.Add(btn);
            }
        }

        /// <summary>
        /// Rebuilds the panel in place, so a change it has just made to the lot is actually shown.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In place, rather than by building a second panel and swapping it, because the map's lot
        /// button holds this instance as "the panel for the selected lot": a replacement would be
        /// on screen while the map still thought the panel it had killed was the live one, and
        /// clicking the lot again would toggle nothing.
        /// </para>
        ///
        /// <para>
        /// Everything is asked again rather than patched, because a rezone changes three things at
        /// once - the caption, whether the lot has a price or a zone, and whether the lot may be
        /// entered - and all three are derived from the same two questions. The option button list
        /// is cleared first: <see cref="MoreTween"/> walks it, so a stale entry would put a
        /// discarded button back on screen.
        /// </para>
        /// </remarks>
        public void Refresh()
        {
            foreach (var child in new List<UIElement>(GetChildren()))
            {
                Remove(child);
            }
            OptionButtons.Clear();

            Build();
            //re-apply the tween, so a panel rebuilt while the options are open stays open rather
            //than snapping shut (or flashing its buttons in) because the new controls start at
            //their default visibility.
            MoreTween = MoreTween;
        }

        /// <summary>
        /// The zone this lot is currently read at.
        /// </summary>
        /// <remarks>
        /// The dictionary the neighbourhood loaded, with the same fallback the caption has always
        /// used for a lot the zoning file does not name (see
        /// <see cref="LotZoning.DefaultZoneForLot"/>) - most of the Downtown, Vacation, Studio and
        /// Magic lots, and residential lot 11, are in that position.
        ///
        /// <para>
        /// Asked from two places, and deliberately the same question in both: the caption, which
        /// decides between "is a community lot" and a price, and the rezone button, which decides
        /// which way the toggle is about to go. A rezone that showed the old caption, or offered
        /// the zone the lot was already in, would come from the two drifting apart.
        /// </para>
        /// </remarks>
        private short CurrentZone()
        {
            short zone;
            if (Content.Get().Neighborhood.ZoningDictionary.TryGetValue((short)HouseID, out zone))
            {
                return zone;
            }
            return LotZoning.DefaultZoneForLot(HouseID);
        }

        /// <summary>
        /// The **Rezone** option: switches this lot between the residential and community zones,
        /// after asking first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A rezone is not undoable from inside the game and it decides whether the next family may
        /// move in at all, so it is confirmed rather than applied on the click - the same "ask
        /// once, act on the answer" shape as the other destructive panel actions, on the screen's
        /// own <see cref="DialogService"/>, so a rezone and a quit cannot both be half-confirmed.
        /// </para>
        ///
        /// <para>
        /// The refusal on an occupied lot is the rule <c>NeighborhoodService.RezoneLot</c>
        /// implements, repeated here so the player is told at the point of the click rather than
        /// after a confirm they have no reason to doubt: a lot has to be emptied first, and
        /// <b>Evict</b> - the button above this one - is how.
        /// </para>
        /// </remarks>
        public void Rezone()
        {
            var screen = UIScreen.Current as TS1GameScreen;
            if (screen == null) return;
            var dialogs = screen.Dialogs;

            if (Content.Get().Neighborhood.GetFamilyForHouse((short)HouseID) != null)
            {
                dialogs.ShowAlert(
                    SimitoneStrings.Get(RezoneTitleId, "Lot Rezone"),
                    SimitoneStrings.Format(RezoneOccupiedId,
                        "There is still a family living at %s, so the lot cannot be rezoned.",
                        HouseID.ToString()),
                    SimitoneStrings.Get(RezoneOccupiedDetailId,
                        "A lot is zoned before anyone moves into it. Use Evict on this panel first."));
                return;
            }

            //the toggle always flips away from what the lot is now, so the confirm can name the
            //zone the lot is being moved to rather than "are you sure".
            var target = CurrentZone() == LotZoning.ZoneCommunity
                ? LotZoning.ZoneResidential
                : LotZoning.ZoneCommunity;

            if (!dialogs.TryBeginConfirm()) return;
            dialogs.ShowConfirm(
                SimitoneStrings.Get(RezoneTitleId, "Lot Rezone"),
                SimitoneStrings.Format(RezoneConfirmId, "Rezone %s as a %s lot?",
                    HouseID.ToString(), LotZoning.ZoneToken(target).Trim()),
                () => ApplyRezone(target),
                null);
        }

        /// <summary>
        /// Does the rezone the player confirmed, then puts this panel back in step with the lot.
        /// </summary>
        /// <remarks>
        /// Every refusal is reported. A rezone that quietly did nothing is the one outcome the
        /// player cannot detect on their own: the data file is the only record of the zone, and
        /// until the next launch nothing else in the game disagrees with what they were told.
        /// </remarks>
        /// <param name="zone">The zone the confirmed rezone is moving the lot to.</param>
        private void ApplyRezone(short zone)
        {
            var screen = UIScreen.Current as TS1GameScreen;
            if (screen == null) return;
            var dialogs = screen.Dialogs;

            var result = screen.RezoneLot((short)HouseID, zone);
            switch (result)
            {
                case RezoneLotResult.Rezoned:
                    //the caption, the price and whether the lot can be entered all changed, so ask
                    //the panel's own questions again rather than editing one label.
                    Refresh();
                    return;

                //the lot is already the zone the player asked for: the click was a no-op and the
                //caption already says so. Nothing to report.
                case RezoneLotResult.AlreadyThatZone:
                    return;

                case RezoneLotResult.Occupied:
                    dialogs.ShowAlert(
                        SimitoneStrings.Get(RezoneTitleId, "Lot Rezone"),
                        SimitoneStrings.Format(RezoneOccupiedId,
                            "There is still a family living at %s, so the lot cannot be rezoned.",
                            HouseID.ToString()),
                        SimitoneStrings.Get(RezoneOccupiedDetailId,
                            "A lot is zoned before anyone moves into it. Use Evict on this panel first."));
                    return;

                case RezoneLotResult.NoZoningEntry:
                    dialogs.ShowAlert(
                        SimitoneStrings.Get(RezoneTitleId, "Lot Rezone"),
                        SimitoneStrings.Format(RezoneNoEntryId,
                            "Lot %s has no zoning entry, so there is nothing to rezone.",
                            HouseID.ToString()),
                        SimitoneStrings.Get(RezoneNoEntryDetailId,
                            "LotZoning.iff only carries the lots the game shipped with a zone for."));
                    return;

                case RezoneLotResult.ShadowedEntry:
                    dialogs.ShowAlert(
                        SimitoneStrings.Get(RezoneTitleId, "Lot Rezone"),
                        SimitoneStrings.Format(RezoneShadowedId,
                            "Lot %s is not decided by its own zoning line, so it cannot be rezoned.",
                            HouseID.ToString()),
                        SimitoneStrings.Get(RezoneShadowedDetailId,
                            "LotZoning.iff also holds another line for that lot which is the one the"
                            + " game reads."));
                    return;

                case RezoneLotResult.ZoneFileMissing:
                    dialogs.ShowAlert(
                        SimitoneStrings.Get(RezoneTitleId, "Lot Rezone"),
                        SimitoneStrings.Get(RezoneUnreadableId,
                            "The neighbourhood's zoning file could not be read, so nothing was changed."));
                    return;

                case RezoneLotResult.WriteFailed:
                    dialogs.ShowAlert(
                        SimitoneStrings.Get(RezoneTitleId, "Lot Rezone"),
                        SimitoneStrings.Format(RezoneUnwrittenId,
                            "The zoning file could not be written, so lot %s is unchanged.",
                            HouseID.ToString()));
                    return;

                case RezoneLotResult.UnknownZone:
                default:
                    //unreachable: the only zones this panel offers are the two the file holds.
                    SimitoneLog.Warn("Rezone of lot " + HouseID + " asked for zone " + zone
                        + ", which is not a zoning value.");
                    return;
            }
        }

        public void Evict(FAMI family)
        {
            if (family == null) return;
            var familyName = family.ChunkParent.Get<FAMs>(family.ChunkID)?.GetString(0) ?? "selected";
            UIMobileAlert evictDialog = null;
            evictDialog = new UIMobileAlert(new UIAlertOptions()
            {
                Title = GameFacade.Strings.GetString("131", "2"),
                Message = GameFacade.Strings.GetString("131", "3", new string[] {
                    familyName.ToString(),
                    "§" + (family.ValueInArch + family.Budget).ToString("##,#0") }
                ),
                Buttons = UIAlertButton.YesNo(
                    (b) => { evictDialog.Close(); ((TS1GameScreen)UIScreen.Current).EvictLot(family, (short)HouseID); },
                    (b) => { evictDialog.Close(); }
                    )
            });
            UIScreen.GlobalShowDialog(evictDialog, true);
        }

        /// <summary>
        /// The **Bulldoze** option: clears an unoccupied lot's saved contents, after asking.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the eviction's bulldoze with the eviction's family handling left off, and it
        /// is deliberately not a second implementation of it: the work is
        /// <see cref="NeighborhoodService.ResetHouse"/>, the same call
        /// <c>NeighborhoodService.EvictLot</c> makes once it has refunded the family and moved it
        /// out. What this panel adds is the part that only makes sense on an empty lot - a
        /// confirm, because the contents are gone for good and there is no refund to reverse it,
        /// and a refusal, because bulldozing a house somebody is living in is not something the
        /// player can undo.
        /// </para>
        ///
        /// <para>
        /// The refusal is the one <see cref="Rezone"/> already makes, for the same reason: the
        /// button reads "Evict" whenever a family is here, so reaching this method anyway means
        /// a panel that was built before the family arrived. The player is pointed at the button
        /// that is supported rather than bulldozing an occupied lot.
        /// </para>
        /// </remarks>
        public void Bulldoze()
        {
            var screen = UIScreen.Current as TS1GameScreen;
            if (screen == null) return;
            var dialogs = screen.Dialogs;

            if (Content.Get().Neighborhood.GetFamilyForHouse((short)HouseID) != null)
            {
                SimitoneLog.Info("Refused to bulldoze lot " + HouseID
                    + ": a family still lives there. A lot has to be evicted before it can be cleared.");
                dialogs.ShowAlert(
                    SimitoneStrings.Get(BulldozeTitleId, "Lot Bulldoze"),
                    SimitoneStrings.Format(BulldozeOccupiedId,
                        "There is still a family living at %s, so the lot cannot be bulldozed.",
                        HouseID.ToString()),
                    SimitoneStrings.Get(BulldozeOccupiedDetailId,
                        "A bulldoze takes the contents of an empty lot. Use Evict on this panel first."));
                return;
            }

            if (!dialogs.TryBeginConfirm()) return;
            dialogs.ShowConfirm(
                SimitoneStrings.Get(BulldozeTitleId, "Lot Bulldoze"),
                SimitoneStrings.Format(BulldozeConfirmId,
                    "Bulldoze lot %s? Everything a family put in it is removed.",
                    HouseID.ToString()),
                () => ApplyBulldoze(screen),
                null);
        }

        /// <summary>
        /// Does the bulldoze the player confirmed, then puts this panel back in step with the lot.
        /// </summary>
        /// <remarks>
        /// There is no refusal to report and so no dialog here, unlike
        /// <see cref="ApplyRezone"/>. <see cref="NeighborhoodService.ResetHouse"/> never throws
        /// and has no in-band result: a lot it could not read is logged and left exactly as it
        /// was, and an empty lot whose contents it could not clear is indistinguishable from one
        /// it did clear until the next family moves in - so a dialog claiming success would be a
        /// claim this caller has no way to support. <see cref="Refresh"/> still runs, because the
        /// panel's view of the lot is re-derived from the lot rather than patched.
        /// </remarks>
        /// <param name="screen">The screen the confirmation was raised on.</param>
        private void ApplyBulldoze(TS1GameScreen screen)
        {
            SimitoneLog.Info("Bulldozing lot " + HouseID + " from the lot panel.");

            // Reuse the eviction's bulldoze rather than writing a second one. A
            // NeighborhoodService is a thin handle over the screen, and ResetHouse - all of what
            // decides which objects survive a bulldoze, and stages, backs up and commits the lot
            // file - is screen-independent, so a fresh instance is the entire wiring a
            // standalone bulldoze needs. The eviction path builds its own from the same screen.
            new NeighborhoodService(screen).ResetHouse(HouseID);

            Refresh();
        }

        /// <summary>
        /// Opens this lot's family in the Create-a-Sim family editor, so the sims that already live
        /// there can be renamed, re-skinned, added to or removed and saved back in place.
        ///
        /// This is the counterpart to the plain CAS button on the neighbourhood switcher, which
        /// always starts from an empty family. The FAMI ChunkID is all that is handed over; the CAS
        /// screen resolves the family itself and refuses (falling back to the family select screen)
        /// if it is not a member of the current neighbourhood. The house select panel does not need
        /// killing first - switching screens tears the whole panel down with it.
        /// </summary>
        public void EditFamily(FAMI family)
        {
            if (family == null) return;
            //chunk id 0 is the townies family, which is never editable from a lot.
            if (family.ChunkID == 0)
            {
                SimitoneLog.Warn("Asked to edit the townies family from lot " + HouseID + "; ignoring.");
                return;
            }
            var familyName = family.ChunkParent.Get<FAMs>(family.ChunkID)?.GetString(0) ?? "(unnamed)";
            SimitoneLog.Info("Opening family " + family.ChunkID + " (" + familyName + ") in CAS for editing.");
            GameController.EnterCAS(family.ChunkID);
        }

        public void ShowMore(bool more)
        {
            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "MoreTween", (more)?1f:0f } }, TweenQuad.EaseOut);
            /*
            var nonOptionButtons = new UIElement[]
            {
                LotDescription,
                SecondaryText,
                EnterLot,
                More
            };

            foreach (var btn in nonOptionButtons)
            {
                var b = btn as UIButton;
                if (b != null) b.Disabled = more;
            }

            foreach (var btn in OptionButtons)
            {
                btn.Disabled = !more;
            }
            */
        }

        public void Kill()
        {
            EnterLot.Opacity = 0.99f; //force an unpressable state
            More.Opacity = 0.99f;
            var screen = GameFacade.Screens.CurrentUIScreen;
            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "X", (screen.ScreenWidth / -2) - 32 } }, TweenQuad.EaseIn);
            GameThread.SetTimeout(() => { Parent.Remove(this); }, 500);
        }

        private void InitLabel(UILabel label)
        {
            label.CaptionStyle = label.CaptionStyle.Clone();
            label.CaptionStyle.Color = UIStyle.Current.Text;
            label.CaptionStyle.Size = 19;
            Add(label);
        }
    }

    public class UIHouseFamilyList : UIContainer
    {
        public List<UIAvatarSelectButton> Btns = new List<UIAvatarSelectButton>();
        
        public UIHouseFamilyList(FAMI family)
        {
            PopulateList(family);
        }

        public void PopulateList(FAMI family)
        {
            var world = new FSO.LotView.World(GameFacade.GraphicsDevice);
            world.Initialize(GameFacade.Scenes);
            var context = new VMContext(world);
            var vm = new VM(context, new VMServerDriver(new VMTS1GlobalLinkStub()), new VMNullHeadlineProvider());
            vm.Init();
            var blueprint = new Blueprint(1, 1);

            //world.InitBlueprint(blueprint);
            context.Blueprint = blueprint;
            context.Architecture = new VMArchitecture(1, 1, blueprint, vm.Context);

            int i = 0;
            var baseX = 0;
            foreach (var sim in family.FamilyGUIDs)
            {
                var fam = vm.Context.CreateObjectInstance(sim, LotTilePos.OUT_OF_WORLD, Direction.NORTH, true).BaseObject;
                var btn = new UIAvatarSelectButton(UIIconCache.GetObject(fam));
                btn.Opacity = 1f;
                var id = i;
                btn.Name = fam.Name;
                btn.X = baseX + (i++) * 100;
                btn.Y = 0;
                btn.DeregisterHandler();
                Btns.Add(btn);
                Add(btn);
                fam.Delete(true, vm.Context);
            }
            world.Dispose();
        }
    }
}
