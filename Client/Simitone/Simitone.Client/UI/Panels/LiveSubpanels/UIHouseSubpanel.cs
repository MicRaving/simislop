/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.Model;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    /// <summary>
    /// The House panel: the household's chores, and who is working for it.
    ///
    /// <para>
    /// <b>Why a panel.</b> Retail's live-mode strip had a House category with two things in it -
    /// the chore list, and hire-a-service for the maid, the gardener and the repairman. One of
    /// those two is real data this tree can read; the other is not data at all, and the panel
    /// says so with a reason rather than showing an empty box.
    /// </para>
    ///
    /// <para>
    /// <b>The chores half, and why it is empty.</b> Not "no data" - there is provably no chore
    /// data anywhere in The Sims 1, which is a different and more useful claim. A census over the
    /// eleven shipped <c>.far</c> cabinets (<c>Global.far</c>, <c>Objects.far</c>, <c>Deluxe.far</c>,
    /// <c>ExpansionPack.far</c> .. <c>ExpansionPack7.far</c>, <c>ExpansionShared.far</c>) finds
    /// <list type="bullet">
    /// <item><b>zero</b> chore strings - no <c>TTA</c>/<c>STR</c> entry, no <c>dialog_private</c>
    /// text and no BHAV literal in any of the cabinets contains "chore" in any casing;</item>
    /// <item><b>zero</b> chore trees and <b>zero</b> chore objects - no <c>OBJD</c> is labelled
    /// or semiglobal-named for one, and no <c>BHAV</c> label contains the word;</item>
    /// <item><b>no spare lot global</b> for one. A census of every <c>Global[]</c> expression in
    /// the same eleven cabinets touches indices 0 to 36 and nothing above. A <c>SIMI</c> puts at
    /// most 32 globals on the wire (<c>SIMI.GlobalsOnWire</c> returns 0x20 for the 0x3E every
    /// save is stamped with), and 32 to 37 read back as zero in every shipped lot, so a chore
    /// list has nowhere to live in a <c>House{NN}.iff</c>;</item>
    /// <item><b>no chore code</b> in the engine either - <c>FSO.SimAntics</c> has no reference to
    /// one outside an unrelated <c>EchoRequested</c> field.</item>
    /// </list>
    /// The chore list was therefore engine-side panel state inside <c>Sims.exe</c>, alongside the
    /// 128px strip that drew it, and it was never saved. There is nothing for this panel to read,
    /// write, or reconstruct - not a missing reader, but a missing thing. That is what
    /// <see cref="HouseServices.ChoresWhyId"/> says on screen.
    /// </para>
    ///
    /// <para>
    /// <b>The hire half is real, and this panel previously read the wrong thing.</b> It used to
    /// show a per-sim "rehire ticker" out of person data 88, 89 and 90
    /// (<c>VMPersonDataVariable.MaidRehireTicker</c> and its two neighbours). Those three slots
    /// are named in the engine's enum, they sit inside the 101-short runtime array, and
    /// <b>no shipped script has ever written them</b>: a census of every
    /// <c>MyPersonData</c>/<c>StackPersonData</c>/<c>NeighborPersonData</c> expression across the
    /// eleven cabinets finds 87 distinct slots and a maximum of 87, so all three rows were
    /// permanently zero and every row always read "Can be hired". They are gone from this panel
    /// rather than dressed up.
    /// </para>
    ///
    /// <para>
    /// What the retail scripts <em>do</em> keep is on <c>Objects/NPCController.iff</c> (OBJD
    /// 0x41A7, GUID <c>0x70F69082</c>, <c>Global = 1</c>, so it is spawned onto every lot on
    /// load). Its <c>'init tree'</c> zeroes attributes 1, 2 and 3; <c>'Block Maid'</c> /
    /// <c>'Block Gardener'</c> / <c>'Block Repairman'</c> write 5 into attributes 1, 2 and 3; and
    /// <c>'Is Maid Blocked?'</c> / <c>'Is Gardener Blocked?'</c> / <c>'Is Repairman Blocked?'</c>
    /// are three one-instruction trees that test attribute 1 / 2 / 3 against zero. The phone
    /// reaches them by name: <c>PhoneGlobals.iff</c> 8241-8243 select the controller with
    /// <c>set_to_next</c> on that same GUID and <c>run_tree_by_name</c> against
    /// <c>Global.iff</c> <c>STR 0x012F[61..63]</c>, whose entries are exactly those three
    /// strings. A zero is what a lot with nobody on the books has, because that is what
    /// <c>'init tree'</c> writes. So attribute 1 = maid, 2 = gardener, 3 = repairman, and
    /// <b>zero means the household can hire that service</b>.
    /// </para>
    /// </summary>
    public class UIHouseSubpanel : UISubpanel
    {
        private const int RowHeight = 24;
        private const int FirstX = 18;
        private const int FirstY = 10;
        private const int FirstRowY = 56;
        private const int ChoreColumnWidth = 370;
        private const int HireColumnX = FirstX + 400;

        private UILabel choresTitle;
        private UILabel choresBody;
        private UILabel choresWhy;
        private UILabel[] serviceNames;
        private UILabel[] serviceStates;

        public UIHouseSubpanel(TS1GameScreen game) : base(game)
        {
            // Everything is laid out inside the strip's own 128px (UISubpanel.PanelHeight),
            // because past that the strip's background has stopped and a label is drawn over the
            // lot view with no plate under it.
            choresTitle = MakeLabel(FirstX, FirstY, ChoreColumnWidth, 22, 17);
            choresTitle.Caption = SimitoneStrings.Get(HouseServices.ChoresTitleId, "Chores");
            Add(choresTitle);

            choresBody = MakeLabel(FirstX, FirstY + 22, ChoreColumnWidth, 38, 12);
            choresBody.Wrapped = true;
            choresBody.MaxLines = 2;
            choresBody.Alignment = TextAlignment.Top | TextAlignment.Left;
            choresBody.Caption = SimitoneStrings.Get(HouseServices.ChoresBodyId,
                "Chores are assigned by the family, not by the game: the household's chore list "
                + "lives in the lot's own data and nothing here can read it.");
            Add(choresBody);

            // The sentence above is Simitone's own and is already in the shipped string table, so
            // it is left exactly as it was - including the part of it that puts the chore list in
            // the lot's data, which the census in this file's own header says is not where it
            // was. The line under it is the reason a player would actually want: what is missing,
            // specifically, and why no amount of reading would find it.
            choresWhy = MakeLabel(FirstX, FirstY + 60, ChoreColumnWidth, 56, 12);
            choresWhy.Wrapped = true;
            choresWhy.MaxLines = 4;
            choresWhy.Alignment = TextAlignment.Top | TextAlignment.Left;
            choresWhy.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            choresWhy.Caption = HouseServices.ChoresWhy();
            Add(choresWhy);

            var columnX = HireColumnX;
            var header = MakeLabel(columnX, FirstY, 320, 22, 17);
            header.Caption = SimitoneStrings.Get(HouseServices.HireTitleId, "Hire a Service");
            Add(header);

            var note = MakeLabel(columnX, FirstY + 22, 340, 22, 12);
            note.Wrapped = true;
            note.MaxLines = 1;
            note.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            note.Caption = SimitoneStrings.Get(HouseServices.HireBodyId,
                "Hiring is the household's own business: ring the service, or use the service's object.");
            Add(note);

            var services = HouseServices.All;
            serviceNames = new UILabel[services.Length];
            serviceStates = new UILabel[services.Length];

            for (int i = 0; i < services.Length; i++)
            {
                var y = FirstRowY + (i * RowHeight);
                serviceNames[i] = MakeLabel(columnX, y, 150, 22, 15);
                serviceNames[i].Caption = HouseServices.NameOf(services[i]);
                Add(serviceNames[i]);

                serviceStates[i] = MakeLabel(columnX + 155, y, 340, 22, 13);
                serviceStates[i].CaptionStyle.Color = UIStyle.Current.SecondaryText;
                serviceStates[i].Caption = "";
                Add(serviceStates[i]);
            }
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            // Deliberately not gated on Game.SelectedAvatar. Whether the household has a maid is
            // a fact about the lot, not about whichever sim happens to be selected, and the row
            // used to blank out every frame nothing was selected.
            var vm = Game.vm;
            var services = HouseServices.All;
            for (int i = 0; i < services.Length; i++)
            {
                serviceStates[i].Caption = HouseServices.StateCaption(HouseServices.StateOf(vm, services[i]));
            }
        }

        private UILabel MakeLabel(int x, int y, int width, int height, int size)
        {
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
    }

    /// <summary>The three services a household in TS1 can hire.</summary>
    public enum HouseholdService
    {
        /// <summary>The maid, who cleans.</summary>
        Maid,

        /// <summary>The gardener.</summary>
        Gardener,

        /// <summary>The repairman.</summary>
        Repairman,
    }

    /// <summary>
    /// What a hire row can say about one service. Four states because "the household has a maid
    /// on the books" and "a maid is standing in the kitchen right now" are different facts, and
    /// conflating them is how a panel ends up claiming a maid is working after she has gone
    /// home. The fourth is the one a bug produces, and it is worth its own word.
    /// </summary>
    public enum HouseholdServiceState
    {
        /// <summary>
        /// The lot has no NPC controller, so the flag this panel reads does not exist. The
        /// controller is <c>Global = 1</c> and is spawned on every lot load, so this means
        /// something is wrong with the lot rather than that the household has nobody.
        /// </summary>
        NoController,

        /// <summary>The flag is zero, which is what <c>'init tree'</c> writes: nobody is on the books.</summary>
        NotHired,

        /// <summary>On the books, and the service's NPC is an avatar on this lot right now.</summary>
        HiredOnDuty,

        /// <summary>On the books, but the NPC is not on this lot at the moment.</summary>
        HiredOffDuty,
    }

    /// <summary>
    /// The hire-a-service rows: where the retail hire state lives, and how it is read.
    ///
    /// <remarks>
    /// <para>
    /// <b>Where the state is.</b> One object. <c>Objects/NPCController.iff</c> carries a single
    /// OBJD, 0x41A7, GUID <see cref="ControllerGuid"/>, with <c>Global = 1</c> - the flag
    /// <c>VMTS1ActivatorNew</c> uses to spawn a controller onto a lot whether or not the lot
    /// template mentions it - and sixteen attributes. Three of them are the hire flags, and which
    /// three is settled by the shipped trees rather than guessed:
    /// </para>
    /// <list type="bullet">
    /// <item><c>4101 'init tree'</c> - <c>MyAttrs[1] = MyAttrs[2] = MyAttrs[3] = 0</c>, so a lot
    /// with nobody hired has all three at zero.</item>
    /// <item><c>4098 'Is Maid Blocked?'</c> - <c>StackAttrs[1] == 0</c>;
    /// <c>4100 'Is Gardener Blocked?'</c> - <c>StackAttrs[2] == 0</c>;
    /// <c>4102 'Is Repairman Blocked?'</c> - <c>StackAttrs[3] == 0</c>. Three one-instruction
    /// trees, so those are the exact indices.</item>
    /// <item><c>4097 'Block Maid'</c>, <c>4099 'Block Gardener'</c> and
    /// <c>4103 'Block Repairman'</c> are the three setters, and each writes 5 into the same
    /// attribute its sibling query tests.</item>
    /// <item>And the phone really does use them: <c>PhoneGlobals.iff</c> 8241, 8242 and 8243 -
    /// <c>'gardener available?'</c>, <c>'maid available?'</c>, <c>'repairman available?'</c> -
    /// <c>set_to_next</c> with SearchType 4 (<c>ObjectOfType</c>) on
    /// <see cref="ControllerGuid"/>, which is the same lookup this panel does, and then
    /// <c>run_tree_by_name</c> the trees by the names in <c>Global.iff</c> <c>STR 0x012F</c>:
    /// <c>[61] "Is Gardener Blocked?"</c>, <c>[62] "Is Maid Blocked?"</c>,
    /// <c>[63] "Is Repairman Blocked?"</c>. (The string index is <c>StringID - 1</c> per
    /// <c>VMRunTreeByNameOperand</c>, which is why the shipped operand's 0x3E/0x3F/0x40 land on
    /// 61/62/63.)</item>
    /// </list>
    /// <para>
    /// The panel reads the same attribute the phone's own query reads, and classifies it with the
    /// same predicate that query is: <b>zero means hireable</b>.
    /// </para>
    /// <para>
    /// <b>What is deliberately not read.</b> The rehire tickers in person data 88, 89 and 90. The
    /// engine's enum names them and the runtime array is long enough to hold them, but a census
    /// of every <c>MyPersonData</c> / <c>StackPersonData</c> / <c>NeighborPersonData</c>
    /// expression across all eleven shipped <c>.far</c> cabinets finds 87 distinct slots with a
    /// maximum index of 87 and <b>not one access to 88, 89 or 90</b>. Nothing in The Sims 1 ever
    /// wrote them, so a row built on them is a row that always says "Can be hired" - which is
    /// what this panel used to ship. <see cref="TickerId"/> is the string-table id that used to
    /// caption it; it is kept as a constant and deliberately not called.
    /// </para>
    /// </remarks>
    public static class HouseServices
    {
        /// <summary>Ids 530 to 543 of Simitone's own table.</summary>
        /// <remarks>
        /// <see cref="TickerId"/> is the one id here with no call site. It is 538,
        /// "Next chance in %s day(s)", which used to caption the rehire ticker; the ticker is
        /// gone (see the class remarks) and the shipped string table cannot be edited from here,
        /// so the constant stays and the panel says nothing. A table entry nothing reads is
        /// invisible to a translator, which is exactly why it is named here.
        /// </remarks>
        public const string ChoresTitleId = "530",
            ChoresBodyId = "531",
            HireTitleId = "532",
            HireBodyId = "533",
            MaidNameId = "534",
            GardenerNameId = "535",
            RepairmanNameId = "536",
            AvailableId = "537",
            TickerId = "538",
            ChoresWhyId = "539",
            HiredOnDutyId = "541",
            HiredOffDutyId = "542",
            NoControllerId = "543";

        /// <summary>
        /// <c>Objects/NPCController.iff</c> OBJD 0x41A7. The lot object the phone asks about the
        /// household's services, found with <c>set_to_next</c> SearchType 4.
        /// </summary>
        public const uint ControllerGuid = 0x70F69082;

        /// <summary>
        /// The controller's attribute per service, in <see cref="All"/> order: 1 maid,
        /// 2 gardener, 3 repairman. See the remarks for the three trees that fix the indices.
        /// </summary>
        public static readonly int[] HireFlagAttributes = { 1, 2, 3 };

        /// <summary>
        /// The service NPC each row is talking about, and the GUID the shipped trees search for.
        /// <c>PhoneGlobals.iff</c> 8295 <c>'have Maid?'</c> and 8306
        /// <c>'Interaction - Call Repairman TEST'</c> carry <c>0xC61931DC</c> and
        /// <c>0x565AA54B</c> as <c>set_to_next</c> GUIDs, and 8294 <c>'have Gardener?'</c> carries
        /// <c>0xD62B91B4</c>; those are the OBJD GUIDs of <c>Objects/People/maid.iff</c>,
        /// <c>repairman.iff</c> and <c>gardener.iff</c> respectively.
        /// </summary>
        public static readonly uint[] ServiceNpcGuids = { 0xC61931DC, 0xD62B91B4, 0x565AA54B };

        /// <summary>
        /// The retail tree that reads each flag, as the phone's <c>run_tree_by_name</c> names it
        /// out of <c>Global.iff</c> <c>STR 0x012F</c>. Kept because it is the evidence for the
        /// attribute order and the only place the three flags are named in the shipped data.
        /// </summary>
        public static readonly string[] RetailQueryTreeNames =
        {
            "Is Maid Blocked?",
            "Is Gardener Blocked?",
            "Is Repairman Blocked?",
        };

        /// <summary>English for each service's row caption.</summary>
        public static readonly string[] NameFallback = { "Maid", "Gardener", "Repairman" };

        /// <summary>English for each service's row caption's table id.</summary>
        public static readonly string[] NameId = { MaidNameId, GardenerNameId, RepairmanNameId };

        /// <summary>All three services, in the order the panel lists them.</summary>
        public static readonly HouseholdService[] All =
        {
            HouseholdService.Maid,
            HouseholdService.Gardener,
            HouseholdService.Repairman,
        };

        /// <summary>A service's position in <see cref="All"/>, or -1 for one that is not in it.</summary>
        public static int IndexOf(HouseholdService service)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i] == service) return i;
            }
            return -1;
        }

        /// <summary>A service's row caption.</summary>
        public static string NameOf(HouseholdService service)
        {
            var index = IndexOf(service);
            if (index < 0) return service.ToString();
            return SimitoneStrings.Get(NameId[index], NameFallback[index]);
        }

        /// <summary>
        /// The controller attribute index a service's flag lives in, or -1 for a service that is
        /// not one of the three.
        /// </summary>
        public static int HireFlagAttributeOf(HouseholdService service)
        {
            var index = IndexOf(service);
            if (index < 0) return -1;
            if (index >= HireFlagAttributes.Length) return -1;
            return HireFlagAttributes[index];
        }

        /// <summary>
        /// The whole classification, with no engine in it: retail's own predicate is
        /// <c>attribute == 0</c>, and a zero is what <c>'init tree'</c> writes for a household
        /// that has hired nobody.
        /// </summary>
        /// <param name="hireFlag">
        /// The controller attribute, or <c>null</c> when there is no controller on the lot. Kept
        /// nullable rather than folded to zero because "nobody hired" and "no flag exists" are
        /// different facts and the panel has to be able to say which one it is looking at.
        /// </param>
        /// <param name="npcOnLot">Whether the service's NPC is currently an avatar on this lot.</param>
        public static HouseholdServiceState Classify(int? hireFlag, bool npcOnLot)
        {
            if (hireFlag == null) return HouseholdServiceState.NoController;
            // The phone's own query - 'Is X Blocked?' - is `attribute == 0`, and 'init tree'
            // writes 0 into all three flags, so 0 is the "nobody on the books" state.
            return hireFlag.Value == 0
                ? HouseholdServiceState.NotHired
                : (npcOnLot ? HouseholdServiceState.HiredOnDuty : HouseholdServiceState.HiredOffDuty);
        }

        /// <summary>What a hire row says for a state.</summary>
        public static string StateCaption(HouseholdServiceState state)
        {
            switch (state)
            {
                case HouseholdServiceState.NotHired:
                    return SimitoneStrings.Get(AvailableId, "Can be hired");
                case HouseholdServiceState.HiredOnDuty:
                    return SimitoneStrings.Get(HiredOnDutyId, "Hired - on duty now");
                case HouseholdServiceState.HiredOffDuty:
                    return SimitoneStrings.Get(HiredOffDutyId, "Hired - not on the lot");
                default:
                    return SimitoneStrings.Get(NoControllerId,
                        "Unknown - this lot has no NPC controller to read");
            }
        }

        /// <summary>
        /// The one sentence that replaces "no chore data": what is missing, and the three
        /// independent censuses that establish it.
        /// </summary>
        /// <remarks>
        /// The panel's other chores line is table id 531, which was written before this was
        /// checked and puts the chore list in the lot's data. That is not where it was - a lot
        /// <c>SIMI</c> carries 32 globals and the scripts use 0 to 36 of them, all accounted for -
        /// so the correction is stated separately rather than by editing a string that is already
        /// in every language's table.
        /// </remarks>
        public static string ChoresWhy()
        {
            return SimitoneStrings.Get(ChoresWhyId,
                "There is no chore list to read: retail's scripts carry no chore object, no chore "
                + "tree and no chore string, and every lot global they use is already spoken for. "
                + "The chore list was never saved at all.");
        }

        /// <summary>
        /// The hire flag for one service, read off the lot's NPC controller.
        /// </summary>
        /// <returns>
        /// The attribute, or <c>null</c> when the controller is not on the lot. A <c>null</c> is
        /// deliberately not zero: zero would read as "can be hired", which is a claim about the
        /// household rather than about a missing object.
        /// </returns>
        public static int? ReadHireFlag(VM vm, HouseholdService service)
        {
            var attribute = HireFlagAttributeOf(service);
            if (vm == null || attribute < 0) return null;
            var controller = FindOnLot(vm, ControllerGuid);
            if (controller == null) return null;
            return controller.GetAttribute(attribute);
        }

        /// <summary>
        /// Whether a service's NPC is standing on this lot right now.
        /// </summary>
        /// <remarks>
        /// A separate fact from the hire flag, and one the shipped data also asks about:
        /// <c>PhoneGlobals.iff</c> 8294, 8295 and 8306 <c>set_to_next</c> on
        /// <see cref="ServiceNpcGuids"/> to answer "have we got one?", and 8304-8306
        /// (<c>'Interaction - Call Gardener Test'</c> and its two siblings) refuse to place a call
        /// when the NPC is already on the lot. The same GUIDs, the same lookup.
        /// </remarks>
        public static bool IsNpcOnLot(VM vm, HouseholdService service)
        {
            var index = IndexOf(service);
            if (vm == null || index < 0 || index >= ServiceNpcGuids.Length) return false;
            return FindOnLot(vm, ServiceNpcGuids[index]) != null;
        }

        /// <summary>
        /// The first entity on this lot with the given OBJD GUID, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The same query <c>set_to_next</c> SearchType 4 (<c>ObjectOfType</c>) performs, through
        /// the same <see cref="VMObjectQueries"/> index, so the panel cannot answer a question
        /// the scripts would answer differently. Null-guarded because
        /// <see cref="VMObjectQueries.GetObjectsByGUID"/> returns null - not an empty list - for
        /// a GUID nothing on the lot carries.
        /// </remarks>
        public static VMEntity FindOnLot(VM vm, uint guid)
        {
            var entities = vm?.Context?.ObjectQueries?.GetObjectsByGUID(guid);
            if (entities == null) return null;
            foreach (var entity in entities)
            {
                if (entity != null && !entity.Dead) return entity;
            }
            return null;
        }

        /// <summary>
        /// A whole hire row, so a caller does not have to remember to ask both questions in the
        /// right order. A thin wrapper over <see cref="ReadHireFlag"/>,
        /// <see cref="IsNpcOnLot"/> and <see cref="Classify"/>.
        /// </summary>
        public static HouseholdServiceState StateOf(VM vm, HouseholdService service)
        {
            return Classify(ReadHireFlag(vm, service), IsNpcOnLot(vm, service));
        }
    }
}
