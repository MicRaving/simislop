using FSO.Client.UI.Framework;
using FSO.Content;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Model;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels.LotControls
{
    public class UICallNeighborAlert : UIMobileDialog
    {
        public UICallNeighborPanel NPanel;
        public short SelectedNeighbour
        {
            get
            {
                return NPanel.SelectedNeighbour;
            }
        }
        
        public event Action<int> OnResult;

        /// <summary>
        /// The four words this dialog is made of, as ids 130 to 133 of Simitone's own table.
        /// </summary>
        /// <remarks>
        /// TS1 never had a phone book, so nothing here has a game string behind it; the English
        /// is the second argument of every lookup, which is what a build with no table shows.
        /// </remarks>
        internal const string TitleId = "130", CallCaptionId = "131", CancelCaptionId = "132", UnknownId = "133";

        public UICallNeighborAlert(short callerNID, VM vm)
        {
            Caption = SimitoneStrings.Get(TitleId, "Call Neighbour");
            SetHeight(490);
            NPanel = new UICallNeighborPanel(callerNID, vm);
            UpdatePlacement();
            NPanel.OnResult += (res) => { OnResult?.Invoke(res); Close(); };
            Add(NPanel);
        }

        /// <summary>
        /// Centres the panel on the dialog. Pulled out of the constructor so
        /// <see cref="GameResized"/> can re-run it: the panel is a fixed 1030px wide, so on a
        /// resized window the old position was left hugging the left edge.
        /// </summary>
        private void UpdatePlacement()
        {
            NPanel.Position = new Microsoft.Xna.Framework.Vector2((Width - 1030) / 2, 110);
        }

        public override void GameResized()
        {
            base.GameResized();
            UpdatePlacement();
        }
    }

    public class UICallNeighborPanel : UIContainer
    {
        public Dictionary<short, List<short>> NeighborsByFamilyID = new Dictionary<short, List<short>>();
        public UITouchStringList FamilyList;
        public UITouchStringList NeighbourList;
        public UIAvatarSelectButton Icon;
        public UIBigButton CallButton, CancelButton;
        public int SelectedFamily = -1;
        public short SelectedNeighbour = -1;
        public VM VM;

        public event Action<int> OnResult;

        public UICallNeighborPanel(short callerNID, VM vm)
        {
            VM = vm;
            var nb = Content.Get().Neighborhood;
            var neigh = nb.GetNeighborByID(callerNID);
            var rels = neigh.Relationships.Keys;
            //var rels = nb.Neighbors.NeighbourByID.Keys;

            foreach (var to in rels)
            {
                var tn = nb.GetNeighborByID((short)to);
                var family = tn.PersonData?.ElementAt((int)VMPersonDataVariable.TS1FamilyNumber) ?? 0;
                var gender = tn.PersonData?.ElementAt((int)VMPersonDataVariable.Gender) ?? 0; //can't call pets
                if (family != 0 && gender < 2)
                {
                    List<short> famList = null;
                    if (!NeighborsByFamilyID.TryGetValue(family, out famList))
                    {
                        famList = new List<short>();
                        NeighborsByFamilyID[family] = famList;
                    }

                    famList.Add((short)to);
                }
            }

            FamilyList = new UITouchStringList();
            FamilyList.Size = new Microsoft.Xna.Framework.Vector2(320, 350);
            FamilyList.BackingList = NeighborsByFamilyID.Select(x => nb.GetFamilyString((ushort)x.Key).GetString(0)).ToList();
            FamilyList.Refresh();
            FamilyList.OnSelectionChange += FamilyList_OnSelectionChange;
            Add(FamilyList);

            NeighbourList = new UITouchStringList();
            NeighbourList.Size = new Microsoft.Xna.Framework.Vector2(320, 350);
            NeighbourList.OnSelectionChange += NeighbourList_OnSelectionChange;
            Add(NeighbourList);

            var cancelButton = CancelButton = new UIBigButton(false);
            cancelButton.Caption = SimitoneStrings.Get(UICallNeighborAlert.CancelCaptionId, "Cancel");
            cancelButton.OnButtonClick += (btn) => { OnResult?.Invoke(-1); };
            cancelButton.Width = 275;
            Add(cancelButton);

            CallButton = new UIBigButton(true);
            CallButton.Caption = SimitoneStrings.Get(UICallNeighborAlert.CallCaptionId, "Call");
            CallButton.OnButtonClick += (btn) => { OnResult?.Invoke(SelectedNeighbour); };
            CallButton.Width = 275;
            Add(CallButton);

            PlaceControls();

            NeighbourList_OnSelectionChange((NeighborsByFamilyID.Count==0)?-2:-1);

            OnResult += (res) => { CallButton.Disabled = true; CancelButton.Disabled = true; };
        }

        private void NeighbourList_OnSelectionChange(int obj)
        {
            if (Icon != null) { Remove(Icon); Icon = null; }
            if (obj < 0)
            {
                //-1 is "nothing picked in the neighbour list", -2 is the initial "this family has
                //no neighbours you can call" state that the constructor passes in. Both mean
                //there is nothing to call, so both leave Call greyed out. Anything else is an
                //index into the selected family's list, and the previous shape of this (an
                //obj == -1 test with an else branch) indexed the list for -2, which threw.
                SelectedNeighbour = -1;
                CallButton.Disabled = true;
                return;
            }

            //show the picked neighbour's portrait
            SelectedNeighbour = NeighborsByFamilyID.ElementAt(SelectedFamily).Value[obj];
            CallButton.Disabled = false;

            var guid = Content.Get().Neighborhood.GetNeighborByID(SelectedNeighbour).GUID;
            var temp = VM.Context.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, Direction.NORTH, true);
            Icon = new UIAvatarSelectButton(UIIconCache.GetObject(temp.BaseObject));
            Icon.Position = new Microsoft.Xna.Framework.Vector2(892, 60);
            Add(Icon);
            temp.Delete(VM.Context);
        }

        private void FamilyList_OnSelectionChange(int obj)
        {
            var nb = Content.Get().Neighborhood;
            //populate the rightmost list with the selected family
            NeighbourList.BackingList.Clear();
            if (obj != -1)
            {
                var people = NeighborsByFamilyID.ElementAt(obj).Value;
                NeighbourList.BackingList =
                    people.Select(x => {
                        var guid = nb.GetNeighborByID(x).GUID;
                        var gobj = Content.Get().WorldObjects.Get(guid);
                        if (gobj == null) return SimitoneStrings.Get(UICallNeighborAlert.UnknownId, "Unknown");
                        return gobj.Resource.Get<FSO.Files.Formats.IFF.Chunks.CTSS>(gobj.OBJ.CatalogStringsID)?.GetString(0)
                            ?? SimitoneStrings.Get(UICallNeighborAlert.UnknownId, "Unknown");
                        }
                    ).ToList();
            }
            NeighbourList.Refresh();
            NeighbourList_OnSelectionChange(-1);
            SelectedFamily = obj;
        }

        /// <summary>
        /// Places the three right-hand column controls. Positions are absolute, so they are
        /// re-applied on a window resize rather than being set once in the constructor.
        /// </summary>
        private void PlaceControls()
        {
            CallButton.Position = new Microsoft.Xna.Framework.Vector2(370 + 385, 255);
            CancelButton.Position = new Microsoft.Xna.Framework.Vector2(370 + 385, 135);
            NeighbourList.Position = new Microsoft.Xna.Framework.Vector2(370, 0);
        }

        public override void GameResized()
        {
            base.GameResized();
            PlaceControls();
        }
    }
}
