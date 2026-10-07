using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Common.Utils;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client
{
    public static class GameController
    {
        /// <summary>
        /// Whether <see cref="PublishNeighbourhoodInstallPath"/> has already run this process.
        /// </summary>
        private static bool neighbourhoodInstallPathPublished;

        /// <summary>
        /// Points the Neighbourhood Management page at the resolved The Sims install, so its
        /// "New (blank)" button has a source to copy the base template out of instead of having
        /// to resolve an install path of its own.
        ///
        /// <remarks>
        /// <para>
        /// The value is <c>Content.TS1BasePath</c>, which is <c>TS1HybridBasePath</c> - the folder
        /// <c>Program.cs</c> resolved the install into and set
        /// <c>Content.TS1HybridBasePath</c> from. That is the same folder the neighbourhood
        /// provider copies a missing slot from (see
        /// <c>TS1NeighborhoodProvider.InitSpecific</c>) and the same folder the lot templates are
        /// loaded out of, so the management page's seeds and the running game's data cannot come
        /// from two different installs.
        /// </para>
        /// <para>
        /// It is published here rather than in <c>SimitoneGame.Initialize</c> because that runs
        /// before the loading screen has called <c>Content.Init</c>, and <c>Content.Get()</c> is
        /// <c>null</c> until then. By the time <see cref="EnterGameMode"/> runs, content is up and
        /// the base path is settled. Guarded because <c>EnterGameMode</c> also runs on the way
        /// back from CAS, and the install cannot have changed in between.
        /// </para>
        /// <para>
        /// A <c>null</c> is a legitimate outcome - a build that never resolved an install leaves
        /// the management page's copy button greyed out with an explanation, rather than guessing
        /// a drive.
        /// </para>
        /// </remarks>
        /// </summary>
        private static void PublishNeighbourhoodInstallPath()
        {
            if (neighbourhoodInstallPathPublished) return;
            neighbourhoodInstallPathPublished = true;

            var install = FSO.Content.Content.Get()?.TS1BasePath;
            NeighbourhoodManager.DefaultInstallPath = install;
            SimitoneLog.Info("The neighbourhood management page will seed new neighbourhoods from "
                + (string.IsNullOrEmpty(install) ? "(no install path is known)" : "'" + install + "'") + ".");
        }

        public static void EnterLoading()
        {
            //The weather clips are Simitone's own content, not game data, and are decoded here -
            //once per process, on the game thread, with the mixer up - rather than in the game
            //class's LoadContent, which knows nothing about content folders. Decoding them before
            //the loading screen goes up means a storm is not waiting on a file read the first
            //time it is asked for.
            WeatherSounds.Load();

            var screen = new LoadingScreen();
            GameFacade.Screens.RemoveCurrent();
            GameFacade.Screens.AddScreen(screen);
        }

        /// <summary>
        /// The move-in request a lot name carries, if it carries one.
        /// </summary>
        /// <remarks>
        /// The protocol is a <c>!</c> followed by <c>n</c> (a normal move-in) or <c>m</c> (a
        /// move-in with magic), followed by the lot number: <c>"!n4"</c>.
        ///
        /// <para>
        /// <b>Every part of it is validated, because none of it was.</b> This runs inside a
        /// <c>GameThread.NextUpdate</c> callback with nothing catching, so a
        /// <see cref="FormatException"/> from parsing the number is process death rather than a
        /// wrong lot. A null name, a name too short to carry a number, a prefix that is not a
        /// move-in marker, a number that is not a number, and a number that is not a lot all
        /// answer <see cref="NeighSelectionMode.Normal"/> - which is what every caller that passes
        /// an ordinary lot path already gets.
        /// </para>
        /// </remarks>
        /// <param name="lotName">Whatever the caller passed, including null.</param>
        /// <param name="lotNumber">The requested lot number when the mode is not Normal.</param>
        /// <returns>
        /// The move-in mode to enter the game in, or <see cref="NeighSelectionMode.Normal"/> for
        /// an ordinary lot name or a request that could not be read.
        /// </returns>
        public static NeighSelectionMode ResolveEntryRequest(string lotName, out int lotNumber)
        {
            lotNumber = 0;
            if (string.IsNullOrEmpty(lotName) || lotName.Length < 3 || lotName[0] != '!') return NeighSelectionMode.Normal;

            var mode = NeighSelectionMode.Normal;
            switch (lotName[1])
            {
                case 'n': mode = NeighSelectionMode.MoveIn; break;
                case 'm': mode = NeighSelectionMode.MoveInMagic; break;
                default: return NeighSelectionMode.Normal;
            }

            var digits = lotName.Substring(2);
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out lotNumber)
                && lotNumber >= 0)
            {
                return mode;
            }

            // The prefix was a move-in but the rest was not usable. Say so and open the lot
            // normally rather than throwing on the game thread; the log is where a caller whose
            // name it mangled will find out.
            SimitoneLog.Warn("'" + lotName + "' is a move-in request, but '" + digits
                + "' is not a lot number, so the game is opening it as an ordinary lot.");
            lotNumber = 0;
            return NeighSelectionMode.Normal;
        }

        public static void EnterGameMode(string lotName, bool external)
        {
            GameThread.NextUpdate((x) =>
            {
                //content is up by now, so the management page's install path can be published
                //before anything can ask for it
                PublishNeighbourhoodInstallPath();

                var mode = ResolveEntryRequest(lotName, out var moveInFamily);
                var screen = new TS1GameScreen(mode);
                if (mode != NeighSelectionMode.Normal)
                {
                    screen.StartMoveIn(moveInFamily);
                }
                var last = GameFacade.Screens.CurrentUIScreen;
                GameFacade.Screens.RemoveCurrent();
                GameFacade.Screens.AddScreen(screen);

                var load = (last as LoadingScreen);

                if (load != null)
                {
                    load.Close();
                    var children = new List<UIElement>(last.GetChildren());
                    for (int i = 0; i < children.Count; i++)
                    {
                        last.Remove(children[i]);
                        screen.Add(children[i]);
                    }
                }
                screen.Initialize(lotName, external);
            });
        }

        /// <summary>
        /// Enters the Create-a-Sim screen on its "create a new family" start.
        /// </summary>
        public static void EnterCAS()
        {
            EnterCAS(null);
        }

        /// <summary>
        /// Enters the Create-a-Sim screen.
        /// </summary>
        /// <param name="editFamilyId">
        /// A FAMI ChunkID of an existing, already placed family to open in the family editor, or
        /// null for the normal "create a new family" flow. The CAS screen holds the id until its
        /// VM exists and then decodes the family into the editor, where Accept saves it back in
        /// place (rewriting the members' character IFFs and merging the family's GUID list)
        /// instead of creating a second copy of the family.
        /// </param>
        public static void EnterCAS(int? editFamilyId)
        {
            var screen = new TS1CASScreen(editFamilyId);
            var last = GameFacade.Screens.CurrentUIScreen;
            if (last is TS1GameScreen) ((TS1GameScreen)last).CleanupLastWorld();
            GameFacade.Screens.RemoveCurrent();
            GameFacade.Screens.AddScreen(screen);
        }
    }
}
