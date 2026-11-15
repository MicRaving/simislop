/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace Simitone.Client.UI.Panels.EODs
{
    /// <summary>
    /// Every EOD panel layout this build ships, and the only place the set of them is written
    /// down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these exist at all.</b> Every panel in <c>FSO.Client.UI.Panels.EODs</c> builds its
    /// interface by calling <c>UIContainer.RenderScript("something.uis")</c>, and
    /// <c>UIScript.Parse</c> opens that name with <c>File.OpenText</c>. The names are retail
    /// The Sims Online layout scripts: they are on disk in a TSO install and in
    /// <c>FSO.Content.TSO</c>, and in neither a TS1 install nor this repository. So with no
    /// file, the panel constructor throws, and <c>UIEODController</c> - correctly - drops the
    /// panel and leaves the plugin talking to a client that never answers. That is why
    /// <c>EODHost</c> reported itself off with "none of the EOD UI layout scripts are present".
    /// </para>
    /// <para>
    /// <b>Why they are not EA's.</b> The retail scripts are not layouts in the sense a window
    /// is a layout; they are a picture of a window, drawn with a couple of hundred
    /// <c>DefineImage</c> nodes whose asset ids point into TSO's <c>UIGraphics.far</c>. Nothing
    /// about the geometry is load-bearing - the panels reposition almost everything themselves
    /// once the plugin sends its first message - and the artwork is exactly the part we may not
    /// ship. So the layouts here are ours: a grid, plain FSO.UI controls, and captions out of
    /// Simitone's own string table. The panels behave identically; they just look plainer.
    /// </para>
    /// <para>
    /// <b>How the element lists were arrived at.</b> By reflection over the panel types
    /// (<c>GetProperties</c> for the ones <c>UIScript.WireUp</c> can reach, which is properties
    /// only, plus the <c>UIImage</c>-typed fields the panels hand to
    /// <c>UIScript.Create</c>), read against the <c>UIScript.Create&lt;T&gt;("id")</c> call sites
    /// in <c>tso.client/UI/Panels/EODs</c>. So the list is "what each panel actually asks a
    /// layout for", not a guess - a control that is missing is a null reference in the
    /// constructor, and a control that is invented is a stray button on screen. The
    /// <see cref="EODControlKind.PanelOwned"/> entries are the ones the panel builds itself:
    /// for those the node must be <c>SetControlProperties</c>, because the panel replaces the
    /// property afterwards and an <c>AddButton</c> would leave a second, unowned button drawn
    /// on top of the first.
    /// </para>
    /// <para>
    /// The <c>.uis</c> files are generated from this by <see cref="EODLayoutBuilder"/> and
    /// committed under <c>Content/UI/uiscripts</c>, so there is no runtime file writing, no
    /// install-time generation and nothing to go stale on a player's machine. Regenerate with
    /// <see cref="EODLayoutWriter"/>; <c>EODLayoutBuilderTests</c> fails if the committed files
    /// and this catalogue disagree.
    /// </para>
    /// </remarks>
    public static class EODLayouts
    {
        /// <summary>Folder, relative to the content directory, the files are written to.</summary>
        public const string Folder = "UI/uiscripts";

        /// <summary>
        /// Every layout this build authors. Hold'em is absent on purpose: it builds its whole
        /// interface in code and never calls RenderScript, so it needs no file and is the one
        /// panel that was reachable before any of this.
        /// </summary>
        public static ReadOnlyCollection<EODLayoutSpec> All { get; private set; }

        static EODLayouts()
        {
            var specs = new List<EODLayoutSpec>
            {            // Sign
            new EODLayoutSpec("signseod.uis", "Sign", new EODLayoutElement[]
            {
                E("FriendReadCheckButton", EODControlKind.Button, "Friends: read"),
                E("FriendWriteCheckButton", EODControlKind.Button, "Friends: write"),
                E("OwnerPermissionsButton", EODControlKind.Button, "Permissions"),
                E("OwnerPermissionsSubpanel", EODControlKind.PanelOwned),
                E("OwnerWriteTextBox", EODControlKind.TextEdit),
                E("OwnerWriteTextButton", EODControlKind.Button, "Write text"),
                E("OwnerWriteTextSubpanel", EODControlKind.PanelOwned),
                E("ReadTextBox", EODControlKind.TextEdit),
                E("ReadTextSubpanel", EODControlKind.PanelOwned),
                E("RoommateReadCheckButton", EODControlKind.Button, "Roommates: read"),
                E("RoommateWriteCheckButton", EODControlKind.Button, "Roommates: write"),
                E("TextRFriends", EODControlKind.Label, "Friends"),
                E("TextRRoommates", EODControlKind.Label, "Roommates"),
                E("TextRVisitors", EODControlKind.Label, "Visitors"),
                E("TextRead", EODControlKind.Label),
                E("TextWFriends", EODControlKind.Label, "Friends"),
                E("TextWRoommates", EODControlKind.Label, "Roommates"),
                E("TextWVisitors", EODControlKind.Label, "Visitors"),
                E("TextWrite", EODControlKind.Label),
                E("VisitorReadCheckButton", EODControlKind.Button, "Visitors: read"),
                E("VisitorWriteCheckButton", EODControlKind.Button, "Visitors: write"),
                E("WriteTextBox", EODControlKind.TextEdit),
                E("WriteTextSubpanel", EODControlKind.PanelOwned)
            }),
            // Dance Floor
            new EODLayoutSpec("danceflooreod.uis", "Dance Floor", new EODLayoutElement[]
            {
                E("Dance001Button", EODControlKind.Button),
                E("Dance002Button", EODControlKind.Button),
                E("Dance003Button", EODControlKind.Button),
                E("Dance004Button", EODControlKind.Button),
                E("Dance005Button", EODControlKind.Button),
                E("Dance006Button", EODControlKind.Button),
                E("Dance007Button", EODControlKind.Button),
                E("Dance008Button", EODControlKind.Button),
                E("Dance009Button", EODControlKind.Button),
                E("Dance010Button", EODControlKind.Button),
                E("Dance011Button", EODControlKind.Button),
                E("Dance012Button", EODControlKind.Button),
                E("Dance013Button", EODControlKind.Button),
                E("Dance014Button", EODControlKind.Button),
                E("Dance015Button", EODControlKind.Button),
                E("Dance016Button", EODControlKind.Button),
                E("Dance017Button", EODControlKind.Button),
                E("Dance018Button", EODControlKind.Button),
                E("Dance019Button", EODControlKind.Button),
                E("Dance020Button", EODControlKind.Button),
                E("Dance021Button", EODControlKind.Button),
                E("Dance022Button", EODControlKind.Button),
                E("Dance023Button", EODControlKind.Button),
                E("Dance024Button", EODControlKind.Button),
                E("DanceToMusicButton", EODControlKind.Button, "Dance to music"),
                E("UIBackground", EODControlKind.PanelOwned)
            }),
            // Pizza Maker
            new EODLayoutSpec("pizzamakereod.uis", "Pizza Maker", new EODLayoutElement[]
            {
                E("UIBackground", EODControlKind.PanelOwned),
                E("background", EODControlKind.PanelOwned),
                E("btnIngredient1", EODControlKind.Button, "Add topping"),
                E("btnIngredient2", EODControlKind.Button, "Add topping"),
                E("btnIngredient3", EODControlKind.Button, "Add topping"),
                E("labelStation1", EODControlKind.Label),
                E("labelStation2", EODControlKind.Label),
                E("labelStation3", EODControlKind.Label),
                E("labelStation4", EODControlKind.Label),
                E("playerPos1", EODControlKind.PanelOwned),
                E("playerPos2", EODControlKind.PanelOwned),
                E("playerPos3", EODControlKind.PanelOwned),
                E("playerPos4", EODControlKind.PanelOwned)
                },
                new[] { "imageAnchovies", "imageCheese", "imageDough", "imageMushrooms", "imagePepperoni", "imageSauce", "imagePlayer" }),

            // Paper Chase
            new EODLayoutSpec("paperchaseeod.uis", "Paper Chase", new EODLayoutElement[]
            {
                E("AButton", EODControlKind.Button, "A"),
                E("BButton", EODControlKind.Button, "B"),
                E("CButton", EODControlKind.Button, "C"),
                E("UIBackground", EODControlKind.PanelOwned),
                E("background", EODControlKind.PanelOwned),
                E("kLetterEntryBody", EODControlKind.Label),
                E("kLetterEntryLogic", EODControlKind.Label),
                E("kLetterEntryMech", EODControlKind.Label),
                E("kLetterPrevBody", EODControlKind.Label),
                E("kLetterPrevLogic", EODControlKind.Label),
                E("kLetterPrevMech", EODControlKind.Label),
                E("kPrevCorrect", EODControlKind.Label),
                E("kPrevText", EODControlKind.Label),
                E("labelStation1", EODControlKind.Label),
                E("labelStation2", EODControlKind.Label),
                E("labelStation3", EODControlKind.Label),
                E("playerPos1", EODControlKind.PanelOwned),
                E("playerPos2", EODControlKind.PanelOwned),
                E("playerPos3", EODControlKind.PanelOwned)
                },
                new[] { "imagePlayer" }),

            // Scoreboard
            new EODLayoutSpec("scoreboardeod.uis", "Scoreboard", new EODLayoutElement[]
            {
                E("ColorBlack", EODControlKind.Button, "Black"),
                E("ColorBlue", EODControlKind.Button, "Blue"),
                E("ColorGreen", EODControlKind.Button, "Green"),
                E("ColorLHS", EODControlKind.Button, "Left score"),
                E("ColorOrange", EODControlKind.Button, "Orange"),
                E("ColorPurple", EODControlKind.Button, "Purple"),
                E("ColorRHS", EODControlKind.Button, "Right score"),
                E("ColorRed", EODControlKind.Button, "Red"),
                E("ColorSelector", EODControlKind.PanelOwned),
                E("ColorWhite", EODControlKind.Button, "White"),
                E("ColorYellow", EODControlKind.Button, "Yellow"),
                E("Loading", EODControlKind.Label),
                E("ScoreTextEntryLHS", EODControlKind.TextEdit),
                E("ScoreTextEntryRHS", EODControlKind.TextEdit),
                E("SpinnerDecreaseLHS", EODControlKind.Button, "-"),
                E("SpinnerDecreaseRHS", EODControlKind.Button, "-"),
                E("SpinnerIncreaseLHS", EODControlKind.Button, "+"),
                E("SpinnerIncreaseRHS", EODControlKind.Button, "+"),
                E("UIBackground", EODControlKind.PanelOwned)
            }),
            // Permission Door
            new EODLayoutSpec("dooreod.uis", "Permission Door", new EODLayoutElement[]
            {
                E("CodeDoorButton", EODControlKind.Button, "Enter code"),
                E("CodeDoorDirections", EODControlKind.Label),
                E("CodeTextEntry", EODControlKind.TextEdit),
                E("DoorFee", EODControlKind.Label),
                E("Employees", EODControlKind.Label, "Employees"),
                E("Employees2", EODControlKind.Label, "Employees"),
                E("Employees2CheckButton", EODControlKind.Button, "Employees"),
                E("EmployeesCheckButton", EODControlKind.Button, "Employees"),
                E("EntryField", EODControlKind.PanelOwned),
                E("Exempt", EODControlKind.Label, "Exempt"),
                E("Friends", EODControlKind.Label, "Friends"),
                E("Friends2", EODControlKind.Label, "Friends"),
                E("Friends2CheckButton", EODControlKind.Button, "Friends"),
                E("FriendsCheckButton", EODControlKind.Button, "Friends"),
                E("Loading", EODControlKind.Label),
                E("LotMembers", EODControlKind.Label, "Lot members"),
                E("LotMembersCheckButton", EODControlKind.Button, "Lot members"),
                E("PayDoorButton", EODControlKind.Button, "Pay to enter"),
                E("PermissionDoorButton", EODControlKind.Button, "Set permissions"),
                E("PermissionDoorDirections", EODControlKind.Label),
                E("SimoleanSymbol", EODControlKind.Label),
                E("SubpanelBackground", EODControlKind.PanelOwned),
                E("Visitors", EODControlKind.Label, "Visitors"),
                E("VisitorsCheckButton", EODControlKind.Button, "Visitors")
            }),
            // Slot Machine
            new EODLayoutSpec("slotseod.uis", "Slot Machine", new EODLayoutElement[]
            {
                E("ArmButton", EODControlKind.Button, "Arm"),
                E("BetIndents", EODControlKind.PanelOwned),
                E("BetText", EODControlKind.TextEdit),
                E("ButtonSeat", EODControlKind.Image),
                E("CashOutButton", EODControlKind.Button, "Cash out"),
                E("CashText", EODControlKind.TextEdit),
                E("Chips", EODControlKind.PanelOwned),
                E("CurrentOdds", EODControlKind.TextEdit),
                E("House", EODControlKind.Label, "House"),
                E("LightsFrame1", EODControlKind.PanelOwned),
                E("LightsFrame2", EODControlKind.PanelOwned),
                E("Loading", EODControlKind.Label),
                E("Odds", EODControlKind.Label, "Odds"),
                E("OddsSlider", EODControlKind.Slider),
                E("OnOff", EODControlKind.Label, "Power"),
                E("OnOffButton", EODControlKind.Button, "Power"),
                E("PayoutTable1", EODControlKind.Label),
                E("PayoutTable2", EODControlKind.Label),
                E("PayoutTable3", EODControlKind.Label),
                E("PayoutTableColumn1Row1", EODControlKind.PanelOwned),
                E("PayoutTableColumn1Row2", EODControlKind.PanelOwned),
                E("PayoutTableColumn1Row3", EODControlKind.PanelOwned),
                E("PayoutTableColumn1Row4", EODControlKind.PanelOwned),
                E("PayoutTableColumn2Row1", EODControlKind.PanelOwned),
                E("PayoutTableColumn2Row2", EODControlKind.PanelOwned),
                E("PayoutTableColumn2Row3", EODControlKind.PanelOwned),
                E("PayoutTableColumn2Row4", EODControlKind.PanelOwned),
                E("PayoutText1", EODControlKind.TextEdit),
                E("PayoutText2", EODControlKind.TextEdit),
                E("PayoutText3", EODControlKind.TextEdit),
                E("PayoutText4", EODControlKind.TextEdit),
                E("PayoutText5", EODControlKind.TextEdit),
                E("PayoutText6", EODControlKind.TextEdit),
                E("PayoutText7", EODControlKind.TextEdit),
                E("PayoutText8", EODControlKind.TextEdit),
                E("Player", EODControlKind.Label, "Player"),
                E("SpinButton", EODControlKind.Button, "Spin"),
                E("SpinnerDecreaseBet", EODControlKind.Button, "Less bet"),
                E("SpinnerIncreaseBet", EODControlKind.Button, "More bet"),
                E("TextBack", EODControlKind.Image),
                E("WheelsBack", EODControlKind.PanelOwned),
                E("Wheelsback", EODControlKind.Image),
                E("WinningLine", EODControlKind.PanelOwned)
                },
                new[] { "Wheel1Image", "Wheel2Image", "Wheel3Image", "MoneyChipsImage", "LightsFrame1Image", "LightsFrame2Image", "Wheel1LegendImage", "Wheel2LegendImage", "Wheel3LegendImage" }),

            // Trunk
            new EODLayoutSpec("trunkeod.uis", "Trunk", new EODLayoutElement[]
            {
                E("AcceptButton", EODControlKind.Button, "OK"),
                E("BodySkinBrowser", EODControlKind.PanelOwned),
                E("CostumeOptions", EODControlKind.PanelOwned),
                E("LargeThumbnail", EODControlKind.PanelOwned),
                E("SubpanelBackground", EODControlKind.PanelOwned)
            }),
            // War Game
            new EODLayoutSpec("wargameeod.uis", "War Game", new EODLayoutElement[]
            {
                E("ArtilleryButton", EODControlKind.PanelOwned, "Artillery"),
                E("BlueMoveBorder", EODControlKind.PanelOwned),
                E("BluePlayerPos", EODControlKind.PanelOwned),
                E("CavalryButton", EODControlKind.PanelOwned, "Cavalry"),
                E("CommandButton", EODControlKind.PanelOwned, "Command"),
                E("DefeatArtilleryPos", EODControlKind.PanelOwned),
                E("DefeatCavalryPos", EODControlKind.PanelOwned),
                E("DefeatCommandPos", EODControlKind.PanelOwned),
                E("DefeatInfantryPos", EODControlKind.PanelOwned),
                E("DefeatIntelPos", EODControlKind.PanelOwned),
                E("InfantryButton", EODControlKind.PanelOwned, "Infantry"),
                E("IntelButton", EODControlKind.PanelOwned, "Intel"),
                E("MoveSelectionBkgnd", EODControlKind.PanelOwned),
                E("ProgPosition1", EODControlKind.PanelOwned),
                E("ProgPosition2", EODControlKind.PanelOwned),
                E("ProgPosition3", EODControlKind.PanelOwned),
                E("ProgPosition4", EODControlKind.PanelOwned),
                E("ProgPosition5", EODControlKind.PanelOwned),
                E("ProgPosition6", EODControlKind.PanelOwned),
                E("RedMoveBorder", EODControlKind.PanelOwned),
                E("RedPlayerPos", EODControlKind.PanelOwned),
                E("ResultDefeatImg", EODControlKind.PanelOwned),
                E("ResultVictoryImg", EODControlKind.PanelOwned),
                E("ResultsBkgnd", EODControlKind.PanelOwned)
                },
                new[] { "MoveIconImage", "BlueProgressImage", "RedProgressImage" }),

            // Timer
            new EODLayoutSpec("timereod.uis", "Timer", new EODLayoutElement[]
            {
                E("ColonStaticText", EODControlKind.Label),
                E("CountdownButton", EODControlKind.Button, "Countdown"),
                E("MinDownButton", EODControlKind.Button, "Minute down"),
                E("MinTextEntry", EODControlKind.TextEdit),
                E("MinUpButton", EODControlKind.Button, "Minute up"),
                E("PauseButton", EODControlKind.Button, "Pause"),
                E("PlayButton", EODControlKind.Button, "Play"),
                E("SecDownButton", EODControlKind.Button, "Second down"),
                E("SecTextEntry", EODControlKind.TextEdit),
                E("SecUpButton", EODControlKind.Button, "Second up"),
                E("StopwatchButton", EODControlKind.Button, "Stopwatch"),
                E("SubpanelBackground", EODControlKind.PanelOwned)
            }),
            // Game Comp: Draw A Card
            new EODLayoutSpec("gamecompdrawacardeod.uis", "Game Comp: Draw A Card", new EODLayoutElement[]
            {
                E("BetterLabel", EODControlKind.TextEdit),
                E("DeleteBtn", EODControlKind.Button, "Delete"),
                E("DrawChancesText", EODControlKind.Label),
                E("EditCardBack", EODControlKind.PanelOwned),
                E("EditCardBtn", EODControlKind.Button, "Edit card"),
                E("EditCardNextBtn", EODControlKind.Button, "Next"),
                E("EditCardPrevBtn", EODControlKind.Button, "Previous"),
                E("EditCardTextEdit", EODControlKind.TextEdit),
                E("EditGameBtn", EODControlKind.Button, "Edit game"),
                E("ManageGameBack", EODControlKind.PanelOwned),
                E("ManageGameDescripTextEdit", EODControlKind.TextEdit),
                E("ManageGameTitleTextEdit", EODControlKind.TextEdit),
                E("NewCardBack", EODControlKind.PanelOwned),
                E("NewCardBtn", EODControlKind.Button, "New card"),
                E("NewCardTextEdit", EODControlKind.TextEdit),
                E("NumCardsText", EODControlKind.Label),
                E("NumCardsTextEdit", EODControlKind.TextEdit),
                E("NumDrawChancesTextEdit", EODControlKind.TextEdit),
                E("SaveBtn", EODControlKind.Button, "Save"),
                E("SelectCardBack", EODControlKind.PanelOwned),
                E("SelectCardList", EODControlKind.ListBox),
                E("SelectCardText", EODControlKind.Label),
                E("SpinnerMinusBtn", EODControlKind.Button, "One fewer card"),
                E("SpinnerPlusBtn", EODControlKind.Button, "One more card"),
                E("UniqueCardsText", EODControlKind.Label),
                E("UniqueCardsTextEdit", EODControlKind.TextEdit),
                E("ViewDrawInfoBack", EODControlKind.PanelOwned),
                E("ViewDrawInfoTextEdit", EODControlKind.TextEdit)
                },
                new[] { "ImagePanelDivider" }),

            // Band
            new EODLayoutSpec("jobobjband.uis", "Band", new EODLayoutElement[]
            {
                E("BUZZ", EODControlKind.PanelOwned, "Buzz"),
                E("ButtonBack", EODControlKind.PanelOwned),
                E("CASHOUT", EODControlKind.PanelOwned, "Sell out"),
                E("CONTINUE", EODControlKind.PanelOwned, "Rock on"),
                E("DOH", EODControlKind.PanelOwned, "Do"),
                E("DOH2", EODControlKind.PanelOwned, "Do (high)"),
                E("FA", EODControlKind.PanelOwned, "Fa"),
                E("LA", EODControlKind.PanelOwned, "La"),
                E("MI", EODControlKind.PanelOwned, "Mi"),
                E("Player1", EODControlKind.PanelOwned),
                E("Player1Wait", EODControlKind.Label),
                E("Player2", EODControlKind.PanelOwned),
                E("Player2Wait", EODControlKind.Label),
                E("Player3", EODControlKind.PanelOwned),
                E("Player3Wait", EODControlKind.Label),
                E("Player4", EODControlKind.PanelOwned),
                E("Player4Wait", EODControlKind.Label),
                E("Player5", EODControlKind.PanelOwned),
                E("Player6", EODControlKind.PanelOwned),
                E("Player7", EODControlKind.PanelOwned),
                E("Player8", EODControlKind.PanelOwned),
                E("RE", EODControlKind.PanelOwned, "Re"),
                E("SO", EODControlKind.PanelOwned, "So"),
                E("TI", EODControlKind.PanelOwned, "Ti"),
                E("TextMessage", EODControlKind.Label),
                E("WaitPlayer1", EODControlKind.PanelOwned),
                E("WaitPlayer2", EODControlKind.PanelOwned),
                E("WaitPlayer3", EODControlKind.PanelOwned),
                E("WaitPlayer4", EODControlKind.PanelOwned)
                },
                new[] { "PlayerImage", "ContinueButtonImage", "CashOutButtonImage" }),

            // Roulette
            new EODLayoutSpec("rouletteeod.uis", "Roulette", new EODLayoutElement[]
            {
                E("btnChip1", EODControlKind.PanelOwned),
                E("btnChip2", EODControlKind.PanelOwned),
                E("btnChip3", EODControlKind.PanelOwned),
                E("btnChip4", EODControlKind.PanelOwned),
                E("btnChip5", EODControlKind.PanelOwned),
                E("btnShowAllBets", EODControlKind.PanelOwned, "Show all bets"),
                E("labelNumber", EODControlKind.Label, "Number"),
                E("labelTotalBets", EODControlKind.Label, "Total bets"),
                E("rouletteBall", EODControlKind.PanelOwned),
                E("rouletteTable", EODControlKind.PanelOwned),
                E("rouletteWheel", EODControlKind.PanelOwned)
                },
                new[] { "imageChip_001", "imageChip_005", "imageChip_010", "imageChip_025", "imageChip_100", "imageChip_001_Btn", "imageChip_005_Btn", "imageChip_010_Btn", "imageChip_025_Btn", "imageChip_100_Btn" }),

            // Blackjack
            new EODLayoutSpec("blackjackeod.uis", "Blackjack", new EODLayoutElement[]
            {
                E("TotalBetEntry", EODControlKind.TextEdit),
                E("betEntryBox", EODControlKind.PanelOwned),
                E("btnBet", EODControlKind.PanelOwned, "Bet"),
                E("btnBetDown", EODControlKind.Button, "Less bet"),
                E("btnBetUp", EODControlKind.Button, "More bet"),
                E("btnChip1", EODControlKind.PanelOwned),
                E("btnChip2", EODControlKind.PanelOwned),
                E("btnChip3", EODControlKind.PanelOwned),
                E("btnChip4", EODControlKind.PanelOwned),
                E("btnChip5", EODControlKind.PanelOwned),
                E("btnDouble", EODControlKind.PanelOwned, "Double"),
                E("btnHit", EODControlKind.PanelOwned, "Hit"),
                E("btnSplit", EODControlKind.PanelOwned, "Split"),
                E("btnStand", EODControlKind.PanelOwned, "Stand"),
                E("dealerPos_Cards", EODControlKind.PanelOwned),
                E("dealerPos_Head", EODControlKind.PanelOwned),
                E("labelTotalBet", EODControlKind.Label),
                E("offset_ButtonSeat", EODControlKind.PanelOwned),
                E("offset_HandTotal", EODControlKind.PanelOwned),
                E("offset_PlayerSeat", EODControlKind.PanelOwned),
                E("playerOffset_Bet", EODControlKind.PanelOwned),
                E("playerOffset_Cards", EODControlKind.PanelOwned),
                E("playerOffset_Head", EODControlKind.PanelOwned),
                E("playerPos1", EODControlKind.PanelOwned),
                E("playerPos2", EODControlKind.PanelOwned),
                E("playerPos3", EODControlKind.PanelOwned),
                E("playerPos4", EODControlKind.PanelOwned)
                },
                new[] { "imagePlayerBack", "imagePlayerBox", "imagePlayerBoxEdit", "imageBtnSeat", "image_BetAmount" }),

            // Job Object Maze (charisma)
            new EODLayoutSpec("twopersonjobobjectmazecharisma.uis", "Job Object Maze (charisma)", new EODLayoutElement[]
            {
                E("BlueIcon", EODControlKind.PanelOwned),
                E("East", EODControlKind.Button, "East"),
                E("GreenIcon", EODControlKind.PanelOwned),
                E("HWall", EODControlKind.PanelOwned),
                E("North", EODControlKind.Button, "North"),
                E("PayoutCaption", EODControlKind.Label),
                E("PayoutField", EODControlKind.Label),
                E("RedIcon", EODControlKind.PanelOwned),
                E("South", EODControlKind.Button, "South"),
                E("UIPlayBackground", EODControlKind.PanelOwned),
                E("UIWaitBackground", EODControlKind.PanelOwned),
                E("VWall", EODControlKind.PanelOwned),
                E("Waiting", EODControlKind.Label),
                E("West", EODControlKind.Button, "West")
            }),
            // Job Object Maze (logic)
            new EODLayoutSpec("twopersonjobobjectmazelogic.uis", "Job Object Maze (logic)", new EODLayoutElement[]
            {
                E("BlueIcon", EODControlKind.PanelOwned),
                E("East", EODControlKind.Button, "East"),
                E("GreenIcon", EODControlKind.PanelOwned),
                E("HWall", EODControlKind.PanelOwned),
                E("North", EODControlKind.Button, "North"),
                E("PayoutCaption", EODControlKind.Label),
                E("PayoutField", EODControlKind.Label),
                E("RedIcon", EODControlKind.PanelOwned),
                E("South", EODControlKind.Button, "South"),
                E("UIPlayBackground", EODControlKind.PanelOwned),
                E("UIWaitBackground", EODControlKind.PanelOwned),
                E("VWall", EODControlKind.PanelOwned),
                E("Waiting", EODControlKind.Label),
                E("West", EODControlKind.Button, "West")
            }),
            // Neon Downtown Dance Floor
            new EODLayoutSpec("danceplatformeod.uis", "Neon Downtown Dance Floor", new EODLayoutElement[]
            {
                E("Dance001Button", EODControlKind.Button),
                E("Dance002Button", EODControlKind.Button),
                E("Dance003Button", EODControlKind.Button),
                E("Dance004Button", EODControlKind.Button),
                E("Dance005Button", EODControlKind.Button),
                E("Dance006Button", EODControlKind.Button),
                E("Dance007Button", EODControlKind.Button),
                E("Dance008Button", EODControlKind.Button),
                E("Dance009Button", EODControlKind.Button),
                E("Dance010Button", EODControlKind.Button),
                E("Dance011Button", EODControlKind.Button),
                E("Dance012Button", EODControlKind.Button),
                E("Dance013Button", EODControlKind.Button),
                E("Dance014Button", EODControlKind.Button),
                E("Dance015Button", EODControlKind.Button),
                E("Dance016Button", EODControlKind.Button),
                E("Dance017Button", EODControlKind.Button),
                E("Dance018Button", EODControlKind.Button),
                E("Dance019Button", EODControlKind.Button),
                E("Dance020Button", EODControlKind.Button),
                E("Dance021Button", EODControlKind.Button),
                E("Dance022Button", EODControlKind.Button),
                E("Dance023Button", EODControlKind.Button),
                E("Dance024Button", EODControlKind.Button),
                E("TeamMedallion", EODControlKind.PanelOwned),
                E("UIBackground", EODControlKind.PanelOwned)
                },
                new[] { "DancerRedImage", "DancerBlueImage", "DancerGreenImage", "DancerYellowImage" }),

            // DJ Station
            new EODLayoutSpec("djstationeod.uis", "DJ Station", new EODLayoutElement[]
            {
                E("BassLabel", EODControlKind.Image),
                E("BassPanel", EODControlKind.Image),
                E("BassSample", EODControlKind.Label),
                E("BassText", EODControlKind.Image),
                E("DrumsLabel", EODControlKind.Image),
                E("DrumsPanel", EODControlKind.Image),
                E("DrumsSample", EODControlKind.Label),
                E("DrumsText", EODControlKind.Image),
                E("SynthLabel", EODControlKind.Image),
                E("SynthPanel", EODControlKind.Image),
                E("SynthSample", EODControlKind.Label),
                E("SynthText", EODControlKind.Image),
                E("TeamMedallion", EODControlKind.PanelOwned),
                E("VoxLabel", EODControlKind.Image),
                E("VoxPanel", EODControlKind.Image),
                E("VoxSample", EODControlKind.Label),
                E("VoxText", EODControlKind.Image)
                },
                new[] { "DJRedImage", "DJBlueImage", "DJGreenImage", "DJYellowImage" }),
            };
            //Reserve the captions across every layout at once, so a string's id cannot depend
            //on which layout happened to be built first. After this the ids are fixed and the
            //generated .cst block is reproducible.
            EODLayoutStrings.Reserve(specs);

            All = new ReadOnlyCollection<EODLayoutSpec>(specs);
        }

        private static EODLayoutElement E(string id, EODControlKind kind, string caption = null)
        {
            return new EODLayoutElement(id, kind, caption);
        }

        /// <summary>
        /// The spec for one layout file, or null when this build does not author it. Matched
        /// case-insensitively because <c>UIScript</c> stores string keys lower case and a layout
        /// asked for as "SlotsEOD.uis" must find the same file.
        /// </summary>
        public static EODLayoutSpec For(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            foreach (var spec in All)
            {
                if (string.Equals(spec.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return spec;
            }
            return null;
        }

        /// <summary>True when this build authors a layout by that name.</summary>
        public static bool IsAuthored(string fileName)
        {
            return For(fileName) != null;
        }
    }

    /// <summary>
    /// Writes the generated half of the EOD layout content out: the <c>.uis</c> files and the
    /// <c>.cst</c> block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An authoring tool, not part of the game. It is here, and public, because the files it
    /// produces are committed and somebody has to be able to regenerate them - and because
    /// "run this and diff" is the most honest way to review a change to a layout.
    /// </para>
    /// <para>
    /// It is never called by the game. The game's only relationship with these files is that
    /// <c>UIContainer.RenderScript</c> opens them by name out of the content directory, which
    /// is the first root <c>EODPluginPolicy.LayoutSearchPaths</c> probes, so the same search
    /// that decides whether a layout "exists" is the one the panel reads from.
    /// </para>
    /// </remarks>
    public static class EODLayoutWriter
    {
        /// <summary>Writes every <c>.uis</c> into <paramref name="contentDir"/>, and reports what it wrote.</summary>
        public static IReadOnlyList<string> WriteLayouts(string contentDir)
        {
            if (string.IsNullOrWhiteSpace(contentDir)) throw new ArgumentException("no content directory", nameof(contentDir));
            var dir = Path.Combine(contentDir, "UI", "uiscripts");
            Directory.CreateDirectory(dir);
            var written = new List<string>();
            foreach (var spec in EODLayouts.All)
            {
                var path = Path.Combine(dir, spec.FileName);
                File.WriteAllText(path, EODLayoutBuilder.Build(spec));
                written.Add(path);
            }
            return written;
        }

        /// <summary>
        /// The generated <c>.cst</c> lines, so a maintainer can paste them into
        /// <c>_f900_simitone.cst</c>. A test compares the shipped file against this.
        /// </summary>
        public static IEnumerable<string> CstLines()
        {
            return EODLayoutStrings.CstLines();
        }
    }
}