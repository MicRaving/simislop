using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels.LiveSubpanels.Catalog
{
    public class UICatalogItem : UITSContainer
    {
        /// <summary>Tint the row's background plate is drawn with when the row is affordable.</summary>
        private static readonly Color BgTint = new Color(104, 164, 184, 255);

        /// <summary>Opacity an affordable row draws at.</summary>
        private const float FullOpacity = 1f;

        /// <summary>
        /// Opacity an over-budget row draws at. The stock client swaps in a generated "disabled"
        /// cell texture; we cannot, because that path needs a GraphicsDevice and this row is built
        /// on demand while the panel scrolls. Fading the tile and greying the price is the same
        /// read, costs nothing, and is undone by <see cref="SetAffordable"/>.
        /// </summary>
        private const float DisabledOpacity = 0.45f;

        /// <summary>
        /// Catalog object icons, keyed by the object's GUID.
        /// </summary>
        /// <remarks>
        /// Bounded. It used to grow for the process lifetime - every icon the player ever scrolled
        /// past in Buy mode stayed resident on the GPU, and the only method that could release them
        /// had its one call site commented out - so a long session browsing a large catalog was a
        /// slow leak. Past <see cref="MaxCachedIcons"/> the oldest entries are dropped and their
        /// textures disposed; a row that scrolls back re-resolves its icon, which costs one content
        /// lookup and no memory.
        ///
        /// <para>
        /// Eviction is least-recently-used rather than first-come because the objects a player
        /// returns to are the ones they are shopping for. The order is tracked separately in
        /// <see cref="_iconOrder"/> so <see cref="IconCache"/> itself stays the plain map a caller
        /// (and a test) wants to look at.
        /// </para>
        /// </remarks>
        public static readonly Dictionary<uint, Texture2D> IconCache = new Dictionary<uint, Texture2D>();

        /// <summary>
        /// How many catalog icons stay resident. Big enough that paging through a category and
        /// coming back is free, small enough that the whole cache is a rounding error next to the
        /// lot's own textures.
        /// </summary>
        public const int MaxCachedIcons = 256;

        private static readonly Queue<uint> _iconOrder = new Queue<uint>();

        /// <summary>
        /// The keys to drop to bring <paramref name="count"/> back to
        /// <paramref name="capacity"/>, oldest first and never the key being added.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="order"/> is a use <em>history</em>, not a set, so a key that has been
        /// looked at twice appears twice. "Oldest" therefore means "whose most recent use is
        /// earliest", and only a key's <em>last</em> entry in the history is eligible - otherwise
        /// re-opening a page would make its icons the first thing thrown away, which is the
        /// opposite of what a recency order is for.
        /// </para>
        ///
        /// <para>
        /// Pure and static so the eviction policy is testable without a <see cref="Texture2D"/>,
        /// which needs a graphics device a headless test has no way to get.
        /// </para>
        /// </remarks>
        /// <param name="order">Keys in the order they were last asked for, oldest first.</param>
        /// <param name="count">How many keys the cache currently holds.</param>
        /// <param name="capacity">The bound.</param>
        /// <param name="incoming">The key about to be added, which must survive.</param>
        internal static List<uint> KeysToEvict(IEnumerable<uint> order, int count, int capacity, uint incoming)
        {
            var result = new List<uint>();
            if (capacity <= 0 || count <= capacity) return result;
            if (order == null) return result;

            var history = new List<uint>(order);
            var lastUse = new Dictionary<uint, int>();
            for (int i = 0; i < history.Count; i++) lastUse[history[i]] = i;

            for (int i = 0; i < history.Count && result.Count < count - capacity; i++)
            {
                var key = history[i];
                if (key == incoming) continue;
                if (lastUse[key] != i) continue;   //a later use of this key exists; not its time yet
                result.Add(key);
            }
            return result;
        }

        /// <summary>
        /// Drops every cached icon and releases its texture. A no-op when nothing is cached, and
        /// safe to call while rows are still on screen: they re-resolve on their next
        /// <see cref="GetObjIcon"/>.
        /// </summary>
        public static void ClearIconCache()
        {
            foreach (var item in IconCache)
            {
                Retire(item.Value);
            }
            IconCache.Clear();
            _iconOrder.Clear();
        }

        /// <summary>
        /// Textures that left the cache but may still be referenced by a row that is mid-frame.
        /// They are disposed once <see cref="RetireGraceMs"/> has passed; by then every row that
        /// held one has noticed (in <see cref="Draw"/>) that it is no longer the cached texture
        /// and has re-fetched.
        /// </summary>
        private static readonly Queue<KeyValuePair<long, Texture2D>> _retired = new Queue<KeyValuePair<long, Texture2D>>();
        private const long RetireGraceMs = 2000;

        private static void Retire(Texture2D texture)
        {
            if (texture != null) _retired.Enqueue(new KeyValuePair<long, Texture2D>(Environment.TickCount64, texture));
        }

        private static void DisposeRetired()
        {
            var now = Environment.TickCount64;
            while (_retired.Count > 0 && now - _retired.Peek().Key >= RetireGraceMs)
            {
                _retired.Dequeue().Value.Dispose();
            }
        }

        private uint _iconGuid;
        private bool _iconFromCache;
        public Texture2D BG;
        public Texture2D Icon;
        public Texture2D Outline;
        public bool Outlined;

        /// <summary>True when this row costs more than the family can currently pay.</summary>
        public bool Unaffordable { get; private set; }

        public UILabel PriceLabel;
        private UIBuyBrowsePanel BudgetProvider;

        public override void Draw(UISpriteBatch SBatch)
        {
            //the background plate is drawn with a fixed colour rather than BlendColor, so an
            //over-budget row needs its tint dimmed explicitly to match the faded icon.
            var bgColor = Unaffordable ? BgTint * DisabledOpacity : BgTint;
            DrawLocalTexture(SBatch, BG, null, new Vector2(BG.Width-90, BG.Height-105) / -2, Vector2.One, bgColor);
            var iconSize = 55f;
            //A cached icon can be evicted (and later disposed) while this row is still built;
            //only draw the instance the cache currently holds.
            if (_iconFromCache && (!IconCache.TryGetValue(_iconGuid, out var live) || !ReferenceEquals(live, Icon)))
            {
                Icon = GetObjIcon(_iconGuid);
            }
            if (Icon != null)
            {
                
                if (Icon.Width / (float)Icon.Height < 1.1f || Icon.Width == 127 || Icon.Width == 128)
                {
                    iconSize = 77.7f;
                    var scale = iconSize/(float)Math.Sqrt(Icon.Width * Icon.Width + Icon.Height * Icon.Height);
                    DrawLocalTexture(SBatch, Icon, new Rectangle(0, 0, Icon.Width, Icon.Height), new Vector2((Icon.Width*scale-90) / -2, (Icon.Height*scale-105) / -2), new Vector2(scale));
                }
                else DrawLocalTexture(SBatch, Icon, new Rectangle(0, 0, Icon.Width / 2, Icon.Height), new Vector2((iconSize-90) / -2, (iconSize- 105) / -2), new Vector2(iconSize / Icon.Height, iconSize / Icon.Height));
            }

            //One outline serves both states now, tinted by which of the two it is: the selected
            //row gets the full-strength selection colour, a row that is merely under the pointer
            //gets the dimmed hover colour. The player can therefore see what they are about to
            //click without that reading as "this one is already active".
            if (Outlined) DrawLocalTexture(SBatch, Outline, null, new Vector2(Outline.Width - 90, Outline.Height - 105) / -2, Vector2.One, IsSelected ? UIStyle.Current.ActiveSelection : UIStyle.Current.HoverSelection);
            base.Draw(SBatch);
        }

        public UICatalogItem(UICatalogElement elem, UIBuyBrowsePanel budgetProvider)
        {
            BG = Content.Get().CustomUI.Get("pswitch_icon_bg.png").Get(GameFacade.GraphicsDevice);
            if (elem.Special?.Res != null)
            {
                Icon = elem.Special.Res.GetIcon(elem.Special.ResID);
            }
            else
            {
                _iconGuid = elem.Item.GUID;
                _iconFromCache = true;
                Icon = GetObjIcon(_iconGuid);
            }
            Outline = Content.Get().CustomUI.Get("pswitch_icon_sel.png").Get(GameFacade.GraphicsDevice);

            PriceLabel = new UILabel();
            PriceLabel.Alignment = TextAlignment.Center | TextAlignment.Middle;
            PriceLabel.Position = new Vector2(0, 110);
            PriceLabel.Size = new Vector2(90, 1);
            PriceLabel.CaptionStyle = PriceLabel.CaptionStyle.Clone();
            PriceLabel.CaptionStyle.Color = UIStyle.Current.Text;
            PriceLabel.CaptionStyle.Size = 14;
            //CalcPrice, not Item.Price: it is the price after the lot's discount (and the donate
            //discount) has been applied, which is what the player is actually charged.
            PriceLabel.Caption = "§" + elem.CalcPrice.ToString();
            Add(PriceLabel);

            SetAffordable(BuildBudget.CanAfford(elem.CalcPrice, BudgetService.GetBudget()));

            BudgetProvider = budgetProvider;
        }

        /// <summary>
        /// Dims the row when it is over budget, and restores it when it is affordable again. Purely
        /// cosmetic, and reversible - the stock client likewise leaves an unaffordable row
        /// clickable, refusing it later in the build tool itself.
        /// </summary>
        public void SetAffordable(bool affordable)
        {
            Unaffordable = !affordable;
            Opacity = Unaffordable ? DisabledOpacity : FullOpacity;
            PriceLabel.CaptionStyle.Color = Unaffordable ? UIStyle.Current.BtnDisable : UIStyle.Current.Text;
        }

        /// <summary>
        /// Clicking the row that is already the active one puts it back down instead of selecting
        /// it a second time.
        ///
        /// <para>
        /// The strip calls <c>Deselected</c> on the outgoing row before <c>Selected</c> on the
        /// incoming one, so by the time we get here the row has already been un-highlighted - this
        /// is a re-click, not a fresh pick. Re-running <see cref="UIBuyBrowsePanel.Selected"/>
        /// would build a second out-of-world instance of an object the player already has in hand
        /// and leave the first one selected behind it, which reads as a purchase that did nothing.
        /// </para>
        /// </summary>
        public override void Selected()
        {
            if (BudgetProvider.ItemID == ItemID)
            {
                Deselected(); //no selection was made, so this row's own state must not claim one
                BudgetProvider.Deselect();
                return;
            }

            base.Selected();
            Outlined = true;
            BudgetProvider.Selected(ItemID);
        }

        /// <summary>
        /// Losing the selection only takes the outline away if the pointer is not on this row:
        /// a row that is still hovered stays outlined, in the hover tint. The base call is what
        /// makes <see cref="IsSelected"/> false, which is what <see cref="Unhovered"/> and
        /// <see cref="Draw"/> read to pick the tint.
        /// </summary>
        public override void Deselected()
        {
            base.Deselected();
            Outlined = IsHovered;
        }

        public override void Hovered()
        {
            base.Hovered();
            Outlined = true;
        }

        public override void Unhovered()
        {
            base.Unhovered();
            Outlined = IsSelected;
        }

        public Texture2D GetObjIcon(uint GUID)
        {
            if (!IconCache.ContainsKey(GUID))
            {
                var obj = Content.Get().WorldObjects.Get(GUID);
                if (obj == null)
                {
                    Cache(GUID, null);
                    return null;
                }
                var bmp = obj.Resource.Get<BMP>(obj.OBJ.CatalogStringsID);
                Cache(GUID, bmp != null ? bmp.GetTexture(GameFacade.GraphicsDevice) : null);
            }
            else
            {
                //A hit still counts as a use: this is the list the LRU order is built from, so a
                //player browsing back and forth keeps the objects they keep coming to.
                Touch(GUID);
            }
            return IconCache[GUID];
        }

        /// <summary>
        /// Adds one icon to the cache and keeps the cache within <see cref="MaxCachedIcons"/>,
        /// releasing the textures of whatever had to go.
        /// </summary>
        private static void Cache(uint guid, Texture2D texture)
        {
            IconCache[guid] = texture;
            _iconOrder.Enqueue(guid);

            foreach (var stale in KeysToEvict(_iconOrder, IconCache.Count, MaxCachedIcons, guid))
            {
                if (!IconCache.TryGetValue(stale, out var old)) continue;
                Retire(old);
                IconCache.Remove(stale);
            }
            DisposeRetired();

            //Keep the history from growing without bound. Dequeuing the oldest entries blindly
            //could drop the only entry of a cached key, after which KeysToEvict could never pick
            //it and the cache would grow past its cap; so compact instead: one entry per cached
            //key, in order of last use.
            if (_iconOrder.Count > IconCache.Count * 2 + MaxCachedIcons)
            {
                var seen = new HashSet<uint>();
                var kept = new List<uint>();
                foreach (var key in _iconOrder.Reverse())
                {
                    if (IconCache.ContainsKey(key) && seen.Add(key)) kept.Add(key);
                }
                kept.Reverse();
                _iconOrder.Clear();
                foreach (var key in kept) _iconOrder.Enqueue(key);
            }
        }

        /// <summary>Moves an already-cached key to the young end of the order.</summary>
        private static void Touch(uint guid)
        {
            _iconOrder.Enqueue(guid);
        }
    }
}
