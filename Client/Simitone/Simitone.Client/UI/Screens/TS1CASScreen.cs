using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Common.Utils;
using FSO.SimAntics;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.NetPlay;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.NetPlay.Model;
using Simitone.Client.UI.Panels.WorldUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Rendering.Framework.Camera;
using Microsoft.Xna.Framework;
using FSO.LotView.RC;
using FSO.LotView.Utils;
using FSO.LotView.Model;
using FSO.Content;
using FSO.Vitaboy;
using FSO.Content.TS1;
using Simitone.Client.UI.Panels.CAS;
using Simitone.Client.UI.Controls;
using FSO.SimAntics.Model;
using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.UI.Panels;
using FSO.Client.UI.Controls;
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using FSO.SimAntics.Utils;
using FSO.SimAntics.Model.TS1Platform;
using FSO.LotView;

namespace Simitone.Client.UI.Screens
{
    public class TS1CASScreen : UIScreen
    {
        #region CAS confirmation strings

        /// <summary>
        /// TS1's family-management string table, which carries the localised "delete this
        /// family" confirmation.
        /// </summary>
        private const string CasFamilyTable = "128";

        /// <summary>TS1's family editor table, which carries the save/discard confirmations.</summary>
        private const string CasEditorTable = "129";

        /// <summary>Title of the delete confirmation, in <see cref="CasFamilyTable"/>.</summary>
        private const string CasDeleteTitleId = "6";

        /// <summary>Body of the delete confirmation, in <see cref="CasFamilyTable"/>. Takes the
        /// family name as its one %s.</summary>
        private const string CasDeleteMessageId = "7";

        /// <summary>Title of the save confirmation, in <see cref="CasEditorTable"/>.</summary>
        private const string CasSaveTitleId = "13";

        /// <summary>Body of the save confirmation, in <see cref="CasEditorTable"/>.</summary>
        private const string CasSaveMessageId = "14";

        /// <summary>Title of the discard confirmation, in <see cref="CasEditorTable"/>.</summary>
        private const string CasDiscardTitleId = "7";

        /// <summary>Body of the discard confirmation, in <see cref="CasEditorTable"/>.</summary>
        private const string CasDiscardMessageId = "8";

        #endregion

        private FSO.LotView.World World;
        public FSO.SimAntics.VM vm { get; set; }
        public VMNetDriver Driver;
        public BasicCamera Cam;
        public bool Initialized;
        public VMAvatar[] HeadAvatars;
        public VMAvatar[] BodyAvatars;
        public List<string> ActiveHeads;
        public List<string> ActiveBodies;
        public List<string> ActiveHeadTex;
        public List<string> ActiveHandgroupTex;
        public List<string> ActiveBodyTex;

        public static int NeighTypeFrom = 4;
        private bool Dead;

        private float _FamilySimInterp = -2;
        public float FamilySimInterp
        {
            set
            {
                CameraInterp(value);
                CASPanel.Position = new Vector2((Cam == null)?10:((ScreenWidth - 500) / 2), 10 - (282 * (1-value)));
                FamilyPanel.ShowI = 1-Math.Abs(value);
                FamiliesPanel.TitleI = 1 - Math.Abs(value+1);

                _FamilySimInterp = value;
            }
            get
            {
                return _FamilySimInterp;
            }
        }

        public float HeadPosition = -9f;
        public float BodyPosition = -9f;

        public float HeadSpeed = 0f;
        public float BodySpeed = 0f;
        public float XLast = -1f;

        public int HeadPositionLast = 0;
        public int BodyPositionLast = 0;

        public string CurrentCode = "ma";
        public string CurrentSkin = "lgt";
        private bool CurrentChild;

        private int? MoveInFamily;

        private UICASMode Mode = UICASMode.FamilyEdit;

        /// <summary>
        /// The FAMI this screen is editing in place, or null when a brand new family is being
        /// created. When non-null, Accept commits through <see cref="SaveFamilyInPlace"/> rather
        /// than <see cref="SaveFamily"/>, so existing character IFFs are rewritten instead of a
        /// second copy of the family being created.
        /// </summary>
        public FAMI EditingFamily;

        /// <summary>
        /// GUIDs of members the player deleted during the current edit session. They are only
        /// removed from <c>FAMI.FamilyGUIDs</c> when the family is saved, so backing out of the
        /// editor (which calls <see cref="ClearFamily"/>) discards them.
        /// </summary>
        private readonly HashSet<uint> PendingRemovals = new HashSet<uint>();

        /// <summary>
        /// FAMI ChunkID handed in by <see cref="GameController.EnterCAS(int?)"/>. It cannot be
        /// loaded in the constructor because decoding a family needs the CAS VM (to spawn the
        /// preview avatars), which does not exist until <see cref="InitializeLot"/> runs on the
        /// first Update.
        /// </summary>
        private int? PendingEditFamilyId;

        public List<CASFamilyMember> WIPFamily = new List<CASFamilyMember>();
        public List<VMAvatar> RepresentFamily = new List<VMAvatar>();

        public UISimCASPanel CASPanel;
        public UIFamilyCASPanel FamilyPanel;
        public UIFamiliesCASPanel FamiliesPanel;
        public UITwoStateButton BackButton;
        public UITwoStateButton AcceptButton;

        public Vector3[] ModePositions = new Vector3[]
        {
            new Vector3(177.3843f, 150.92333f, 3.25105f),
            new Vector3(157.3843f, 28.92333f, 23.25105f),
            new Vector3(119.5611f, 6.122346f, 104.0364f),
            new Vector3(114.0793f, 10f, 64.67827f)
        };

        public Vector3[] ModeTargets = new Vector3[]
        {
            new Vector3(177.3843f-7f, 130.92333f, 3.25105f+5f),
            new Vector3(150.3057f, 26.01005f, 29.6858f),
            new Vector3(111.7678f, 3.936443f, 98.164f),
            new Vector3(104.5736f, 6.896059f, 64.59684f)
        };

        public Vector3[] Mode2D = new Vector3[]
        {
            new Vector3(104, 0, 57),
            new Vector3(104, 0, 57),
            new Vector3(103.5611f, 0, 92.0364f),
            new Vector3(84.0793f+6, 0, 64f-6)
        };

        public Vector3[] SinTransitions = new Vector3[]
        {
            new Vector3(0, 0, 0),
            new Vector3(12f, 0, 0),
            new Vector3(-6f, 0, 0),
            new Vector3()
        };

        public void CameraInterp(float value)
        {
            if (value < -2) value = -2;
            var prev = (int)(value + 2);
            var next = (int)(Math.Ceiling(value) + 2);
            if (next > 3) next = 3;

            if (Cam == null)
            {
                //2d
                var pos1 = Mode2D[prev];
                var pos2 = Mode2D[next];

                if (World != null) World.State.PreciseZoom = 1 + Math.Min(0, value * 0.5f);
                value = (float)DirectionUtils.PosMod(value, 1.0);
                var campos = Vector3.Lerp(pos1, pos2, value);
                campos += (float)Math.Sin(value * Math.PI) * SinTransitions[prev];



                if (World != null)
                    World.State.CenterTile = new Vector2(campos.X, campos.Z) / 3f;
            }
            else
            {

                var pos1 = ModePositions[prev];
                var pos2 = ModePositions[next];
                var targ1 = ModeTargets[prev] - pos1;
                var targ2 = ModeTargets[next] - pos2;

                value = (float)DirectionUtils.PosMod(value, 1.0);
                var camvec = Vector3.Lerp(targ1, targ2, value);
                var campos = Vector3.Lerp(pos1, pos2, value);

                campos += (float)Math.Sin(value * Math.PI) * SinTransitions[prev];

                Cam.Position = campos;
                Cam.Target = campos + camvec;
            }
        }

        private string MissingFallback(string x, IEnumerable<string> texnames)
        {
            return "x";
        }

