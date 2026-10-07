using FSO.Client;
using FSO.Common.Rendering.Framework;
using FSO.Common.Rendering.Framework.Camera;
using FSO.Common.Utils;
using FSO.Content;
using FSO.SimAntics;
using FSO.SimAntics.Model;
using FSO.Vitaboy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Model
{
    /// <summary>
    /// Caches icons for objects missing them, eg. heads, some catalog objects..
    /// </summary>
    public static class UIIconCache
    {
        /// <summary>
        /// Rendered head icons, indexed as "mesh:texture".
        /// </summary>
        /// <remarks>
        /// Bounded, and clearable. It used to be a private dictionary that grew for the process
        /// lifetime with no way to empty it: every distinct head a player ever saw in a pie menu,
        /// a sim switcher, a relationship bar or a call-neighbour dialog stayed resident on the
        /// GPU, and generating one means rendering a whole 3D scene, so a long session in a
        /// neighbourhood full of tourists is a slow leak. Past <see cref="MaxCachedHeadIcons"/> the
        /// least recently used head is dropped and its texture disposed; a dialog that reopens
        /// re-renders it, which costs one frame and no memory.
        /// </remarks>
        private static readonly Dictionary<string, Texture2D> AvatarHeadCache = new Dictionary<string, Texture2D>();

        /// <summary>
        /// How many rendered head icons stay resident. There are at most a few hundred head meshes
        /// in a TS1 install, so this covers a whole neighbourhood's worth of Sims while still
        /// being a number rather than "however many the session produced".
        /// </summary>
        public const int MaxCachedHeadIcons = 64;

        /// <summary>Keys in the order they were last asked for, oldest first.</summary>
        private static readonly Queue<string> _headOrder = new Queue<string>();

        /// <summary>
        /// The keys to drop to bring <paramref name="count"/> back to
        /// <paramref name="capacity"/>, oldest first and never the key being added.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="order"/> is a use <em>history</em>, not a set, so a head that has been
        /// asked for twice appears twice. "Oldest" therefore means "whose most recent use is
        /// earliest", and only a key's <em>last</em> entry in the history is eligible - otherwise
        /// the Sims currently on screen would be the first icons thrown away.
        /// </para>
        ///
        /// <para>
        /// Pure and static so the eviction policy is testable without a <see cref="Texture2D"/>,
        /// which needs a graphics device a headless test has no way to get.
        /// </para>
        /// </remarks>
        internal static List<string> KeysToEvict(IEnumerable<string> order, int count, int capacity, string incoming)
        {
            var result = new List<string>();
            if (capacity <= 0 || count <= capacity) return result;
            if (order == null) return result;

            var history = new List<string>(order);
            var lastUse = new Dictionary<string, int>();
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
        /// Drops every rendered head icon and releases its texture. Called when the process is
        /// done with the lot - a switch to Downtown or back to the world - so the cache is empty
        /// when the next lot's Sims start asking for icons.
        /// </summary>
        public static void ClearAvatarHeadCache()
        {
            foreach (var texture in AvatarHeadCache.Values)
            {
                texture?.Dispose();
            }
            AvatarHeadCache.Clear();
            _headOrder.Clear();
        }

        /// <summary>How many head icons are currently resident. For tests and diagnostics.</summary>
        internal static int CachedHeadCount => AvatarHeadCache.Count;

        public static Texture2D GetObject(VMEntity obj)
        {
            if (obj is VMAvatar)
            {
                var ava = (VMAvatar)obj;
                var headname = ava.HeadOutfit.Name;
                if (headname == "") headname = ava.BodyOutfit.OftData.TS1TextureID;
                var id = headname +":"+ ava.HeadOutfit.OftData.TS1TextureID;

                if (!AvatarHeadCache.TryGetValue(id, out var result))
                {
                    result = GenHeadTex(ava);
                    AvatarHeadCache[id] = result;
                    _headOrder.Enqueue(id);
                    Evict(id);
                }
                else
                {
                    //A hit still counts as a use: this is the list the LRU order is built from, so
                    //the Sims on screen keep their icons and the ones scrolled past do not.
                    _headOrder.Enqueue(id);
                }
                return result;
            }
            else if (obj is VMGameObject)
            {
                if (obj.Object.OBJ.GUID == 0x000007C4) return Content.Get().CustomUI.Get("int_gohere.png").Get(GameFacade.GraphicsDevice);
                else return obj.GetIcon(GameFacade.GraphicsDevice, 0);
            }
            return null;
        }

        /// <summary>Releases whatever has fallen out of <see cref="MaxCachedHeadIcons"/>.</summary>
        private static void Evict(string keep)
        {
            foreach (var stale in KeysToEvict(_headOrder, AvatarHeadCache.Count, MaxCachedHeadIcons, keep))
            {
                if (!AvatarHeadCache.TryGetValue(stale, out var old)) continue;
                old?.Dispose();
                AvatarHeadCache.Remove(stale);
            }

            //Drop queue entries for keys that are no longer cached, so a long session does not
            //accumulate a queue longer than the cache it describes. Bounded to twice the cache
            //plus its capacity: every surviving key has at least one entry by then.
            while (_headOrder.Count > AvatarHeadCache.Count * 2 + MaxCachedHeadIcons)
            {
                _headOrder.Dequeue();
            }
        }

        public static Texture2D GenHeadTex(VMAvatar ava)
        {
            var m_Head = new SimAvatar(ava.Avatar); //talk about confusing...
            m_Head.StripAllButHead();


            var HeadCamera = new BasicCamera(GameFacade.GraphicsDevice, new Vector3(0.0f, 7.0f, -17.0f), Vector3.Zero, Vector3.Up);

            var pos2 = m_Head.Skeleton.GetBone("HEAD").AbsolutePosition;
            // a dog's head bone sits lower and the muzzle is longer, so the same offset framed
            // a person framed the dog from the nose up.
            pos2.Y += ava.IsDog ? .25f : 0.1f;
            HeadCamera.Position = new Vector3(0, pos2.Y, 12.5f);
            HeadCamera.FOV = (float)Math.PI / 3f;
            HeadCamera.Target = pos2;
            HeadCamera.ProjectionOrigin = new Vector2(74/2, 74/2);

            var HeadScene = new _3DTargetScene(GameFacade.GraphicsDevice, HeadCamera, new Point(74, 74), (GlobalSettings.Default.AntiAlias > 0) ? 8 : 0);
            HeadScene.ID = "UIPieMenuHead";

            m_Head.Scene = HeadScene;
            m_Head.Scale = new Vector3(1f);

            HeadCamera.Zoom = 13f;

            //rotate camera, similar to pie menu

            double xdir = Math.Atan(50 / 100.0);
            double ydir = Math.Atan(-50 / 100.0);

            Vector3 off = new Vector3(0, 0, 13.5f);
            Matrix mat = Microsoft.Xna.Framework.Matrix.CreateRotationY((float)xdir) * Microsoft.Xna.Framework.Matrix.CreateRotationX((float)ydir);

            HeadCamera.Position = new Vector3(0, pos2.Y, 0) + Vector3.Transform(off, mat);

            if (ava.IsPet)
            {
                HeadCamera.Zoom *= 1.5f;
            }
            //end rotate camera

            HeadScene.Initialize(GameFacade.Scenes);
            HeadScene.Add(m_Head);

            HeadScene.Draw(GameFacade.GraphicsDevice);
            var copy = TextureUtils.CopyAccelerated(GameFacade.GraphicsDevice, HeadScene.Target);
            HeadScene.Dispose();
            return copy;
        }
    }
}
