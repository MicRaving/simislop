using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Simitone.Client.UI.Screens;
using FSO.Content;
using FSO.SimAntics.Model;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Rendering.Framework.IO;
using Simitone.Client.UI.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using FSO.SimAntics;
using Microsoft.Xna.Framework.Graphics;
using FSO.LotView.Model;
using FSO.Client;
using FSO.Client.UI.Framework;
using Simitone.Client.UI.Model;
using FSO.Client.UI.Controls;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    public class UIRelationshipSubpanel : UISubpanel
    {
        public List<Tuple<int, int>> Items = new List<Tuple<int, int>>();
        public UITouchScroll ScrollView;
        public int RelSort = -1;
        private bool _fameTabWarnedEmpty;

        public UITwoStateButton FriendButton;
        public UITwoStateButton FamButton;
        public UITwoStateButton AllButton;
        public UITwoStateButton FameButton;

        public UIRelationshipSubpanel(TS1GameScreen game) : base(game)
        {
            var ui = Content.Get().CustomUI;
            ScrollView = new UITouchScroll(() => { return Items?.Count ?? 0; }, DisplayProvider);
            ScrollView.X = 148;
            ScrollView.ItemWidth = 90;
            ScrollView.Size = new Vector2(582, 128);
            Add(ScrollView);

            FriendButton = new UITwoStateButton(ui.Get("rel_friend.png").Get(GameFacade.GraphicsDevice));
            FriendButton.Position = new Vector2(21, 9);
            FriendButton.OnButtonClick += (b) => { ChangeCat(RelationshipCategoryFilter.SortFriends); };
            Add(FriendButton);
            FamButton = new UITwoStateButton(ui.Get("rel_fam.png").Get(GameFacade.GraphicsDevice));
            FamButton.Position = new Vector2(81, 9);
            FamButton.OnButtonClick += (b) => { ChangeCat(RelationshipCategoryFilter.SortFamily); };
            Add(FamButton);
            AllButton = new UITwoStateButton(ui.Get("rel_all.png").Get(GameFacade.GraphicsDevice));
            AllButton.Position = new Vector2(21, 69);
            AllButton.OnButtonClick += (b) => { ChangeCat(RelationshipCategoryFilter.SortAll); };
            Add(AllButton);
            FameButton = new UITwoStateButton(ui.Get("rel_fame.png").Get(GameFacade.GraphicsDevice));
            FameButton.Position = new Vector2(81, 69);
            FameButton.OnButtonClick += (b) => { ChangeCat(RelationshipFameFilter.SortFame); };
            Add(FameButton);

            ChangeCat(RelationshipCategoryFilter.SortAll);
        }

        public void ChangeCat(int cat)
        {
            FriendButton.Selected = cat == RelationshipCategoryFilter.SortFriends;
            FamButton.Selected = cat == RelationshipCategoryFilter.SortFamily;
            AllButton.Selected = cat == RelationshipCategoryFilter.SortAll;
            FameButton.Selected = cat == RelationshipFameFilter.SortFame;
            RelSort = cat;
        }

        public UITSContainer DisplayProvider(int index)
        {
            return new UIRelationshipDisplay(Items[index].Item1, Items[index].Item2, Game.vm);
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            UpdateRelView();
            Invalidate();
        }

        public void UpdateRelView()
        {
            var sel = Game.SelectedAvatar;
            var neighbourhood = Content.Get().Neighborhood;
            if (sel == null) return;
            var neighbour = sel.GetPersonData(VMPersonDataVariable.NeighborId);

            var n = neighbourhood.GetNeighborByID(neighbour);
            var rel = n.Relationships;

            // RelSort doubles as the button's selected state AND the filter to apply. Every tab
            // narrows now: friend (0) and family (1) were pass-through for years, and the fame
            // tab (2) already was not - RelationshipCategoryFilter holds all four rules.
            var viewerFamily = RelationshipCategoryFilter.FamilyNumberOf(sel.GetPersonData(VMPersonDataVariable.TS1FamilyNumber));

            var rItems = RelationshipCategoryFilter.Apply(rel, RelSort, viewerFamily,
                otherNid => RelationshipCategoryFilter.FamilyNumberOf(
                    neighbourhood.GetNeighborByID((short)otherNid)
                        ?.PersonData?[(int)VMPersonDataVariable.TS1FamilyNumber] ?? 0),
                otherNid => RelationshipFameFilter.FameScoreOf(
                    neighbourhood.GetNeighborByID((short)otherNid)?.PersonData))
            .Select(x => new Tuple<int,int>(neighbour, x.Key)).ToList();

            if (RelSort == RelationshipFameFilter.SortFame && rItems.Count == 0 && !_fameTabWarnedEmpty)
            {
                // The fame tab is reachable but nothing can ever satisfy it until the engine
                // starts moving TS1FameScore (VMPersonDataVariable 80). Say so once rather
                // than shipping a tab that silently renders an empty list every frame.
                _fameTabWarnedEmpty = true;
                SimitoneLog.Warn("Relationship fame tab is empty: no neighbour has a TS1FameScore above "
                    + RelationshipFameFilter.MinCelebrityFame + ".");
            }

            bool difference = false;
            if (rItems.Count == Items.Count)
            {
                for (int i = 0; i < rItems.Count; i++)
                {
                    if (!rItems[i].Equals(Items[i])) {
                        difference = true; break;
                    }
                }
            } else
            {
                difference = true;
            }

            if (difference)
            {
                Items = rItems;
                ScrollView.Reset();
            }
        }
    }

    /// <summary>
    /// The category filter behind the relationship subpanel's four tab buttons.
    ///
    /// <para>
    /// <see cref="UIRelationshipSubpanel.RelSort"/> is the selected tab and each button maps one
    /// value onto it: <see cref="SortAll"/> (-1) = all, <see cref="SortFriends"/> (0) = friends,
    /// <see cref="SortFamily"/> (1) = family, <see cref="RelationshipFameFilter.SortFame"/> (2) =
    /// famous. Every tab narrows the selected sim's relationship table; only "all" passes it
    /// through untouched.
    /// </para>
    ///
    /// <para>
    /// <b>Where the answers come from.</b> A relationship entry is a bare
    /// <c>Dictionary&lt;int, List&lt;short&gt;&gt;</c> keyed by the <em>other</em> sim's neighbour
    /// id, and the vector is the same one the engine's <c>relationship</c> primitive (opcode 26)
    /// and <c>old_relationship</c> (24) read and write, in the layout the neighbourhood's
    /// <c>NBRS</c> chunk uses:
    /// <list type="bullet">
    /// <item><see cref="RelationshipIndex"/> - the relationship score, the first bar on the tile;</item>
    /// <item><see cref="FriendFlagIndex"/> - the "are these two friends" flag;</item>
    /// <item><see cref="FriendshipIndex"/> - the friendship score, the second bar.</item>
    /// </list>
    /// Both the friend flag and the friendship bar are the engine's own, not this panel's
    /// invention, which is what makes the friend tab agree with what the game's scripts think
    /// about the same pair of sims. The indices are named constants rather than literals so a
    /// correction to the layout is a one-line change with a test failure pointing at it.
    /// </para>
    ///
    /// <para>
    /// <b>Family</b> is a different question and is answered from person data rather than from
    /// the relationship vector: TS1 stores the family slot on every sim as
    /// <see cref="VMPersonDataVariable.TS1FamilyNumber"/> (slot 61), and the phone book's own
    /// panel (<c>UICallNeighborPanel</c>) groups its list by exactly that slot. A family number of
    /// zero means "no family", so a sim whose slot is unset never claims a family member - and
    /// neither does the viewer, which is why a sim with no family sees an empty family tab rather
    /// than every other sim in the neighbourhood.
    /// </para>
    ///
    /// <para>
    /// Every lookup is a delegate returning a nullable or zero value, so a neighbour that has
    /// left the neighbourhood, has no person data yet, or is not in the table at all reads as "no"
    /// instead of throwing out of the render loop.
    /// </para>
    /// </summary>
    public static class RelationshipCategoryFilter
    {
        /// <summary>The <see cref="UIRelationshipSubpanel.RelSort"/> value the All button selects.</summary>
        public const int SortAll = -1;

        /// <summary>The <see cref="UIRelationshipSubpanel.RelSort"/> value the Friend button selects.</summary>
        public const int SortFriends = 0;

        /// <summary>The <see cref="UIRelationshipSubpanel.RelSort"/> value the Family button selects.</summary>
        public const int SortFamily = 1;

        /// <summary>Index of the relationship score - the top bar on a relationship tile.</summary>
        public const int RelationshipIndex = 0;

        /// <summary>Index of the "is a friend" flag in the relationship vector.</summary>
        public const int FriendFlagIndex = 1;

        /// <summary>Index of the friendship score - the bottom bar on a relationship tile.</summary>
        public const int FriendshipIndex = 2;

        /// <summary>
        /// The family number a person-data reading means "no family at all".
        /// </summary>
        /// <remarks>
        /// Slot 61 is zero on a sim that was never put in a family, and the phone book skips
        /// those rather than filing them under family 0 - so zero has to be excluded on both
        /// sides of the comparison or every homeless sim becomes everybody's relative.
        /// </remarks>
        public const short NoFamily = 0;

        /// <summary>
        /// Normalises a raw family-number reading, mapping anything at or below
        /// <see cref="NoFamily"/> to zero.
        /// </summary>
        public static short FamilyNumberOf(short familyNumber)
        {
            return familyNumber > NoFamily ? familyNumber : NoFamily;
        }

        /// <summary>
        /// Is this relationship vector one between friends?
        /// </summary>
        /// <remarks>
        /// The flag at <see cref="FriendFlagIndex"/> is the only evidence of a friendship, and
        /// the friendship <em>score</em> at <see cref="FriendshipIndex"/> is deliberately not
        /// consulted: it is the number on the second bar, which the scripts move for anybody a
        /// sim has been socialising with, and treating it as a flag would list everyone anybody
        /// has ever talked to.
        ///
        /// <para>
        /// A vector too short to hold the flag lists nobody, which is the honest answer: the
        /// engine pads a relationship vector out to the slot it writes, so a short one is a
        /// relationship that has never been written to rather than one that was written
        /// differently.
        /// </para>
        /// </remarks>
        public static bool IsFriend(IList<short> relationship)
        {
            if (relationship == null) return false;
            if (relationship.Count <= FriendFlagIndex) return false;
            return relationship[FriendFlagIndex] > 0;
        }

        /// <summary>
        /// Are these two sims in the same family? False unless both slots name a real family and
        /// the two agree.
        /// </summary>
        public static bool IsFamilyMember(short viewerFamily, short otherFamily)
        {
            return viewerFamily > NoFamily && viewerFamily == otherFamily;
        }

        /// <summary>
        /// Narrows a relationship table to the selected tab.
        /// </summary>
        /// <param name="relationships">The selected neighbour's relationship table.</param>
        /// <param name="relSort">The selected tab, as <c>UIRelationshipSubpanel.RelSort</c>.</param>
        /// <param name="viewerFamily">
        /// The selected sim's family number, already through <see cref="FamilyNumberOf"/>.
        /// </param>
        /// <param name="otherFamilyNumberOf">
        /// The family number of the sim with the given neighbour id, or 0 when it is unknown.
        /// </param>
        /// <param name="fameScoreOf">
        /// Fame score of the sim with the given neighbour id, or null when it is unknown.
        /// </param>
        public static IEnumerable<KeyValuePair<int, List<short>>> Apply(
            IEnumerable<KeyValuePair<int, List<short>>> relationships,
            int relSort,
            short viewerFamily,
            Func<int, short> otherFamilyNumberOf,
            Func<int, short?> fameScoreOf)
        {
            if (relationships == null) return Enumerable.Empty<KeyValuePair<int, List<short>>>();
            if (relSort == SortAll) return relationships;

            if (relSort == SortFriends)
            {
                return relationships.Where(x => IsFriend(x.Value));
            }

            if (relSort == SortFamily)
            {
                if (otherFamilyNumberOf == null) return relationships;
                return relationships.Where(x => IsFamilyMember(
                    viewerFamily,
                    FamilyNumberOf(otherFamilyNumberOf(x.Key))));
            }

            if (relSort == RelationshipFameFilter.SortFame)
            {
                return RelationshipFameFilter.Apply(relationships, RelationshipFameFilter.SortFame, fameScoreOf);
            }

            // An unrecognised tab (a stale RelSort) shows everything rather than emptying the
            // list: a filter that silently drops every row is worse than one that does not
            // narrow.
            return relationships;
        }
    }

    /// <summary>
    /// The celebrity half of the relationship subpanel's tab filter, kept separate because it is
    /// the only rule that needs a *threshold* rather than a flag, and because it predates the
    /// friend and family rules (which is what <see cref="RelationshipCategoryFilter"/> composes).
    ///
    /// A sim counts as a celebrity when its <see cref="VMPersonDataVariable.TS1FameScore"/> is
    /// strictly greater than <see cref="MinCelebrityFame"/>.
    ///
    /// The lookup is passed in as a <see cref="Func{T, TResult}"/> returning a nullable score so
    /// this stays pure static: a neighbour that is missing from the neighbourhood, or whose
    /// person data has not been built yet, yields <c>null</c> and is simply not a celebrity.
    /// A lifted <c>short?</c> comparison makes that fall out of the predicate for free.
    /// </summary>
    public static class RelationshipFameFilter
    {
        /// <summary>The <see cref="UIRelationshipSubpanel.RelSort"/> value the fame button selects.</summary>
        public const int SortFame = 2;

        /// <summary>
        /// A neighbour needs a fame score strictly above this to be listed by the fame tab.
        /// </summary>
        public const short MinCelebrityFame = 1;

        /// <summary>
        /// Read a neighbour's fame score out of its TS1 person data.
        /// <paramref name="personData"/> is the raw <c>short[]</c> behind
        /// <see cref="Neighbour.PersonData"/>. The neighbourhood allocates 88 entries and
        /// <see cref="VMPersonDataVariable.TS1FameScore"/> is 80, but that array can be null
        /// for a neighbour with no person mode, so the length is checked rather than trusted.
        /// </summary>
        public static short? FameScoreOf(short[] personData)
        {
            if (personData == null) return null;

            int index = (int)VMPersonDataVariable.TS1FameScore;
            if (index >= personData.Length) return null;

            return personData[index];
        }

        /// <summary>
        /// Narrow a relationship table to the famous. A null table yields an empty sequence
        /// rather than throwing, and a missing lookup passes the input through rather than
        /// dropping every row.
        /// </summary>
        /// <param name="relationships">The selected neighbour's relationship table.</param>
        /// <param name="relSort">The selected tab, as <c>UIRelationshipSubpanel.RelSort</c>.</param>
        /// <param name="fameScoreOf">Fame score of the neighbour with the given id, or null if unknown.</param>
        public static IEnumerable<KeyValuePair<int, List<short>>> Apply(
            IEnumerable<KeyValuePair<int, List<short>>> relationships,
            int relSort,
            Func<int, short?> fameScoreOf)
        {
            if (relationships == null) return Enumerable.Empty<KeyValuePair<int, List<short>>>();
            if (relSort != SortFame || fameScoreOf == null) return relationships;

            return relationships.Where(x => fameScoreOf(x.Key) > MinCelebrityFame);
        }
    }

    /// <summary>
    /// Turning a drag of a relationship bar into a number, and putting that number back.
    ///
    /// <para>
    /// Retail's signature relationship interaction is grab the bar and pull it, and this is the
    /// whole of that: the bar is 200 points wide as a value (<see cref="MinValue"/> to
    /// <see cref="MaxValue"/>, the same range the engine's <c>relationship</c> primitive clamps
    /// to), so a pointer at one end of the bar means one extreme and a pointer in the middle
    /// means neutral.
    /// </para>
    ///
    /// <para>
    /// The mapping is absolute rather than relative, deliberately. A relative drag would have to
    /// remember where in the bar the grab happened, and the relationship tiles live in a
    /// scrolling strip that can be yanked sideways mid-drag - an absolute mapping is one less
    /// piece of state to get wrong and gives the same answer whichever end the player started
    /// from.
    /// </para>
    /// </summary>
    public static class RelationshipBarDrag
    {
        /// <summary>Lowest value a relationship can be dragged to.</summary>
        public const short MinValue = -100;

        /// <summary>Highest value a relationship can be dragged to.</summary>
        public const short MaxValue = 100;

        /// <summary>
        /// The relationship value at a position along the bar, as a 0..1 fraction of its width.
        /// </summary>
        /// <remarks>
        /// Out-of-range fractions are clamped rather than wrapped, so a pointer dragged off
        /// either end of the bar pins the value to that end instead of throwing it to the
        /// opposite extreme.
        /// </remarks>
        public static short ValueForFraction(float fraction)
        {
            if (float.IsNaN(fraction)) return 0;
            if (fraction < 0f) fraction = 0f;
            if (fraction > 1f) fraction = 1f;
            return Clamp((short)Math.Round(MinValue + ((MaxValue - MinValue) * fraction)));
        }

        /// <summary>
        /// Keeps a dragged value inside the range the engine stores.
        /// </summary>
        public static short Clamp(short value)
        {
            if (value < MinValue) return MinValue;
            if (value > MaxValue) return MaxValue;
            return value;
        }

        /// <summary>
        /// The fraction along the bar a stored value sits at - the inverse of
        /// <see cref="ValueForFraction"/>, and what the bar itself draws.
        /// </summary>
        public static float FractionForValue(short value)
        {
            return (Clamp(value) - (float)MinValue) / (MaxValue - MinValue);
        }

        /// <summary>
        /// Writes a dragged value into a relationship vector, padding the vector out to
        /// <paramref name="index"/> first.
        /// </summary>
        /// <remarks>
        /// The engine does the same padding in <c>VMRelationship</c>: a relationship vector
        /// written by a build that stopped at the first entry has no slot 1 or 2 to assign, and
        /// a drag that silently did nothing would be the least explicable thing this panel could
        /// do. Returns whether anything changed, so the caller can avoid a pointless write.
        /// </remarks>
        public static bool Apply(IList<short> relationship, int index, short value)
        {
            if (relationship == null || index < 0) return false;
            var wanted = Clamp(value);
            while (relationship.Count <= index) relationship.Add(0);
            if (relationship[index] == wanted) return false;
            relationship[index] = wanted;
            return true;
        }
    }

    /// <summary>
    /// One relationship tile: the other sim's portrait and name, and the two bars - the
    /// relationship score and the friendship score.
    ///
    /// <para>
    /// <b>The bars can be dragged</b>, which is retail's signature relationship interaction and
    /// was the one thing the tile never did. A press picks the bar, <see cref="Update"/> tracks
    /// the pointer past the tile's own edges (a <c>ListenForMouse</c> region stops firing the
    /// moment the pointer leaves it, so the drag cannot be driven from the event), and every
    /// frame in between writes the position straight into the viewer's relationship vector -
    /// the same live <c>Neighbour.Relationships</c> the engine's <c>relationship</c> primitive
    /// reads and writes. Nothing saves on the spot; the value is part of the neighbourhood from
    /// that frame on, and the ordinary save path persists it exactly as it persists a
    /// relationship a script changed.
    /// </para>
    /// </summary>
    public class UIRelationshipDisplay : UITSContainer {
        private UILabel NameLabel;
        private Texture2D ItemBg;
        private Texture2D Item;
        private UIMotiveBar Bar1;
        private UIMotiveBar Bar2;

        private int NIDF;
        private int NID;

        /// <summary>
        /// Where the two bars are drawn, and the bands the pointer has to land in to grab them.
        /// </summary>
        /// <remarks>
        /// The bars themselves are drawn at half scale (a 150px bar in a 90px tile), which
        /// leaves a hit target about six pixels tall - too small to grab on a touch screen, and
        /// awkward with a mouse as well. The bands below are the full unscaled bar width and a
        /// fixed 14px tall, which is why they are written down here rather than read back off
        /// the bar elements: the bar's <c>Scale</c> is not something a rectangle can be asked
        /// about.
        /// </remarks>
        private static readonly Rectangle BarBand0 = new Rectangle(8, 90, 75, 14);
        private static readonly Rectangle BarBand1 = new Rectangle(8, 102, 75, 14);

        /// <summary>The value index each band edits: relationship, then friendship.</summary>
        private static readonly int[] BarValueIndex = new int[]
        {
            RelationshipCategoryFilter.RelationshipIndex,
            RelationshipCategoryFilter.FriendshipIndex
        };

        /// <summary>Mouse currently dragging a bar, or -1 when none is.</summary>
        private int DragMouseID = -1;

        /// <summary>Which of the two bands the current drag is editing.</summary>
        private int DragBar = -1;

        public UIRelationshipDisplay(int nidFrom, int nid, VM curVM) : base()
        {
            NameLabel = new UILabel();
            NameLabel.CaptionStyle = NameLabel.CaptionStyle.Clone();
            NameLabel.CaptionStyle.Color = UIStyle.Current.Text;
            NameLabel.CaptionStyle.Size = 10;
            NameLabel.Wrapped = true;
            NameLabel.Size = new Vector2(80, 33);
            NameLabel.Position = new Vector2(5, 5);
            NameLabel.Alignment = TextAlignment.Middle | TextAlignment.Center;
            Add(NameLabel);

            Bar1 = new UIMotiveBar();
            Bar1.ScaleX = Bar1.ScaleY = 0.5f;
            Bar1.Position = new Vector2(8, 94);
            Add(Bar1);

            Bar2 = new UIMotiveBar();
            Bar2.ScaleX = Bar2.ScaleY = 0.5f;
            Bar2.Position = new Vector2(8, 106);
            Add(Bar2);

            ItemBg = Content.Get().CustomUI.Get("inv_item.png").Get(GameFacade.GraphicsDevice);

            var neighbourhood = Content.Get().Neighborhood;
            var n = neighbourhood.GetNeighborByID((short)nid);
            var obj = Content.Get().WorldObjects.Get(n.GUID);
            if (obj != null)
            {
                var aobj = curVM.Context.CreateObjectInstance(n.GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH, true);
                Item = UIIconCache.GetObject(aobj.BaseObject);
                aobj.Delete(curVM.Context);
                var ctss = obj.Resource.Get<CTSS>(obj.OBJ.CatalogStringsID);
                //todo: family name
                NameLabel.Caption = ctss?.GetString(0) ?? obj.OBJ.ChunkLabel;
            }
            NIDF = nidFrom;
            NID = nid;

            ListenForMouse(BarBand0, (type, state) => OnBarMouse(0, type, state));
            ListenForMouse(BarBand1, (type, state) => OnBarMouse(1, type, state));
        }

        /// <summary>
        /// Starts or ends a drag on one of the two bars.
        /// </summary>
        /// <remarks>
        /// Only the press is handled here. The drag itself is driven from
        /// <see cref="Update"/> against <c>state.MouseStates</c> rather than from a mouse
        /// event, because a <c>ListenForMouse</c> region only fires while the pointer is over
        /// it - and a drag that stops the moment the player pulls the bar past its own edge is
        /// not a drag. <see cref="UIElement.GlobalPoint"/> is used on the press to work out
        /// which band was grabbed, so the same bands serve both the hit test and the mapping.
        /// </remarks>
        private void OnBarMouse(int bar, UIMouseEventType type, UpdateState state)
        {
            if (type == UIMouseEventType.MouseDown)
            {
                DragMouseID = state.CurrentMouseID;
                DragBar = bar;
                ApplyDrag(state);
            }
            else if (type == UIMouseEventType.MouseUp)
            {
                EndDrag();
            }
        }

        /// <summary>Forgets the current drag, if any.</summary>
        private void EndDrag()
        {
            DragMouseID = -1;
            DragBar = -1;
        }

        /// <summary>
        /// The viewer's relationship vector to this sim, or null when the pair is gone.
        /// </summary>
        private List<short> RelationshipVector()
        {
            var me = Content.Get().Neighborhood.GetNeighborByID((short)NIDF);
            var relationships = me?.Relationships;
            if (relationships == null) return null;
            return relationships.TryGetValue(NID, out var values) ? values : null;
        }

        /// <summary>
        /// Maps the dragging pointer's position onto the bar being dragged and writes it.
        /// </summary>
        private void ApplyDrag(UpdateState state)
        {
            if (DragBar < 0) return;
            var mouse = state.MouseStates.FirstOrDefault(x => x.ID == DragMouseID);
            if (mouse == null) return;

            var band = DragBar == 0 ? BarBand0 : BarBand1;
            var local = GlobalPoint(mouse.MouseState.Position.ToVector2());
            var fraction = (float)((local.X - band.X) / (double)band.Width);
            var wanted = RelationshipBarDrag.ValueForFraction(fraction);

            if (!RelationshipBarDrag.Apply(RelationshipVector(), BarValueIndex[DragBar], wanted)) return;
            Invalidate();
        }

        public override void Update(UpdateState state)
        {
            if (DragMouseID != -1)
            {
                // A touch that has been lifted is reported as a released button and then dropped
                // from MouseStates entirely, so both have to end the drag: checking only for a
                // released button would leave the drag armed for a mouse that is never coming
                // back, and the next press anywhere on the tile would jump the bar.
                var mouse = state.MouseStates.FirstOrDefault(x => x.ID == DragMouseID);
                if (mouse == null || mouse.MouseState.LeftButton != ButtonState.Pressed)
                {
                    EndDrag();
                }
                else
                {
                    ApplyDrag(state);
                }
            }

            var rel = RelationshipVector();
            if (rel != null)
            {
                if (rel.Count > 0) Bar1.MotiveValue = rel[RelationshipCategoryFilter.RelationshipIndex];
                if (rel.Count > RelationshipCategoryFilter.FriendshipIndex)
                    Bar2.MotiveValue = rel[RelationshipCategoryFilter.FriendshipIndex];
            }
            base.Update(state);
        }

        public override void Draw(UISpriteBatch batch)
        {
            DrawLocalTexture(batch, ItemBg, null, new Vector2(20, 40), Vector2.One, new Color(104, 164, 184, 255));

            if (Item != null)
            {
                DrawLocalTexture(batch, Item, null, new Vector2(45 + 42 / -2, 65 + 42 / -2), new Vector2(42f / Item.Height, 42f / Item.Height));
            }

            base.Draw(batch);
        }
    }
}