        private void PopulateSimType(string simtype)
        {
            CurrentCode = simtype;
            var heads = Content.Get().BCFGlobal.CollectionsByName["c"].ClothesByAvatarType[simtype];
            if (simtype[1] == 'c') simtype += "chd";
            var bodies = Content.Get().BCFGlobal.CollectionsByName["b"].GeneralAvatarType(simtype);

            var tex = (TS1AvatarTextureProvider)Content.Get().AvatarTextures;
            var texnames = tex.GetAllNames();
            ActiveHeads = heads;
            ActiveBodies = bodies;
            
            ActiveHeadTex = heads.Select(x => RemoveExt(texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, CurrentSkin))))).ToList();
            ActiveBodyTex = bodies.Select(x => RemoveExt(
                texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, CurrentSkin)))
                ?? texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, ""))) ?? MissingFallback(x, texnames)
                )).ToList();
            ActiveHandgroupTex = ActiveBodyTex.Select(x => (RemoveExt(texnames.FirstOrDefault(y => RemoveExt(y) == "huao"+FindHG(x))) ?? "huao"+ CurrentSkin).Substring(4)).ToList();

            for (int i=0; i<ActiveHeads.Count; i++)
            {
                if (ActiveHeadTex[i] == null)
                {
                    ActiveHeadTex.RemoveAt(i);
                    ActiveHeads.RemoveAt(i--);
                }
            }

            for (int i = 0; i < ActiveBodies.Count; i++)
            {
                if (ActiveBodyTex[i] == null)
                {
                    ActiveBodyTex.RemoveAt(i);
                    ActiveHandgroupTex.RemoveAt(i);
                    ActiveBodies.RemoveAt(i--);
                }
            }

            HeadPositionLast = 0;
            BodyPositionLast = 0;
            PopulateReal();
        }

        private string FindHG(string item)
        {
            var ind = item.IndexOf('_');
            if (ind != -1) item = item.Substring(ind);
            return item;
        }

        private string RemoveExt(string item)
        {
            if (item == null) return null;
            var ind = item.LastIndexOf('.');
            if (ind != -1) return item.Substring(0, ind);
            return item;
        }

        private string ExtractID(string item, string skncol)
        {
            var ind = item.IndexOf('_');
            if (ind != -1) item = item.Substring(0, ind);
            return item + skncol;
        }

        private void UpdateCarousel(UpdateState state)
        {
            //avatars are created in InitializeLot; bail out if that has not happened yet.
            if (vm == null || HeadAvatars == null || BodyAvatars == null) return;
            if (ActiveHeads == null || ActiveBodies == null || ActiveHeads.Count == 0 || ActiveBodies.Count == 0) return;

            var frac = 60f / FSOEnvironment.RefreshRate;
            var minSpeed = (Math.PI / 240f) * frac;
            var mult = (float)Math.Pow(0.95, frac);

            var moving = 0;

            if (state.MouseStates.Count(x => x.MouseState.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed) > 0)
            {
                if (XLast == -1)
                {
                    if (state.MouseState.Y > 282)
                        XLast = state.MouseState.X;
                }
                else
                {
                    if (state.MouseState.Y / (float)UIScreen.Current.ScreenHeight > 0.625f)
                    {
                        BodySpeed = ((XLast - state.MouseState.X) / 200f) * frac;
                        moving = 1;
                    }
                    else
                    {
                        HeadSpeed = ((XLast - state.MouseState.X) / 200f) * frac;
                        moving = 2;
                    }
                    XLast = state.MouseState.X;
                }
            }
            else
            {
                XLast = -1;
            }

            BodySpeed = BodySpeed * mult;
            if (Math.Abs(BodySpeed) < minSpeed && moving != 1)
            {
                var targInt = (int)Math.Round(BodyPosition);
                BodySpeed = (float)Math.Max(-minSpeed, Math.Min(minSpeed, targInt - BodyPosition));
            }

            HeadSpeed = HeadSpeed * mult;
            if (Math.Abs(HeadSpeed) < minSpeed && moving != 2)
            {
                var targInt = (int)Math.Round(HeadPosition);
                HeadSpeed = (float)Math.Max(-minSpeed, Math.Min(minSpeed, targInt - HeadPosition));
            }

            HeadPosition += HeadSpeed;
            BodyPosition += BodySpeed;

            var room = vm.Context.GetRoomAt(LotTilePos.FromBigTile(28, 21, 1));
            foreach (var body in BodyAvatars) body.SetRoom(room);
            foreach (var head in HeadAvatars) head.SetRoom(65534);

            var curbody = (int)BodyPosition;
            if (curbody != BodyPositionLast)
            {
                FSO.HIT.HITVM.Get().PlaySoundEvent(FSO.Client.UI.Model.UISounds.Click);
                int replacePos = (int)DirectionUtils.PosMod((-BodyPositionLast), 18);
                int increment = 1;
                if (curbody < BodyPositionLast) //start adding after last position's compliment position    
                {
                    increment = -1;
                    replacePos = (int)DirectionUtils.PosMod((-BodyPositionLast), 18);
                }
                var total = Math.Abs(curbody - BodyPositionLast);
                for (int i=0; i<total; i++)
                {
                    if (increment == -1)
                    {
                        SetBody(BodyAvatars[replacePos], BodyPositionLast-1);
                    } else
                    {
                        SetBody(BodyAvatars[replacePos], BodyPositionLast + 17);
                    }
                    BodyPositionLast += increment;
                    replacePos = ((replacePos - increment) + 18) % 18;

                }
                BodyPositionLast = curbody;
            }

            var curhead = (int)HeadPosition;
            if (curhead != HeadPositionLast)
            {
                FSO.HIT.HITVM.Get().PlaySoundEvent(FSO.Client.UI.Model.UISounds.Click);
                int replacePos = (int)DirectionUtils.PosMod(-HeadPositionLast, 18);
                int increment = 1;
                if (curhead < HeadPositionLast) //start adding after last position's compliment position    
                {
                    increment = -1;
                    replacePos = (int)DirectionUtils.PosMod((-HeadPositionLast), 18);
                }
                var total = Math.Abs(curhead - HeadPositionLast);
                for (int i = 0; i < total; i++)
                {
                    if (increment == -1)
                    {
                        SetHead(HeadAvatars[replacePos], HeadPositionLast - 1);
                    }
                    else
                    {
                        SetHead(HeadAvatars[replacePos], HeadPositionLast + 17);
                    }
                    HeadPositionLast += increment;
                    replacePos = ((replacePos - increment) + 18) % 18;

                }
                HeadPositionLast = curhead;
            }
        }

        private void SetBody(VMAvatar body, int i)
        {
            i = (int)DirectionUtils.PosMod(i, ActiveBodies.Count);
            var code = CurrentCode[0];
            if (CurrentCode[1] != 'a') code = 'u';
            var oft = new Outfit() { TS1AppearanceID = ActiveBodies[i] + ".apr", TS1TextureID = ActiveBodyTex[i] };
            var hg = ActiveHandgroupTex[i];
            oft.LiteralHandgroup = new HandGroup()
            {
                TS1HandSet = true,
                LightSkin = new HandSet()
                {
                    LeftHand = new Hand()
                    {
                        Idle = new Gesture() { Name = "h" + code + "lo.apr", TexName = "huao" + hg },
                        Pointing = new Gesture() { Name = "h" + code + "lp.apr", TexName = "huap" + hg },
                        Fist = new Gesture() { Name = "h" + code + "lc.apr", TexName = "huac" + hg }
                    },
                    RightHand = new Hand()
                    {
                        Idle = new Gesture() { Name = "h" + code + "ro.apr", TexName = "huao" + hg },
                        Pointing = new Gesture() { Name = "h" + code + "rp.apr", TexName = "huap" + hg },
                        Fist = new Gesture() { Name = "h" + code + "rc.apr", TexName = "huac" + hg }
                    }
                }
            };

            body.BodyOutfit = new FSO.SimAntics.Model.VMOutfitReference(oft);
        }

        private void SetHead(VMAvatar head, int i)
        {
            i = (int)DirectionUtils.PosMod(i, ActiveHeads.Count);
            head.HeadOutfit = new FSO.SimAntics.Model.VMOutfitReference(new Outfit() { TS1AppearanceID = ActiveHeads[i] + ".apr", TS1TextureID = ActiveHeadTex[i] });
        }

        private void PopulateReal()
        {
            var child = CurrentCode[1] == 'c';
            //var animSource = HeadAvatars[0].Avatar.Skeleton;

            int i = 0;
            foreach (var head in HeadAvatars)
            {
                SetHead(head, 17-i);
                //head.Avatar.Skeleton = animSource;
                if (child != CurrentChild)
                {
                    head.Avatar.Skeleton = Content.Get().AvatarSkeletons.Get(child ? "child.skel" : "adult.skel");
                    head.Avatar.ReloadSkeleton();
                }
                i++;
            }
            i = 0;
            foreach (var body in BodyAvatars)
            {
                SetBody(body, 17-i);
                //body.Avatar.Skeleton = animSource;
                if (child != CurrentChild)
                {
                    body.Avatar.Skeleton = Content.Get().AvatarSkeletons.Get(child ? "child.skel" : "adult.skel");
                    body.Avatar.ReloadSkeleton();
                }
                i++;
            }
            CurrentChild = child;
        }

        /// <summary>
        /// Creates the Create-a-Sim screen.
        /// </summary>
        /// <param name="pendingEditFamilyId">
        /// Optional FAMI ChunkID of an existing, already placed family to open in the family
        /// editor. Pass null (the default) for the normal "create a new family" flow. The family
        /// is not decoded here - that needs the CAS VM, which is only created on the first Update
        /// - so the id is held until <see cref="InitializeLot"/> has run.
        /// </param>
        public TS1CASScreen(int? pendingEditFamilyId = null)
        {
            PendingEditFamilyId = pendingEditFamilyId;

            Dialogs = new DialogService();

            var ui = Content.Get().CustomUI;
            var gd = GameFacade.GraphicsDevice;

            CASPanel = new UISimCASPanel();
            CASPanel.OnCollectionChange += CASPanel_OnCollectionChange;
            CASPanel.Position = new Vector2(0, -400);
            CASPanel.OnRandom += CASPanel_OnRandom;
            Add(CASPanel);

            FamilyPanel = new UIFamilyCASPanel(RepresentFamily);
            FamilyPanel.ModifySim = ModifySim;
            Add(FamilyPanel);

            FamiliesPanel = new UIFamiliesCASPanel();
            FamiliesPanel.OnNewFamily += () => { SetMode(UICASMode.FamilyEdit); };
            FamiliesPanel.OnDeleteFamily += FamiliesPanel_OnDeleteFamily;
            Add(FamiliesPanel);

            BackButton = new UITwoStateButton(ui.Get("btn_back.png").Get(gd));
            BackButton.Position = new Vector2(25, ScreenHeight - 140);
            Add(BackButton);

            AcceptButton = new UITwoStateButton(ui.Get("btn_accept.png").Get(gd));
            AcceptButton.Position = new Vector2(ScreenWidth-140, ScreenHeight - 140);
            Add(AcceptButton);

            BackButton.OnButtonClick += GoBack;
            AcceptButton.OnButtonClick += Accept;
        }

        private int EditIndex = -1;

        public void ModifySim(bool delete, int index)
        {
            if (index == -1)
            {
                PrepareEdit(index);
                SetMode(UICASMode.SimEdit);
            } else
            {
                if (delete)
                {
                    var fam = RepresentFamily[index];
                    fam.Delete(true, vm.Context);
                    RepresentFamily.RemoveAt(index);
                    //A member of a family that is being edited in place leaves the FAMI's GUID
                    //list on save. RefGUID 0 means the sim was added during this session and never
                    //made it into the family, so there is nothing to remove.
                    var removed = WIPFamily[index];
                    if (removed != null && CasEdit.ShouldRewriteInPlace(removed.RefGUID))
                    {
                        PendingRemovals.Add(removed.RefGUID);
                    }
                    WIPFamily.RemoveAt(index);

                    foreach (var fam2 in RepresentFamily)
                    {
                        fam2.SetPosition(LotTilePos.OUT_OF_WORLD, Direction.NORTH, vm.Context);
                    }
                    for (int i=0; i<RepresentFamily.Count; i++)
                    {
                        SetFamilyMember(i);
                    }
                    FamilyPanel.Reset();
                } else
                {
                    //prepare sim edit mode with old sim's parameters
                    PrepareEdit(index);
                    SetMode(UICASMode.SimEdit);
                }
            }
        }

        private void PrepareEdit(int i)
        {
            EditIndex = i;
            if (i > -1)
            {
                var sim = WIPFamily[i];

                //load this family member's traits into the editor
                CASPanel.FirstNameTextBox.CurrentText = sim.Name;
                CASPanel.BioEdit.CurrentText = sim.Bio;
                switch (sim.Gender)
                {
                    case 0: CurrentCode = "ma"; break;
                    case 1: CurrentCode = "fa"; break;
                    case 2: CurrentCode = "mc"; break;
                    case 3: CurrentCode = "fc"; break;
                }
                for (int j = 0; j < 5; j++)
                {
                    CASPanel.Personalities[j].Points = sim.Personality[j] / 100;
                }
                CurrentSkin = sim.SkinColor;

                PopulateSimType(CurrentCode);

                //find index for the sims body and head

                BodyPosition = ActiveBodies.IndexOf(sim.Body) - 8;
                HeadPosition = ActiveHeads.IndexOf(sim.Head) - 8;
            } else
            {
                CASPanel.FirstNameTextBox.CurrentText = "";
                CASPanel.BioEdit.CurrentText = "";
                CurrentCode = "ma";
                CurrentSkin = "lgt";
                for (int j = 0; j < 5; j++)
                {
                    CASPanel.Personalities[j].Points = 0;
                }
                PopulateSimType(CurrentCode);
                BodyPosition = - 8;
                HeadPosition = - 8;
            }
            CASPanel.AType = CurrentCode;
            CASPanel.SkinType = CurrentSkin;
            CASPanel.UpdateTotalPoints();
            CASPanel.UpdateType();
        }

        /// <summary>
        /// The screen's dialogs, and the single confirm latch the three confirmations below share.
        /// One instance for the whole screen, which is what the single <c>ConfirmDialog</c> field
        /// they used to share meant: while one confirmation is up, none of the others can open.
        /// </summary>
        public DialogService Dialogs { get; private set; }

        /// <summary>
        /// Delete button on the family select screen. Asks for confirmation, then removes the
        /// family from the neighbourhood and refreshes the list.
        /// </summary>
        private void FamiliesPanel_OnDeleteFamily(int index)
        {
            if (Mode != UICASMode.FamilySelect) return;
            if (FamiliesPanel == null) return;
            if (index < 0 || index >= FamiliesPanel.Families.Count) return;
            var family = FamiliesPanel.Families[index];
            if (family == null) return;
            //The list now holds the shipped unplaced families too, and deleting one of those is
            //not undoable from inside the game. Only families the CAS created may be removed.
            if (!CasFamilyFilter.CanDelete(family))
            {
                SimitoneLog.Warn("CAS refused to delete family " + family.ChunkID
                    + ": it is not a CAS-created family.");
                return;
            }

            //Pin the family by id, not by index. The list is rebuilt whenever a family is created,
            //edited or deleted, and the confirmation can sit open across one of those, so a captured
            //index would name whichever family slid into the slot by the time "yes" is clicked.
            //IndexOfChunkId answers -1 if the family is gone, which the guard at the top turns into
            //a no-op.
            var familyId = family.ChunkID;

            //same confirm/reuse pattern as Accept and GoBack: the dialog's yes callback re-enters
            //this method with the latch still held, which falls through to the deletion below.
            if (Dialogs.TryBeginConfirm())
            {
                //Table 128 holds the neighbourhood family-management strings, including the
                //localised delete confirmation ("Delete Family?" / "Are you sure you want to
                //delete the "%s" family?"). The family's display name is the FAMs string.
                //
                //Read through SimitoneStrings rather than straight off GameFacade.Strings, so a
                //table that is present but does not carry the id answers with the English
                //instead of putting "***MISSING***" in a dialog a player has to click through.
                var familyName = family.ChunkParent?.Get<FAMs>(family.ChunkID)?.GetString(0) ?? "";
                Dialogs.ShowConfirm(
                    SimitoneStrings.GetFromTable(CasFamilyTable, CasDeleteTitleId, "Delete Family?"),
                    SimitoneStrings.FormatFromTable(CasFamilyTable, CasDeleteMessageId,
                        "Are you sure you want to delete the \"%s\" family?", familyName),
                    () => FamiliesPanel_OnDeleteFamily(
                        CasFamilyFilter.IndexOfChunkId(FamiliesPanel.Families, familyId)),
                    null);
                return;
            }
            else
            {
                if (!DeleteFamily(family))
                {
                    SimitoneLog.Warn("CAS could not delete family " + family.ChunkID + " from the neighbourhood.");
                    return;
                }
            }

            SimitoneLog.Info("CAS deleted family " + family.ChunkID + " from the neighbourhood.");
            SetFamilies();
            FamiliesPanel.SetSelection(-1);
        }

        /// <summary>
        /// Removes a family from the neighbourhood's MainResource.
        /// The TS1 neighbourhood provider has no high level delete API, so this mirrors what FreeSO
        /// itself does (see VMGenericTS1Call.TryDeleteFamily/RemoveFromFamily): reset the member sims'
        /// family id and hard remove the FAMI chunk from the resource, then re-save the neighbourhood.
        /// The member sims are intentionally left in the neighbourhood (as townies, family id 0)
        /// because there is no API to remove neighbours or their character IFFs.
        /// </summary>
        private bool DeleteFamily(FAMI family)
        {
            if (family == null) return false;
            //chunk id 0 is the townies family, never deletable.
            if (family.ChunkID == 0) return false;
            //Only CAS-created families. The move-in list shows the shipped families as well, and
            //there is no way to put a shipped FAMI back once it is gone, so this is checked here
            //too and not only in the button handler that reaches it.
            if (!CasFamilyFilter.CanDelete(family))
            {
                SimitoneLog.Warn("CAS will not delete family " + family.ChunkID
                    + ": it is not a CAS-created family.");
                return false;
            }
            var neigh = Content.Get()?.Neighborhood;
            if (neigh?.MainResource == null) return false;
            var main = neigh.MainResource;
            if (family.ChunkParent != main) return false;

            //clear family references on the members, otherwise their person data would point at a
            //family id that gets re-used by the next created family.
            if (family.FamilyGUIDs != null && neigh.Neighbors != null)
            {
                foreach (var guid in family.FamilyGUIDs)
                {
                    var nid = neigh.GetNeighborIDForGUID(guid);
                    var member = (nid != null) ? neigh.GetNeighborByID(nid.Value) : null;
                    var pd = member?.PersonData;
                    if (pd != null && pd.Length > (int)VMPersonDataVariable.TS1FamilyNumber)
                        pd[(int)VMPersonDataVariable.TS1FamilyNumber] = 0;
                }
            }

            //the name chunk shares the family's chunk id. it has to go too, otherwise it would
            //collide with the next family that reuses the id.
            var nameChunk = main.Get<FAMs>(family.ChunkID);
            main.FullRemoveChunk(family);
            if (nameChunk != null) main.FullRemoveChunk(nameChunk);

            //drop the provider's house->family cache entry so it can't hand out a removed family.
            if (neigh.FamilyForHouse != null)
            {
                foreach (var kvp in neigh.FamilyForHouse.Where(x => x.Value == family).Select(x => x.Key).ToList())
                    neigh.FamilyForHouse.Remove(kvp);
            }

            return neigh.SaveNeighbourhood(false);
        }

        #region editing an existing family in place

        /// <summary>
        /// Resolves and opens the family handed in by <see cref="GameController.EnterCAS(int?)"/>.
        /// Called once from Update, after the CAS VM exists but before the screen picks its first
        /// mode, because decoding a family needs the VM to spawn the preview avatars.
        /// </summary>
        private void LoadPendingEditFamily()
        {
            if (PendingEditFamilyId == null) return;
            var familyId = PendingEditFamilyId.Value;
            PendingEditFamilyId = null;

            if (familyId <= 0 || familyId > ushort.MaxValue)
            {
                //0 is the townies family, which has no members to edit.
                SimitoneLog.Warn("CAS was asked to edit family " + familyId + ", which is not a valid family id.");
                return;
            }
            var family = Content.Get()?.Neighborhood?.GetFamily((ushort)familyId);
            if (family == null)
            {
                SimitoneLog.Warn("CAS could not find family " + familyId + " in the current neighbourhood; " +
                    "falling back to the family select screen.");
                return;
            }
            LoadFamilyForEdit(family);
        }

        /// <summary>
        /// The player-facing wording for a <see cref="CasEditEligibility"/> refusal, so the log says
        /// which rule stopped the edit rather than just that it was stopped.
        /// </summary>
        private static string EditIneligibilityReason(CasEditEligibility eligibility, FAMI family)
        {
            switch (eligibility)
            {
                case CasEditEligibility.NoFamily:
                    return "there is no family to edit";
                case CasEditEligibility.Townies:
                    return "it is the townies family, which has no members to edit";
                case CasEditEligibility.NoMembers:
                    return "it has no members";
                case CasEditEligibility.TooManyMembers:
                    //The CAS caps a family at 8 members. Editing a larger one would have to drop
                    //sims on save, so refuse rather than silently shrink somebody's family.
                    return "it has " + (family?.FamilyGUIDs?.Length ?? 0)
                        + " members, more than the CAS limit of " + CasValidation.MaxSimsPerFamily;
                default:
                    return "it is not eligible for editing";
            }
        }

        /// <summary>
        /// Loads an existing, already placed family into the family editor so its members can be
        /// changed and written back in place by <see cref="SaveFamilyInPlace"/>.
        ///
        /// The screen is left in a "create new family" state if anything goes wrong: nothing is
        /// thrown, an unusable family logs and returns with <see cref="EditingFamily"/> still null
        /// so the caller falls back to the family select screen.
        ///
        /// <para><b>What round trips losslessly.</b> For a sim the CAS itself created (and for any
        /// sim whose body strings follow the same conventions):
        /// <list type="bullet">
        /// <item>name and bio - CTSS 2000 strings 0 and 1, exactly what PrepareTemplatePerson writes;</item>
        /// <item>body appearance + texture - STR 200 string 1, first ';'-separated alternative;</item>
        /// <item>head appearance + texture - STR 200 string 2;</item>
        /// <item>hand group texture - STR 200 string 17, with the "huao" prefix removed;</item>
        /// <item>skin colour - STR 200 string 14 ("lgt"/"med"/"drk");</item>
        /// <item>adult/child and male/female - person data 58 and 65, which is what the engine
        /// reads, with STR 200 strings 0 and 12 as the fallback;</item>
        /// <item>the five personality bars - person data 2, 3, 5, 6 and 7, the same indices
        /// <c>SimTemplateCreateInfo.MakePersonData</c> writes.</item>
        /// </list>
        /// </para>
        ///
        /// <para><b>What is best effort.</b> Everything else in a character IFF. The clothing
        /// (STR 200 strings 30-34), the "add-on" outfit, the walk/swim animation overrides and the
        /// exact age in days are not surfaced by the CAS and are not carried through: saving an
        /// unedited member regenerates those from the sim type code, so opening a family and
        /// accepting it without touching a sim is <em>not</em> guaranteed to be byte identical to
        /// before. Skills, job, relationships, money, interests and the neighbour's ID live in the
        /// NBRS person data and are preserved, because only the indices listed above are written.
        /// A member that cannot be decoded at all (no neighbour record, no body string, or no
        /// usable body/head appearance) is skipped with a log line rather than saved as a
        /// half-decoded sim - it is left untouched in the neighbourhood and keeps its place in
        /// FAMI.FamilyGUIDs, because it was never added to <see cref="PendingRemovals"/>.</para>
        /// </summary>
        /// <param name="family">The FAMI to edit. Must belong to the current neighbourhood's MainResource.</param>
        public void LoadFamilyForEdit(FAMI family)
        {
            //Drop any previous edit session first. Clearing the state up front means the teardown
            //below cannot record the outgoing members as "deleted by the player".
            EditingFamily = null;
            PendingRemovals.Clear();
            ClearFamily();

            //Not the townies family, at least one member, and no more than the CAS can hold. The
            //rules themselves are in CasFamilyFilter so they can be tested; this only turns a
            //refusal into a message the player or the log can act on.
            var eligibility = CasFamilyFilter.EditEligibility(family);
            if (eligibility != CasEditEligibility.Eligible)
            {
                SimitoneLog.Warn("CAS cannot edit family " + (family?.ChunkID.ToString() ?? "(none)")
                    + ": " + EditIneligibilityReason(eligibility, family)
                    + ".");
                return;
            }

            var neigh = Content.Get()?.Neighborhood;
            if (neigh?.MainResource == null || family.ChunkParent != neigh.MainResource)
            {
                SimitoneLog.Warn("CAS cannot edit family " + family.ChunkID + ": it is not part of the current neighbourhood.");
                return;
            }

            if (FamilyPanel != null)
            {
                //the family's display name is the FAMs chunk that shares its chunk id.
                FamilyPanel.SecondName.CurrentText =
                    neigh.MainResource.Get<FAMs>(family.ChunkID)?.GetString(0) ?? "";
            }

            var skipped = 0;
            for (int i = 0; i < family.FamilyGUIDs.Length; i++)
            {
                var member = BuildMemberForEdit(neigh, family.FamilyGUIDs[i], i);
                if (member == null)
                {
                    skipped++;
                    continue;
                }
                WIPFamily.Add(member);
                SetFamilyMember(WIPFamily.Count - 1);
            }

            if (skipped > 0)
            {
                SimitoneLog.Warn("CAS skipped " + skipped + " of " + family.FamilyGUIDs.Length
                    + " unreadable members of family " + family.ChunkID + "; they are left untouched in the neighbourhood.");
            }
            if (WIPFamily.Count == 0)
            {
                SimitoneLog.Warn("CAS could not decode any member of family " + family.ChunkID
                    + "; falling back to the family select screen.");
                ClearFamily();
                return;
            }

            EditingFamily = family;
            SimitoneLog.Info("CAS opened family " + family.ChunkID + " for editing with "
                + WIPFamily.Count + " of " + family.FamilyGUIDs.Length + " members loaded.");
        }

        /// <summary>
        /// Decodes one neighbour plus its character IFF into a CAS family member. Returns null -
        /// never throws - when the member cannot be decoded well enough to be re-saved; the caller
        /// skips it and leaves the underlying sim alone.
        /// </summary>
        /// <param name="neigh">The current neighbourhood provider.</param>
        /// <param name="guid">The neighbour GUID from FAMI.FamilyGUIDs.</param>
        /// <param name="replaceIndex">The member's position in the original FAMI, recorded on the
        /// result as CASFamilyMember.ReplaceIndex for diagnostics.</param>
        private CASFamilyMember BuildMemberForEdit(TS1NeighborhoodProvider neigh, uint guid, int replaceIndex)
        {
            try
            {
                var neighbour = FindNeighbour(neigh, guid);
                if (neighbour == null)
                {
                    //without an NBRS record there is no person data to write back to, and the
                    //family's GUID would dangle. skip it.
                    SimitoneLog.Warn("CAS could not find a neighbour record for " + guid + "; skipping this member.");
                    return null;
                }

                var obj = Content.Get()?.WorldObjects?.Get(guid);
                if (obj?.OBJ == null || obj.Resource == null)
                {
                    SimitoneLog.Warn("CAS could not find the character IFF for " + guid + "; skipping this member.");
                    return null;
                }

                //VMAvatar reads the body strings through OBJ.BodyStringID; 0 would mean "chunk 0",
                //which is never the case, so fall back to the 200 the template person uses.
                var bodyId = (obj.OBJ.BodyStringID == 0) ? (ushort)200 : obj.OBJ.BodyStringID;
                var body = obj.Resource.Get<STR>(bodyId);
                if (body == null)
                {
                    SimitoneLog.Warn("CAS found no body strings in the character IFF for " + guid + "; skipping this member.");
                    return null;
                }

                //mirrors Outfit.Read(STR)/Outfit.ReadHead(string): the body list is ';'-separated
                //and the first entry wins, the head string is a single appearance.
                var bodyEntry = CasEdit.FirstBodyStringEntry(BodyString(body, 1));
                var headEntry = BodyString(body, 2);

                var member = new CASFamilyMember()
                {
                    Body = CasEdit.AppearanceId(bodyEntry),
                    BodyTex = CasEdit.TextureId(bodyEntry) ?? "",
                    Head = CasEdit.AppearanceId(headEntry),
                    HeadTex = CasEdit.TextureId(headEntry) ?? "",
                    RefGUID = guid,
                    ReplaceIndex = replaceIndex,
                    Personality = CasEdit.PersonalityFromPersonData(neighbour.PersonData),
                };

                if (string.IsNullOrEmpty(member.Body) || string.IsNullOrEmpty(member.Head))
                {
                    //a body/head we cannot name would be written back as an empty appearance and
                    //the character IFF would be unusable. skip rather than corrupt it.
                    SimitoneLog.Warn("CAS could not decode the body/head of " + guid + "; skipping this member.");
                    return null;
                }

                //skin first: it is the fallback for a hand group token that is missing, and the
                //hand texture is always "huao" + a skin code.
                member.SkinColor = ResolveSkinColor(body, neighbour);
                var handgroup = CasEdit.HandgroupTexFrom(BodyString(body, 17));
                member.HandgroupTex = string.IsNullOrEmpty(handgroup) ? member.SkinColor : handgroup;

                var name = ReadSimName(obj);
                if (string.IsNullOrEmpty(name))
                {
                    SimitoneLog.Warn("CAS could not decode a name for " + guid + "; skipping this member.");
                    return null;
                }
                member.Name = name;
                member.Bio = ReadSimBio(obj);

                ApplyGenderAndAge(member, body, neighbour);
                return member;
            }
            catch (Exception ex)
            {
                //Decoding reads content derived strings. A malformed one must never take the CAS
                //down mid-edit, and must never result in a half decoded sim being written back.
                SimitoneLog.Warn("CAS could not decode neighbour " + guid + "; skipping this member.", ex);
                return null;
            }
        }

        /// <summary>STR lookup that never returns null, so every reader above can index freely.</summary>
        private static string BodyString(STR body, int index)
        {
            if (body == null) return "";
            return body.GetString(index) ?? "";
        }

        /// <summary>
        /// The sim's display name. <c>VMEntity.ToString</c> reads CTSS 2000 string 0, which is
        /// exactly what PrepareTemplatePerson writes, so that is preferred. The OBJD chunk label
        /// ("user00001 - Bella") is the fallback; CAS-created characters are not guaranteed to
        /// have a CTSS chunk in every neighbourhood.
        /// </summary>
        private static string ReadSimName(GameObject obj)
        {
            string name = null;
            try
            {
                name = obj.Resource.Get<CTSS>(obj.OBJ.CatalogStringsID)?.GetString(0);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("CAS could not read the catalog strings of " + obj.GUID + ".", ex);
            }
            if (string.IsNullOrEmpty(name)) name = StripUserPrefix(obj.OBJ.ChunkLabel);
            return CasValidation.SanitizeSimName(name);
        }

        /// <summary>The sim's bio (CTSS 2000 string 1), or an empty string when there is none.</summary>
        private static string ReadSimBio(GameObject obj)
        {
            try
            {
                return obj.Resource.Get<CTSS>(obj.OBJ.CatalogStringsID)?.GetString(1) ?? "";
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("CAS could not read the bio of " + obj.GUID + ".", ex);
                return "";
            }
        }

        /// <summary>
        /// Strips the "user00001 - " prefix the generator writes into the OBJD chunk label, so the
        /// label can stand in for a missing CTSS name.
        /// </summary>
        private static string StripUserPrefix(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var dash = label.IndexOf(" - ", StringComparison.Ordinal);
            if (dash < 0) return label.Trim();
            var name = label.Substring(dash + 3).Trim();
            return name.Length > 0 ? name : label.Trim();
        }

        /// <summary>
        /// The skin colour the sim is actually wearing. STR 200 string 14 is what the body strings
        /// carry and therefore what the appearance was chosen for; person data 60 is the fallback.
        /// </summary>
        private static string ResolveSkinColor(STR body, Neighbour neighbour)
        {
            var stored = BodyString(body, 14);
            if (stored.Trim().Length > 0) return CasEdit.SkinColorCode(stored);
            var pd = neighbour?.PersonData;
            var index = (int)VMPersonDataVariable.SkinColor;
            return CasEdit.SkinColorFromPersonData((pd != null && pd.Length > index) ? pd[index] : (short)0);
        }

        /// <summary>
        /// Decides adult/child and male/female. Person data wins (that is what the engine reads
        /// back), with the body strings' "adult"/"child" and "male"/"female" tokens as the
        /// fallback. A pet token in string 12 is neither, so it reads as male - pets are not
        /// something the TS1 CAS can create, and refusing to load the family over one would be
        /// worse.
        /// </summary>
        private static void ApplyGenderAndAge(CASFamilyMember member, STR body, Neighbour neighbour)
        {
            var pd = neighbour.PersonData;
            var ageIndex = (int)VMPersonDataVariable.PersonsAge;
            var genderIndex = (int)VMPersonDataVariable.Gender;

            bool child;
            bool female;
            if (pd != null && pd.Length > ageIndex && pd.Length > genderIndex)
            {
                child = CasEdit.IsChildPersonAge(pd[ageIndex]);
                female = pd[genderIndex] == 1;
            }
            else
            {
                child = string.Equals(BodyString(body, 0), "child", StringComparison.OrdinalIgnoreCase);
                female = string.Equals(BodyString(body, 12), "female", StringComparison.OrdinalIgnoreCase);
            }
            member.Gender = CasEdit.GenderCode(child, female);
        }

        /// <summary>Finds a neighbour by GUID. The provider only exposes the id lookup, so this is the two step form.</summary>
        private static Neighbour FindNeighbour(TS1NeighborhoodProvider neigh, uint guid)
        {
            var id = neigh?.GetNeighborIDForGUID(guid);
            if (id == null) return null;
            return neigh.GetNeighborByID(id.Value);
        }

        /// <summary>
        /// Commits the family currently open in the editor back into the neighbourhood.
        ///
        /// Existing members (<c>CASFamilyMember.RefGUID != 0</c>) have their character IFF backed
        /// up and then regenerated through <c>SimitoneNeighbourGenerator.PrepareTemplatePerson</c>;
        /// sims added during the session are created and their new GUIDs merged into
        /// FAMI.FamilyGUIDs; sims deleted during the session are merged out. The FAMI's display
        /// name is updated from the family panel. Nothing here runs unless a family was actually
        /// opened for editing.
        ///
        /// <para>A deleted member keeps its neighbour record and character IFF and simply stops
        /// being listed in the family - it becomes a townie. There is no API to remove a
        /// neighbour or its file, which is the same trade-off <see cref="DeleteFamily"/> already
        /// makes.</para>
        ///
        /// <para>Note on what "in place" means upstream: <c>PrepareTemplatePerson</c> does not
        /// edit the existing <c>user#####.iff</c>, it writes a freshly generated one and re-points
        /// the object provider at it. That is fine - the neighbour id, person data and therefore
        /// relationships all survive - but it would leave the old file on disk, and a later
        /// neighbourhood load scans that directory and would re-register whichever file it
        /// happens to see last. So the superseded file is deleted here, after a backup has been
        /// taken.</para>
        /// </summary>
        public void SaveFamilyInPlace()
        {
            var family = EditingFamily;
            if (family == null)
            {
                SimitoneLog.Warn("CAS SaveFamilyInPlace was called with no family open for editing; ignoring.");
                return;
            }
            var neigh = Content.Get()?.Neighborhood;
            if (neigh?.MainResource == null || family.ChunkParent != neigh.MainResource)
            {
                SimitoneLog.Warn("CAS cannot save family " + family.ChunkID + " in place: it is not part of the current neighbourhood.");
                return;
            }

            var name = CasValidation.SanitizeFamilyName(FamilyPanel?.SecondName?.CurrentText);
            if (!CasValidation.IsValidFamilyName(name))
            {
                SimitoneLog.Warn("CAS cannot save family " + family.ChunkID + " in place: the family name is empty or too long.");
                return;
            }
            SetFamilyDisplayName(neigh, family, name);

            var existing = family.FamilyGUIDs ?? new uint[0];
            var added = new List<uint>();
            var rewritten = 0;
            var created = 0;

            for (int i = 0; i < WIPFamily.Count; i++)
            {
                var member = WIPFamily[i];
                if (member == null) continue;
                var info = CASToNeighGen(member);
                //the person data records the family id, so a new member has to know which family it
                //is joining before MakePersonData runs.
                info.FamilyID = (short)family.ChunkID;

                if (CasEdit.ShouldRewriteInPlace(member.RefGUID))
                {
                    var previousPath = GetCharacterIffPath(member.RefGUID);
                    BackupCharacterIff(previousPath, member.RefGUID);
                    SimitoneNeighbourGenerator.PrepareTemplatePerson(member.RefGUID, info);
                    RemoveSupersededCharacterIff(previousPath, member.RefGUID);
                    //appearance lives in the character IFF, but gender, age, skin, personality and
                    //the family id live in the NBRS person data and have to be refreshed too.
                    ApplyTemplatePersonData(FindNeighbour(neigh, member.RefGUID)?.PersonData, info);
                    rewritten++;
                }
                else
                {
                    var guid = SimitoneNeighbourGenerator.GenerateGUID(existing.Concat(added).ToArray());
                    SimitoneNeighbourGenerator.CreateNeighbor(guid, info);
                    member.RefGUID = guid;
                    member.ReplaceIndex = family.FamilyGUIDs.Length + added.Count;
                    added.Add(guid);
                    created++;
                }
            }

            family.FamilyGUIDs = CasEdit.MergeFamilyGuids(existing, added.ToArray(), PendingRemovals.ToArray());
            PendingRemovals.Clear();

            if (!neigh.SaveNeighbourhood(true))
            {
                SimitoneLog.Warn("CAS edited family " + family.ChunkID + " in memory, but the neighbourhood could not be written to disk.");
            }
            else
            {
                SimitoneLog.Info("CAS saved family " + family.ChunkID + " in place: " + rewritten
                    + " member(s) rewritten, " + created + " created, " + family.FamilyGUIDs.Length + " total.");
            }

            EditingFamily = null;
            SetFamilies();
            ClearFamily();
        }

        /// <summary>
        /// Writes the family's display name into the FAMs chunk that shares its chunk id, creating
        /// the chunk when the neighbourhood does not have one (some shipped families only have the
        /// name inside their FAMI's history).
        /// </summary>
        private static void SetFamilyDisplayName(TS1NeighborhoodProvider neigh, FAMI family, string name)
        {
            var nameChunk = neigh.MainResource.Get<FAMs>(family.ChunkID);
            if (nameChunk == null)
            {
                nameChunk = new FAMs()
                {
                    ChunkLabel = "",
                    ChunkID = family.ChunkID,
                    ChunkProcessed = true,
                    ChunkType = "FAMs",
                    ChunkParent = neigh.MainResource,
                    AddedByPatch = true,
                };
                nameChunk.InsertString(0, new STRItem() { Comment = "", Value = name });
                neigh.MainResource.AddChunk(nameChunk);
                return;
            }
            if (nameChunk.Length > 0)
            {
                //STR.SetString is a silent no-op for an out of range index, so check the length.
                nameChunk.SetString(0, name);
            }
            else
            {
                nameChunk.InsertString(0, new STRItem() { Comment = "", Value = name });
            }
        }

        /// <summary>
        /// The path of the character IFF the object provider currently has registered for a GUID,
        /// or null when it has none.
        /// </summary>
        private static string GetCharacterIffPath(uint guid)
        {
            try
            {
                var objs = Content.Get()?.WorldObjects;
                if (objs?.Entries == null) return null;
                GameObjectReference reference;
                lock (objs.Entries)
                {
                    objs.Entries.TryGetValue(guid, out reference);
                }
                return reference?.FileName;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("CAS could not resolve the character IFF path for " + guid + ".", ex);
                return null;
            }
        }

        /// <summary>
        /// Copies a character's IFF next to its siblings' backup folder before it is regenerated.
        ///
        /// The backup deliberately does not live in the Characters directory itself:
        /// <c>TS1NeighborhoodProvider.LoadCharacters</c> enumerates that directory and
        /// <c>return</c>s (not <c>continue</c>s) on the first non-".iff" file, so a
        /// "user00001.iff.bak" sitting there would abort the scan and leave characters
        /// unregistered. See <see cref="CasEdit.BackupDirectoryFor"/>.
        /// </summary>
        private void BackupCharacterIff(string characterIffPath, uint guid)
        {
            if (string.IsNullOrEmpty(characterIffPath))
            {
                SimitoneLog.Warn("CAS has no character IFF on disk for " + guid + "; there is nothing to back up.");
                return;
            }
            try
            {
                if (!File.Exists(characterIffPath))
                {
                    SimitoneLog.Warn("CAS could not back up " + characterIffPath + " for " + guid + ": the file is missing.");
                    return;
                }
                var userPath = Content.Get()?.Neighborhood?.UserPath;
                if (string.IsNullOrEmpty(userPath))
                {
                    SimitoneLog.Warn("CAS has no neighbourhood user path, so it cannot place a backup of " + characterIffPath + ".");
                    return;
                }
                var backupDir = CasEdit.BackupDirectoryFor(Path.Combine(userPath, "Characters"));
                if (string.IsNullOrEmpty(backupDir))
                {
                    SimitoneLog.Warn("CAS could not resolve the character backup folder; not backing up " + characterIffPath + ".");
                    return;
                }
                var backup = CasEdit.BackupPathFor(Path.Combine(backupDir, Path.GetFileName(characterIffPath)));
                Directory.CreateDirectory(backupDir);
                File.Copy(characterIffPath, backup, true);
                SimitoneLog.Info("CAS backed up " + characterIffPath + " to " + backup + " before editing sim " + guid + ".");
            }
            catch (Exception ex)
            {
                //A failed backup is worth shouting about but must not stop the edit itself: the
                //generator writes a brand new file rather than truncating the old one.
                SimitoneLog.Error("CAS could not back up the character IFF " + characterIffPath + " for " + guid, ex);
            }
        }

        /// <summary>
        /// Deletes the character IFF that <c>PrepareTemplatePerson</c> replaced, so the neighbourhood
        /// does not end up with two files claiming the same sim. Refuses to touch anything outside
        /// the neighbourhood's Characters directory, and never throws: if it cannot delete, the edit
        /// still stands and the log says so.
        /// </summary>
        private void RemoveSupersededCharacterIff(string previousPath, uint guid)
        {
            if (string.IsNullOrEmpty(previousPath)) return;
            try
            {
                var currentPath = GetCharacterIffPath(guid);
                if (string.IsNullOrEmpty(currentPath)) return;
                if (string.Equals(NormalisePath(previousPath), NormalisePath(currentPath), StringComparison.OrdinalIgnoreCase))
                {
                    //the generator wrote back over the same file. nothing to clean up.
                    return;
                }
                var userPath = Content.Get()?.Neighborhood?.UserPath;
                if (string.IsNullOrEmpty(userPath)) return;
                var characters = NormalisePath(Path.Combine(userPath, "Characters"));
                if (!NormalisePath(previousPath).StartsWith(characters, StringComparison.OrdinalIgnoreCase))
                {
                    SimitoneLog.Warn("CAS left " + previousPath + " in place: it is not inside the neighbourhood's Characters folder.");
                    return;
                }
                File.Delete(previousPath);
                SimitoneLog.Info("CAS removed the superseded character IFF " + previousPath + " for sim " + guid + ".");
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("CAS could not remove the superseded character IFF " + previousPath
                    + " for " + guid + "; the neighbourhood now has two files for this sim.", ex);
            }
        }

        private static string NormalisePath(string path)
        {
            return (path ?? "").Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// Copies the CAS owned fields of a template onto a neighbour's person data, leaving
        /// everything else (skills, job, relationships, money, interests) untouched. The indices
        /// are the ones <c>SimTemplateCreateInfo.MakePersonData</c> writes, so a rewritten member
        /// ends up with exactly the person data a freshly created one would have.
        /// </summary>
        private static void ApplyTemplatePersonData(short[] personData, SimTemplateCreateInfo info)
        {
            SetPersonData(personData, VMPersonDataVariable.NicePersonality, PersonalityPoint(info, 0));
            SetPersonData(personData, VMPersonDataVariable.ActivePersonality, PersonalityPoint(info, 1));
            SetPersonData(personData, VMPersonDataVariable.PlayfulPersonality, PersonalityPoint(info, 2));
            SetPersonData(personData, VMPersonDataVariable.OutgoingPersonality, PersonalityPoint(info, 3));
            SetPersonData(personData, VMPersonDataVariable.NeatPersonality, PersonalityPoint(info, 4));
            SetPersonData(personData, VMPersonDataVariable.PersonsAge, info.Child ? (short)9 : (short)27);
            SetPersonData(personData, VMPersonDataVariable.SkinColor, info.SkinTone);
            SetPersonData(personData, VMPersonDataVariable.TS1FamilyNumber, info.FamilyID);
            SetPersonData(personData, VMPersonDataVariable.Gender, info.Gender);
        }

        private static short PersonalityPoint(SimTemplateCreateInfo info, int index)
        {
            return (info.PersonalityPoints != null && index < info.PersonalityPoints.Length)
                ? info.PersonalityPoints[index] : (short)0;
        }

        private static void SetPersonData(short[] personData, VMPersonDataVariable variable, short value)
        {
            if (personData == null) return;
            var index = (int)variable;
            if (index >= 0 && index < personData.Length) personData[index] = value;
        }

        #endregion

        private void Accept(UIElement button)
        {
            switch (Mode)
            {
                case UICASMode.SimEdit:
                    //add or replace the sim in the family
                    AcceptMember();
                    break;
                case UICASMode.FamilyEdit:
                    //add or replace the family in the neighbourhood.
                    //need to generate an actual FAMI and save it for this - unless the family
                    //already exists, in which case it is committed in place. The confirmation is
                    //the same localised "are you sure" either way; table 129 has no separate
                    //"save changes to this family" string to use.
                    //
                    //The two English strings under 129 are Simitone's own wording, not TS1's:
                    //nothing in this repository records what 129:13/14 said, and an English
                    //fallback is still better than the "***MISSING***" a raw lookup would put in
                    //a dialog. Worth replacing with TS1's text if a UIText.iff is ever read here.
                    if (Dialogs.TryBeginConfirm())
                    {
                        Dialogs.ShowConfirm(
                            SimitoneStrings.GetFromTable(CasEditorTable, CasSaveTitleId, "Save Changes?"),
                            SimitoneStrings.GetFromTable(CasEditorTable, CasSaveMessageId,
                                "Are you sure you want to save the changes to this family?"),
                            () => Accept(button),
                            null);
                        return;
                    } else
                    {
                        //an already placed family is committed in place (its character IFFs are
                        //rewritten and its FAMI member list merged); a new one is created.
                        if (EditingFamily != null)
                        {
                            SaveFamilyInPlace();
                        }
                        else
                        {
                            SaveFamily();
                        }
                    }
                    break;
                case UICASMode.FamilySelect:
                    //accept button here is move in. notify the neighbourhood screen that we're moving in now.
                    //
                    //SelectedFamily resolves the panel's stored index to a live entry, and the
                    //CanMoveIn rule the list was built from is re-checked here, so a selection that
                    //went stale between the click and this handler can never move in a family that
                    //is not - or is no longer - a candidate.
                    var moving = FamiliesPanel.SelectedFamily;
                    if (moving == null || !CasFamilyFilter.CanMoveIn(moving))
                    {
                        SimitoneLog.Warn("CAS cannot move in the selected family: the family list selection is not a move-in candidate.");
                        return;
                    }
                    MoveInFamily = moving.ChunkID;
                    break;
            }
            SetMode((UICASMode)(((int)Mode) - 1));
        }

        private void GoBack(UIElement button)
        {
            if (Mode == UICASMode.FamilyEdit)
            {
                if (Dialogs.TryBeginConfirm())
                {
                    //Same caveat as the save confirmation above: the English under 129:7/8 is
                    //Simitone's wording, standing in for whatever TS1 said.
                    Dialogs.ShowConfirm(
                        SimitoneStrings.GetFromTable(CasEditorTable, CasDiscardTitleId, "Cancel Changes?"),
                        SimitoneStrings.GetFromTable(CasEditorTable, CasDiscardMessageId,
                            "Are you sure you want to leave the family editor without saving your changes?"),
                        () => GoBack(button),
                        null);
                    return;
                }
                else
                {
                    ClearFamily();
                }
            }
            SetMode((UICASMode)(((int)Mode) - 1));
        }

        public void SetMode(UICASMode mode)
        {
            if (mode == UICASMode.ToNeighborhood)
            {
                //todo: animate into this
                
                Dead = true;
                var dialog = new UITransDialog("normal", () => {
                    CleanupLastWorld();
                    if (MoveInFamily == null)
                        GameController.EnterGameMode("", false);
                    else
                        GameController.EnterGameMode("!"+((NeighTypeFrom == 7)?'m':'n')+MoveInFamily.Value.ToString(), false);
                });
                return;
            } else if (mode == UICASMode.FamilyEdit)
            {
                FamilyPanel.Reset();
            }

            FamiliesPanel.SetSelection(-1);
            if (mode == UICASMode.FamilySelect) AcceptButton.Texture = Content.Get().CustomUI.Get("btn_movein.png").Get(GameFacade.GraphicsDevice);
            else AcceptButton.Texture = Content.Get().CustomUI.Get("btn_accept.png").Get(GameFacade.GraphicsDevice);

            GameFacade.Screens.Tween.To(this, 1f, new Dictionary<string, float> { { "FamilySimInterp", (int)mode-1 } }, TweenQuad.EaseInOut);
            Mode = mode;
        }

        private static readonly Random CASRandom = new Random();

        private void CASPanel_OnRandom()
        {
            HeadPosition = CASRandom.Next(ActiveHeads.Count);
            BodyPosition = CASRandom.Next(ActiveBodies.Count);
            PopulateReal();
            HeadPositionLast = 0;
            BodyPositionLast = 0;
        }

        private void CASPanel_OnCollectionChange()
        {
            if (CurrentSkin == CASPanel.SkinType && CurrentCode == CASPanel.AType) return;
            CurrentSkin = CASPanel.SkinType;
            if (vm == null) return;
            PopulateSimType(CASPanel.AType);
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            ModePositions[3].Y = 11;
            ModeTargets[3].Y = 7.896059f;
            if (Dead) return;
            if (vm == null) InitializeLot();
            vm.Update();
            if (World != null && !Initialized)
            {
                World.State.DisableSmoothRotation = true;
                if (GraphicsModeControl.Mode == GlobalGraphicsMode.Full3D)
                {
                    World.State.SetCameraType(World, FSO.LotView.Utils.Camera.CameraControllerType.FirstPerson, 0);
                    var fp = World.State.Cameras.CameraFirstPerson;
                    Cam = fp.Camera;
                    fp.FixedCam = true;
                }
                LoadPendingEditFamily();
                //always populate the move-in list, even when opening straight into the editor:
                //backing out of an edit lands on the family select screen, and an empty list there
                //would strand the player.
                SetFamilies();
                if (EditingFamily != null)
                {
                    //straight into the family editor. SetMode(FamilyEdit) resets the avatar list
                    //panel, so it has to run *after* the members have been spawned, not before.
                    SetMode(UICASMode.FamilyEdit);
                }
                else
                {
                    SetMode(UICASMode.FamilySelect);
                }
                Initialized = true;
                //FamilySimInterp = FamilySimInterp;
            }

            if (World.State.PreciseZoom != 1) World.State.PreciseZoom = World.State.PreciseZoom;
            switch (Mode)
            {
                case UICASMode.FamilySelect:
                    if (World.State.Level != 2)
                    {
                        World.State.Level = 2;
                        World.State.DrawRoofs = true;
                        vm.Context.Blueprint.Cutaway = new bool[vm.Context.Blueprint.Cutaway.Length];
                        vm.Context.Blueprint.Changes.SetFlag(BlueprintGlobalChanges.WALL_CUT_CHANGED);
                    }
                    break;
                default:
                    if (World.State.Level != 1 && Cam == null)
                    {
                        World.State.Level = 1;
                        World.State.DrawRoofs = false;
                        vm.Context.Blueprint.Cutaway = VMArchitectureTools.GenerateRoomCut(vm.Context.Architecture, World.State.Level, World.State.CutRotation, 
                            new HashSet<uint>(vm.Context.RoomInfo.Where(x => x.Room.IsOutside == false).Select(x => (uint)x.Room.RoomID)));
                        vm.Context.Blueprint.Changes.SetFlag(BlueprintGlobalChanges.WALL_CUT_CHANGED);
                    }
                    break;
            }

            vm.Context.Clock.Minutes = 0;
            vm.Context.Clock.Hours = 12;

            var disableAccept = false;
            switch (Mode)
            {
                case UICASMode.SimEdit:
                    if (CASPanel.FirstNameTextBox.CurrentText.Length == 0) disableAccept = true;
                    break;
                case UICASMode.FamilySelect:
                    //the accept button is "move in", so it needs a selected family that really is a
                    //move-in candidate - not just any non-negative index.
                    if (!CasFamilyFilter.CanMoveIn(FamiliesPanel.SelectedFamily)) disableAccept = true;
                    break;
                case UICASMode.FamilyEdit:
                    //FamilyPanel is only guaranteed to exist in this mode, but guard anyway: a null
                    //panel means no name, which means nothing to save.
                    if (WIPFamily.Count == 0) disableAccept = true;
                    else if (FamilyPanel == null || !CasValidation.IsValidFamilyName(FamilyPanel.SecondName.CurrentText)) disableAccept = true;
                    break;
            }

            AcceptButton.Disabled = disableAccept;
            //AcceptButton.ForceState = disableAccept ? 0 : -1;
            //AcceptButton.Opacity = disableAccept ? 0.5f : 1;

            if (Mode == UICASMode.SimEdit)
                UpdateCarousel(state);

            //the carousel avatars are allocated in InitializeLot, which may not have run yet.
            if (HeadAvatars != null)
            {
                for (int i=0; i<18 && i<HeadAvatars.Length; i++)
                {
                    var head = HeadAvatars[i];
                    if (head == null) continue;
                    var relPos = HeadPosition + i - 9;
                    relPos = (float)DirectionUtils.PosMod(relPos, 18);
                    if (relPos > 9) relPos += 6;
                    var angle = (relPos / 24) * (Math.PI * 2);
                    var pos = new Vector3(28.5f + 4.5f*(float)Math.Cos(angle), 21.5f+ 4.5f * (float)Math.Sin(angle), 0);
                    head.RadianDirection = (float)angle + (float)Math.PI/2;
                    head.VisualPosition = pos;
                }
            }

            if (BodyAvatars != null)
            {
                for (int i = 0; i < 18 && i < BodyAvatars.Length; i++)
                {
                    var body = BodyAvatars[i];
                    if (body == null) continue;
                    var relPos = BodyPosition + i - 9;
                    relPos = (float)DirectionUtils.PosMod(relPos, 18);
                    if (relPos > 9) relPos += 6;
                    var angle = (relPos / 24) * (Math.PI * 2);
                    var pos = new Vector3(28.5f + 4.5f * (float)Math.Cos(angle), 21.5f + 4.5f * (float)Math.Sin(angle), 0);
                    body.RadianDirection = (float)angle + (float)Math.PI / 2;
                    body.VisualPosition = pos;
                }
            }

            foreach (var fam in RepresentFamily)
            {
                if (fam == null) continue;
                for (int i = 0; i < 16; i++)
                {
                    fam.SetMotiveData((VMMotive)i, 100);
                }
                var q = new List<FSO.SimAntics.Engine.VMQueuedAction>(fam.Thread.Queue);
                foreach (var action in q)
                    fam.Thread.CancelAction(action.UID);
            }
        }

        /// <summary>
        /// Rebuilds the "select or create family" list from the current neighbourhood.
        ///
        /// <para>
        /// The list is every family that has not moved in yet (<c>HouseNumber == 0</c>), minus the
        /// townies family. It is <em>not</em> gated on the CAS-created flag in
        /// <c>FAMI.Unknown</c>: the families that ship inside a stock <c>Neighborhood.iff</c>
        /// carry <c>Unknown == 0</c> (Bachelor, Roomies, Pleasant), so requiring bit 16 offered
        /// nothing at all on a stock install and no move-in could be started. The whole decision
        /// lives in <see cref="CasFamilyFilter"/> so it can be regression tested.
        /// </para>
        ///
        /// <para>
        /// The panel matches its old selection against the new list by family id rather than by
        /// index, so a family that survives the rebuild keeps being the move-in target and one
        /// that does not leaves the accept button disabled instead of silently moving in whatever
        /// slid into its place.
        /// </para>
        /// </summary>
        public void SetFamilies()
        {
            var main = Content.Get()?.Neighborhood?.MainResource;
            if (main == null)
            {
                //CAS can be entered without a neighbourhood (nothing to list). Leave whatever the
                //panel was showing alone rather than throwing out of a UI callback.
                SimitoneLog.Warn("CAS cannot list families: there is no neighbourhood main resource.");
                return;
            }
            var all = main.List<FAMI>();
            var families = CasFamilyFilter.MoveInCandidates(all);
            if (families.Count == 0)
            {
                SimitoneLog.Warn("CAS found no family that could be moved in ("
                    + (all?.Count ?? 0) + " families in the neighbourhood). Only new families can be created.");
            }
            else
            {
                SimitoneLog.Info("CAS offering " + families.Count + " family/families for move-in.");
            }
            FamiliesPanel.UpdateFamilies(families, vm);
        }

        public void SetFamilyMember(int index)
        {
            if (RepresentFamily.Count <= index)
            {
                var member = (VMAvatar)vm.Context.CreateObjectInstance(0x7FD96B54, LotTilePos.OUT_OF_WORLD
                    
                    , Direction.EAST, false).BaseObject;

                RepresentFamily.Add(member);
            }

            var fam = RepresentFamily[index];

            fam.SetPosition(LotTilePos.FromBigTile(34, 31, 1) +
                    new LotTilePos((short)((((index + 1) / 2) % 2) * 8), (short)(((index % 2) * 2 - 1) * ((index + 1) / 2) * 10), 0), Direction.EAST, vm.Context, VMPlaceRequestFlags.AllowIntersection);
            var data = WIPFamily[index];
            //set this person's body and head

            fam.Name = data.Name;
            fam.Avatar.Skeleton = Content.Get().AvatarSkeletons.Get((data.Gender>1) ? "child.skel" : "adult.skel").Clone();
            fam.Avatar.BaseSkeleton = fam.Avatar.Skeleton.Clone();
            fam.Avatar.ReloadSkeleton();

            fam.SetPersonData(VMPersonDataVariable.PersonsAge, (short)((data.Gender > 1) ? 12 : 21));
            fam.InitBodyData(vm.Context);

            var oft = new Outfit() { TS1AppearanceID = data.Body + ".apr", TS1TextureID = data.BodyTex };
            var code = CasHandSlots.HandLetterFor(data.Gender);
            var hg = data.HandgroupTex;
            oft.LiteralHandgroup = new HandGroup()
            {
                TS1HandSet = true,
                LightSkin = new HandSet()
                {
                    LeftHand = new Hand()
                    {
                        Idle = new Gesture() { Name = "h" + code + "lo.apr", TexName = "huao" + hg },
                        Pointing = new Gesture() { Name = "h" + code + "lp.apr", TexName = "huap" + hg },
                        Fist = new Gesture() { Name = "h" + code + "lc.apr", TexName = "huac" + hg }
                    },
                    RightHand = new Hand()
                    {
                        Idle = new Gesture() { Name = "h" + code + "ro.apr", TexName = "huao" + hg },
                        Pointing = new Gesture() { Name = "h" + code + "rp.apr", TexName = "huap" + hg },
                        Fist = new Gesture() { Name = "h" + code + "rc.apr", TexName = "huac" + hg }
                    }
                }
            };

            fam.BodyOutfit = new FSO.SimAntics.Model.VMOutfitReference(oft);
            fam.HeadOutfit = new FSO.SimAntics.Model.VMOutfitReference(new Outfit() { TS1AppearanceID = data.Head+".apr", TS1TextureID = data.HeadTex });
        }

        private SimTemplateCreateInfo CASToNeighGen(CASFamilyMember x)
        {
            var code = ((x.Gender & 1) == 0) ? "m" : "f";
            code += (x.Gender > 1) ? "c" : "a";
            var bodyType = CasValidation.BodyTypeCode(x.Body);
            code += bodyType;
            var info = new SimTemplateCreateInfo(code, x.SkinColor);
            info.Name = x.Name;
            info.Bio = x.Bio;
            info.PersonalityPoints = x.Personality;

            info.BodyStringReplace[1] = x.Body + ",BODY=" + x.BodyTex;
            info.BodyStringReplace[2] = x.Head + ",HEAD-HEAD=" + x.HeadTex;

            // The six handgroup slots come from the engine's shared writer, the same one
            // VMTS1MakeNewCharacter uses for a sim born from a lot script and
            // VMTS1BodyTransformHelper.BuildAdultBodyStrings uses for a grown-up one, so a
            // CAS-created sim has the hands every other writer of those slots gives it.
            //
            // It used to spell all six out here, and spelled slot 21 "H<hand>LO" - the left
            // *open* hand, in the slot Outfit.Read reads as the left *closed* one. The preview
            // standing next to it (SetBody/SetFamilyMember above) correctly named "hu...lc.apr",
            // so a sim looked right in the CAS and came out of it with its left fist drawn open.
            var handSlots = CasHandSlots.BodyStringsFor(x.Gender, x.HandgroupTex);
            foreach (var handSlot in handSlots)
            {
                info.BodyStringReplace[handSlot.Key] = handSlot.Value;
            }
            return info;
        }

        public void ClearFamily()
        {
            var count = WIPFamily.Count;
            FamilyPanel.SecondName.CurrentText = "";
            for (int i = count-1; i >= 0; i--)
                ModifySim(true, i);
            //ClearFamily is both "back out of the editor" and the teardown at the end of a save, so
            //it is where the in-place edit session ends. Dropping PendingRemovals here is what
            //makes backing out of an edit non destructive.
            PendingRemovals.Clear();
            EditingFamily = null;
            EditIndex = -1;
        }

        public void SaveFamily()
        {
            //Public, and reachable from outside the accept handler. Creating a *second* FAMI while a
            //family is open for editing would duplicate every sim in it, so defer to the in-place
            //save instead.
            if (EditingFamily != null)
            {
                SaveFamilyInPlace();
                return;
            }
            //the accept button is disabled when the name is unusable, but SaveFamily is public and
            //can be reached by other paths, so re-check before writing anything to the neighbourhood.
            var name = CasValidation.SanitizeFamilyName(FamilyPanel.SecondName.CurrentText);
            if (!CasValidation.IsValidFamilyName(name)) return;
            SimitoneNeighbourGenerator.CreateFamily(name, WIPFamily.Count, WIPFamily.Select(CASToNeighGen).ToArray());
            SetFamilies();
            ClearFamily();
        }

        public void AcceptMember()
        {
            var mem = BuildMember();
            //BuildMember returns null when the carousel data is not (or no longer) valid.
            if (mem == null) return;
            if (EditIndex == -1)
            {
                //replacing an existing member is always fine; adding is capped at MaxSimsPerFamily.
                if (!CasValidation.CanAddFamilyMember(WIPFamily.Count))
                {
                    SimitoneLog.Warn("CAS family is full (" + CasValidation.MaxSimsPerFamily + "); ignoring add.");
                    return;
                }
                WIPFamily.Add(mem);
                SetFamilyMember(WIPFamily.Count - 1);
            } else
            {
                //BuildMember always returns a brand new CASFamilyMember, so RefGUID (0) and
                //ReplaceIndex have to be carried over from the member being replaced. Without this
                //an edit of an existing sim would look like "add a new sim" and create a duplicate
                //instead of rewriting the character.
                var previous = WIPFamily[EditIndex];
                if (previous != null)
                {
                    mem.RefGUID = previous.RefGUID;
                    mem.ReplaceIndex = previous.ReplaceIndex;
                }
                WIPFamily[EditIndex] = mem;
                SetFamilyMember(EditIndex);
            }
        }

        public CASFamilyMember BuildMember()
        {
            //the carousel lists can be empty or missing entirely (eg. an Update ran before
            //InitializeLot, or the sim type has no content). bail out instead of indexing nothing.
            if (ActiveBodies == null || ActiveBodies.Count == 0) return null;
            if (ActiveHeads == null || ActiveHeads.Count == 0) return null;
            if (ActiveBodyTex == null || ActiveBodyTex.Count == 0) return null;
            if (ActiveHandgroupTex == null || ActiveHandgroupTex.Count == 0) return null;
            if (ActiveHeadTex == null || ActiveHeadTex.Count == 0) return null;

            //build the object out of the contents of various menus
            var i = (int)DirectionUtils.PosMod(Math.Round(BodyPosition+8), ActiveBodies.Count);
            var j = (int)DirectionUtils.PosMod(Math.Round(HeadPosition+8), ActiveHeads.Count);
            if (i < 0 || i >= ActiveBodies.Count) return null;
            if (j < 0 || j >= ActiveHeads.Count) return null;
            //the texture lists are built alongside the body/head lists, but stay defensive.
            if (i >= ActiveBodyTex.Count || i >= ActiveHandgroupTex.Count) return null;
            if (j >= ActiveHeadTex.Count) return null;

            var sim = new CASFamilyMember()
            {
                Name = CasValidation.SanitizeSimName(CASPanel.FirstNameTextBox.CurrentText),
                Bio = CASPanel.BioEdit.CurrentText,
                Body = ActiveBodies[i],
                BodyTex = ActiveBodyTex[i],
                HandgroupTex = ActiveHandgroupTex[i],
                Head = ActiveHeads[j],
                HeadTex = ActiveHeadTex[j],
                Gender = (short)(((CurrentCode[0] == 'm') ? 0 : 1) | ((CurrentCode[1] == 'c') ? 2 : 0)),
                Personality = CASPanel.Personalities.Select(x => (short)(x.Points * 100)).ToArray(),
                SkinColor = CurrentSkin
            };
            return sim;
        }

        public override void GameResized()
        {
            base.GameResized();
            World?.GameResized();
            BackButton.Position = new Vector2(25, ScreenHeight - 140);
            AcceptButton.Position = new Vector2(ScreenWidth - 140, ScreenHeight - 140);
            FamilySimInterp = FamilySimInterp;
        }

        public override void PreDraw(UISpriteBatch batch)
        {
            base.PreDraw(batch);
            vm?.PreDraw();
        }

        public override void Draw(UISpriteBatch batch)
        {
            base.Draw(batch);
        }

        public void CleanupLastWorld()
        {
            SimitoneLog.Info("CAS world cleanup");
            if (vm == null) return;

            //clear our cache too, if the setting lets us do that
            TimedReferenceController.Clear();
            TimedReferenceController.Clear();

            vm.Context.Ambience.Kill();
            foreach (var ent in vm.Entities)
            { //stop object sounds
                var threads = ent.SoundThreads;
                for (int i = 0; i < threads.Count; i++)
                {
                    threads[i].Sound.RemoveOwner(ent.ObjectID);
                }
                threads.Clear();
            }
            vm.CloseNet(VMCloseNetReason.LeaveLot);
            GameFacade.Scenes.Remove(World);
            World.Dispose();
            vm.SuppressBHAVChanges();
            vm = null;
            World = null;
            Driver = null;
        }

        public void InitializeLot()
        {
            CleanupLastWorld();
            
            World = new FSO.LotView.World(GameFacade.GraphicsDevice);

            World.Opacity = 1;
            GameFacade.Scenes.Add(World);

            var globalLink = new VMTS1GlobalLinkStub();
            Driver = new VMServerDriver(globalLink);

            vm = new VM(new VMContext(World), Driver, new UIHeadlineRendererProvider());
            vm.ListenBHAVChanges();
            vm.Init();

            using (var file = new BinaryReader(File.OpenRead(Path.Combine(FSOEnvironment.ContentDir, "cas.fsov"))))
            {
                var marshal = new FSO.SimAntics.Marshals.VMMarshal();
                marshal.Deserialize(file);
                marshal.PlatformState = new VMTS1LotState();
                vm.Load(marshal);
                vm.Reset();
            }
            vm.Tick();

            vm.Context.Clock.Hours = 12;
            vm.MyUID = uint.MaxValue;
            var settings = GlobalSettings.Default;
            var myClient = new VMNetClient
            {
                PersistID = uint.MaxValue,
                RemoteIP = "local",
                AvatarState = new VMNetAvatarPersistState()
                {
                    Name = settings.LastUser ?? "",
                    DefaultSuits = new VMAvatarDefaultSuits(settings.DebugGender),
                    BodyOutfit = settings.DebugBody,
                    HeadOutfit = settings.DebugHead,
                    PersistID = uint.MaxValue,
                    SkinTone = (byte)settings.DebugSkin,
                    Gender = (short)(settings.DebugGender ? 1 : 0),
                    Permissions = FSO.SimAntics.Model.TSOPlatform.VMTSOAvatarPermissions.Admin,
                    Budget = 1000000
                }

            };

            var server = (VMServerDriver)Driver;
            server.ConnectClient(myClient);

            HeadAvatars = new VMAvatar[18];
            for (int i=0; i<18; i++)
            {
                HeadAvatars[i] = (VMAvatar)vm.Context.CreateObjectInstance(0x7FD96B54, LotTilePos.OUT_OF_WORLD, Direction.NORTH, true).BaseObject;
            }

            BodyAvatars = new VMAvatar[18];
            for (int i = 0; i < 18; i++)
            {
                BodyAvatars[i] = (VMAvatar)vm.Context.CreateObjectInstance(0x7FD96B54, LotTilePos.OUT_OF_WORLD, Direction.NORTH, true).BaseObject;
            }

            PopulateSimType("ma");
        }
    }

    public class CASFamilyMember
    {
        public string Name;
        public string SkinColor;
        public short Gender; //adult 0,1... child 2,3

        public string Head;
        public string HeadTex;
        public string Body;
        public string BodyTex;
        public string HandgroupTex;

        public short[] Personality = new short[5];
        public string Bio;

        /// <summary>
        /// The neighbour GUID of the sim this member came from, or 0 for a sim that was created
        /// inside the current edit session. Non-zero means "this sim already exists in the
        /// neighbourhood": <c>TS1CASScreen.SaveFamilyInPlace</c> rewrites its character IFF rather
        /// than creating a new neighbour, and it is the only thing that decides which of the two
        /// happens. It survives being edited because AcceptMember copies it onto the replacement
        /// member that BuildMember produces.
        /// </summary>
        public uint RefGUID;

        /// <summary>
        /// The member's original index in FAMI.FamilyGUIDs, or -1 for a sim added during the
        /// current edit session. Informational only: the GUID, not the index, is what a save keys
        /// off, so an index that goes stale (or a member skipped during load shifting everything
        /// along) cannot corrupt anything.
        /// </summary>
        public int ReplaceIndex;
    }

    public enum UICASMode : int
    {
        ToNeighborhood = -1,
        FamilySelect = 0,
        FamilyEdit = 1,
        SimEdit = 2,
    }
}
