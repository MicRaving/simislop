using FSO.Client;
using FSO.Common;
using FSO.LotView;
using Simitone.Client;
using Simitone.Client.Utils;
using Simitone.Windows.GameLocator;
using Simitone.Windows.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Simitone.Windows
{
#if WINDOWS || LINUX
    /// <summary>
    /// The main class.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(baseDir);
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            OperatingSystem os = Environment.OSVersion;
            PlatformID pid = os.Platform;

            ILocator gameLocator;
            bool linux = pid == PlatformID.MacOSX || pid == PlatformID.Unix;
            if (linux && Directory.Exists("/Users"))
                gameLocator = new MacOSLocator();
            else if (linux)
                gameLocator = new LinuxLocator();
            else
                gameLocator = new WindowsLocator();

            var useDX = !linux;

            FSOEnvironment.Enable3D = false;
            bool ide = false;
            bool aa = false;
            bool jit = false;
            // Set by -path below. Kept separate from the resolved install because a command
            // line override is authoritative and is not allowed to be remembered.
            bool pathProvidedByCommandLine = false;
            string commandLinePath = null;

            // The settings singleton resolves its config path from FSOEnvironment.UserDir, which is
            // derived from My Documents. It must be set before anything below touches
            // GlobalSettings.Default - the -lang/-hz handlers do - or that first touch would create
            // and permanently use "Content/config.ini" instead of the user's own config file,
            // silently ignoring (and later overwriting nothing of) the real user settings.
            FSOEnvironment.UserDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Simitone/").Replace('\\', '/');
            Directory.CreateDirectory(FSOEnvironment.UserDir);

            // Probe for a pre-existing config *before* anything can touch GlobalSettings.Default.
            // FreeSO's IniConfig.Load() creates config.ini the moment it is asked for a
            // singleton against a missing file, so a probe taken after the -lang/-hz handlers
            // below would always answer "yes, a config exists" and the first-run defaults
            // would never be applied.
            var hadConfigFile = File.Exists(StartupSettings.ConfigFilePath(FSOEnvironment.UserDir));

            #region User resolution parmeters

            FSOEnvironment.Args = string.Join(" ", args);

            foreach (var arg in args)
            {
                // arg.Length: an empty argument ("") would otherwise throw IndexOutOfRange here,
                // before the unhandled-exception logger is registered, so it would die silently.
                if (arg.Length > 0 && arg[0] == '-')
                {
                    var cmd = arg.Substring(1);
                    // TryParse: a malformed -lang/-hz value is ignored rather than crashing startup
                    // before anything is logged.
                    if (cmd.StartsWith("lang"))
                    {
                        if (byte.TryParse(cmd.Substring(4), out var langCode)) GlobalSettings.Default.LanguageCode = langCode;
                    }
                    else if (cmd.StartsWith("hz"))
                    {
                        if (int.TryParse(cmd.Substring(2), out var hz)) GlobalSettings.Default.TargetRefreshRate = hz;
                    }
                    else
                    {
                        //normal style param
                        switch (cmd)
                        {
                            case "ide":
                                ide = true;
                                break;
                            case "3d":
                                FSOEnvironment.Enable3D = true;
                                break;
                            case "aa":
                                aa = true;
                                break;
                            case "jit":
                                jit = true;
                                break;
                            case "dx":
                            case "dx11":
                                useDX = true;
                                break;
                            case "gl":
                            case "ogl":
                                useDX = false;
                                break;
                            case "touch":
                                FSOEnvironment.SoftwareKeyboard = true;
                                break;
                            case "nosound":
                                FSOEnvironment.NoSound = true;
                                break;
                            case string s when s.StartsWith("path"): //The Sims path
                                {
                                    // -path is a per-run instruction, not a preference: it is
                                    // authoritative, and it is never recorded as the install
                                    // choice (see ResolveGameInstall). An empty value is
                                    // ignored, so a later bare "-path" cannot undo an earlier
                                    // good one.
                                    var fromCommandLine = GamePathArgument.Normalize(s.Length > 4 ? s.Substring(4) : null);
                                    if (fromCommandLine != null)
                                    {
                                        commandLinePath = fromCommandLine;
                                        pathProvidedByCommandLine = true;
                                    }
                                    break;
                                }
                        }
                    }
                }
            }
            #endregion

            // User-facing graphics preferences. These used to be stamped unconditionally, which
            // meant anything the in-game Settings panel saved was thrown away on the next launch.
            // They are now seeded only on a genuine first run; afterwards the player's saved values
            // win. -aa still forces antialiasing on regardless, because an explicit command line
            // flag outranks a stored preference.
            //
            // ORDERING IS THE POINT, and it is the one thing about this block that is not
            // self-evident. GlobalSettings.Default.Save() rewrites the whole file, and the only
            // startup save is RememberInstall's - which ResolveGameInstall calls *below*. The
            // stamp used to sit further down, after the install was resolved, so on the common
            // first run (exactly one install detected, so the choice is made silently) the file
            // was written with FreeSO's ini defaults and the stamp only ever reached memory: the
            // next launch saw a config that existed, skipped the stamp, and kept the ini's
            // ComplexShaders=false and LightingMode=-1. Nothing below may save the settings
            // object before this line, and nothing after it should need to: the stamp is in
            // memory for the whole run. The values themselves live on
            // StartupSettings.ApplyGraphicsDefaults, which is where they are tested.
            StartupSettings.ApplyGraphicsDefaults(GlobalSettings.Default, hadConfigFile, aa);

            // Which The Sims to boot on. -path wins outright; otherwise Windows discovery may
            // put a question in front of the player (more than one install on this machine).
            // Resolved here, immediately after the arguments, so that everything below -
            // the sentinel check, the neighbourhood pre-flight, the engine's hybrid content
            // root - works on the final answer instead of on a provisional guess.
            var install = ResolveGameInstall(gameLocator, pathProvidedByCommandLine, commandLinePath);
            var path = install.Path;
            var isSteamInstall = install.IsSteam;

            useDX = MonogameLinker.Link(useDX);

            FSO.Files.ImageLoaderHelpers.BitmapFunction = Simitone.Client.Utils.BitmapDecoder.Read;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            FSOEnvironment.SoftwareDepth = false;
            FSOEnvironment.UseMRT = true;

            if (path != null && !WindowsLocator.IsSimsInstallDir(path))
            {
                // Everything that produces a path either validated it against this sentinel
                // already (discovery, the remembered choice, a browsed folder) or is allowed
                // to name something unchecked (a -path the player typed). Either way this is
                // the last gate before the game boots on it.
                path = null;
                isSteamInstall = false;
            }

            // Pre-flight for the neighbourhood bootstrap, before any of the game is constructed.
            // On first run TS1NeighbourhoodProvider seeds Simitone's own copy of a neighbourhood
            // by copying a source folder out of the install and then opening Neighborhood.iff out
            // of the copy. The Sims never creates UserData during installation - the game writes it
            // the first time it is launched and a neighbourhood is loaded - so a never-launched
            // install of any edition has no UserData at all. The engine now falls back to the
            // TemplateUserData the install ships (see TS1NeighbourProvider.ResolveBootstrapSource),
            // which is why a virgin install is allowed to boot here: there is something to copy,
            // and the copy is a complete (if empty) neighbourhood.
            //
            // So this is no longer a refusal, only a check. It still exits in the one case it
            // cannot help with: an install that has neither a UserData nor a TemplateUserData is
            // not a The Sims installation we can build a neighbourhood out of, and saying so is
            // better than letting the provider throw a path-only DirectoryNotFoundException from
            // inside content init.
            //
            // Two sources, because the two locations are not exclusive. The Steam/EA Legacy
            // Collection keeps its playable saves outside the game folder entirely, and the engine
            // sources the copy from Saved Games for it (which is what Content.TS1SteamInstall
            // carries), while the Template* folders are shipped game data and always sit next to
            // the executable. A Legacy Collection player who has never launched the game therefore
            // has only the template, and a retail player has only UserData.
            var savesSource = path == null ? null : TS1InstallSelection.BootstrapSourceDirectory(path, isSteamInstall, UserProfile());
            if (path != null
                && !TS1UserData.IsBootstrappable(path)
                && !TS1UserData.HasNeighborhood(savesSource))
            {
                var installSource = TS1UserData.InstallNeighborhoodPath(path);

                SimitoneLog.Error(
                    "The Sims installation at '" + path + "' has no " + TS1UserData.NeighborhoodResourceName +
                    " in '" + installSource + "' or in the " + TS1UserData.BaseTemplateFolderName +
                    " folder beside it, so there is no neighbourhood to seed the Simitone copy from.");

                MessageBox.Show(
                    "Could not find a The Sims neighbourhood for Simitone to copy:\n\n" +
                    installSource + "\\" + TS1UserData.NeighborhoodResourceName + "\n\n" +
                    "and neither did it ship the folder it would fall back on:\n\n" +
                    TS1UserData.InstallTemplatePath(path) + "\\" + TS1UserData.NeighborhoodResourceName + "\n\n" +
                    (isSteamInstall
                        ? "The Sims: Legacy Collection (Steam/EA) also keeps its real saves out of the game\n" +
                          "folder, in:\n\n" +
                          TS1UserData.SteamSavesRoot(UserProfile()) + "\n\n"
                        : "") +
                    "A complete The Sims installation ships at least the " + TS1UserData.BaseTemplateFolderName +
                    " folder,\nso this folder does not look like a full installation. Check that the" +
                    " game files are installed.\n\n" +
                    "Please launch The Sims, start a neighbourhood, then restart Simitone.\n" +
                    "If you play another edition, point Simitone at it with -path:\"C:\\Path\\To\\The Sims\".",
                    "Simitone - The Sims save data not found",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }

            if (path != null)
            {
                FSOEnvironment.ContentDir = "Content/";
                FSOEnvironment.GFXContentDir = "Content/" + (useDX ? "DX/" : "OGL/");
                FSOEnvironment.Linux = false;
                FSOEnvironment.DirectX = useDX;
                FSOEnvironment.GameThread = Thread.CurrentThread;
                if (GlobalSettings.Default.LanguageCode == 0) GlobalSettings.Default.LanguageCode = 1;
                FSO.Files.Formats.IFF.Chunks.STR.DefaultLangCode = (FSO.Files.Formats.IFF.Chunks.STRLangCode)GlobalSettings.Default.LanguageCode;

                // Required for the game to run at all - always written, config or not.
                GlobalSettings.Default.StartupPath = path;
                GlobalSettings.Default.TS1HybridEnable = true;
                GlobalSettings.Default.TS1HybridPath = path;
                GlobalSettings.Default.TS1IsSteamInstall = isSteamInstall;
                GlobalSettings.Default.ClientVersion = "0";

                // The engine's copy of the same fact, and the one that decides where the first
                // neighbourhood is seeded from: TS1NeighbourhoodProvider reads Saved Games for
                // a Legacy Collection install and the install directory for every other, falling
                // back to the install's own TemplateUserData when neither has a neighbourhood yet.
                // Set before any content init happens, which is the whole point of settling the
                // installation first.
                FSO.Content.Content.TS1SteamInstall = isSteamInstall;

                // User-facing graphics preferences were stamped above, before the install was
                // resolved and therefore before anything could save. See the ordering note there:
                // this is the only place startup saves, and the first-run graphics defaults have
                // to be in memory before it is reached or they never reach disk at all.

                if (ide) new FSO.IDE.VolcanicStartProxy().InitVolcanic(args);

                var assemblies = new FSO.SimAntics.JIT.Runtime.AssemblyStore();
                //var globals = new TS1.Scripts.Dummy(); //make sure scripts assembly is loaded
                if (jit) assemblies.InitAOT();
                FSO.SimAntics.Engine.VMTranslator.INSTANCE = new FSO.SimAntics.JIT.Runtime.VMAOTTranslator(assemblies);

                var start = new GameStartProxy();
                start.Start(useDX);
            }
            else
            {
                // Reached when a -path named something that is not a Sims install (the
                // sentinel check above cleared it), or if resolution failed to produce a path.
                // Discovery's own "nothing found" case never gets this far: it asks the player
                // to point at the folder first, so there is no need for it to arrive here.
                MessageBox.Show(
                    "Could not find a The Sims installation.\n\nInstall The Sims: Complete Collection (or Legacy Collection via Steam/GOG), place it next to Simitone in a \"The Sims\" folder, or pass -path:\"C:\\Path\\To\\The Sims\" explicitly.",
                    "Simitone - The Sims data not found",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// The user's profile directory, which is where The Sims: Legacy Collection keeps its
        /// save data. Read through here so the several places that need it agree, and so a
        /// null profile (a broken environment) can only be a null that the callers already
        /// handle.
        /// </summary>
        private static string UserProfile()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>
        /// A The Sims installation startup settled on: the directory to boot, and whether its
        /// save data lives in Saved Games rather than next to the game.
        /// </summary>
        private struct GameInstall
        {
            /// <summary>Install directory, forward slashes, trailing slash. Null only on a
            /// path that did not survive the sentinel check.</summary>
            public string Path;

            /// <summary>
            /// The Sims: Legacy Collection (Steam/EA), which keeps <c>UserData</c> under
            /// Saved Games. Everything else keeps it next to the game.
            /// </summary>
            public bool IsSteam;
        }

        /// <summary>
        /// Works out which The Sims to boot on, asking the player only when there is a genuine
        /// choice to be made.
        ///
        /// <para>
        /// The order of authority is: <c>-path</c>, then a previously configured installation
        /// that is still there, then discovery. Discovery is silent when it finds exactly one
        /// install (the overwhelmingly common case, and one where any question would have an
        /// obvious answer) and puts a picker in front of the player when it finds more than
        /// one, because the editions are not interchangeable. Nothing was found and nothing was
        /// remembered falls through to a folder browser, which is the only "no install" case
        /// where the player can do something about it.
        /// </para>
        ///
        /// <para>
        /// The rules themselves are in <see cref="TS1InstallSelection.Resolve"/>, which does no
        /// IO and is unit tested; this method is the part that needs a window.
        /// </para>
        /// </summary>
        private static GameInstall ResolveGameInstall(ILocator gameLocator, bool pathProvidedByCommandLine, string commandLinePath)
        {
            if (pathProvidedByCommandLine)
            {
                SimitoneLog.Info("Startup: -path override in use, '" + commandLinePath + "'.");
                return new GameInstall
                {
                    Path = commandLinePath,
                    // Nothing was discovered, so there is no roster entry to take the edition
                    // from. Fall back to the shape of the path: a Steam library always keeps
                    // games under <library>/steamapps/common.
                    IsSteam = TS1InstallSelection.IsSteamInstall(commandLinePath, discoveredAsSteam: false, pathCameFromThePlayer: true)
                };
            }

            if (!(gameLocator is WindowsLocator windowsLocator))
            {
                // The macOS and Linux locators have a single conventional location and no
                // registry to read, so there is nothing to choose between and nothing worth
                // remembering. They also cannot be asked, and must not start being.
                return new GameInstall { Path = gameLocator.FindTheSims1() };
            }

            var remembered = StoredInstallPath();
            if (GlobalSettings.Default.TS1InstallationConfigured && remembered == null)
            {
                // Only worth saying out loud: an unconfigured player has no stored path to have
                // gone missing, and the ini default is not an install on anybody's machine.
                SimitoneLog.Warn("Startup: the configured install '" + GlobalSettings.Default.TS1HybridPath +
                    "' is no longer a Sims install.");
            }

            // Logged separately from the decision, because a settled decision carries no
            // roster: the two numbers answer different questions ("what is on this machine?"
            // and "what are we doing about it?") and a remembered path hides the first one.
            var candidates = windowsLocator.FindAllTheSims1Installations();
            var decision = TS1InstallSelection.Resolve(
                pathProvidedByCommandLine: false,
                commandLinePath: null,
                wasConfigured: TS1InstallSelection.IsAlreadyConfigured(GlobalSettings.Default.TS1InstallationConfigured, remembered),
                rememberedPath: remembered,
                candidates: candidates);

            SimitoneLog.Info("Startup: " + candidates.Count + " The Sims installation(s) found" +
                Describe(candidates) + "; decision is " + decision.Kind +
                (decision.Path == null ? "." : ", booting '" + decision.Path + "'."));

            switch (decision.Kind)
            {
                case SimsInstallDecisionKind.RememberedPath:
                    // The stored edition flag travels with the stored path: both were written
                    // together when the player chose, and only this pair is authoritative.
                    return new GameInstall { Path = decision.Path, IsSteam = GlobalSettings.Default.TS1IsSteamInstall };

                case SimsInstallDecisionKind.SingleCandidate:
                    // Nothing to ask, but worth remembering - this is the first run of a player
                    // who has one install, and every later launch should be silent.
                    return Settle(decision.Candidates[0], confirm: false);

                case SimsInstallDecisionKind.MultipleCandidates:
                case SimsInstallDecisionKind.StaleRememberedPath:
                    // The stored path is the caller's to name: it knows the value, and only the
                    // caller knows whether it survived validation.
                    return AskWhichInstall(decision,
                        decision.Kind == SimsInstallDecisionKind.StaleRememberedPath ? GlobalSettings.Default.TS1HybridPath : null);

                default:
                    return LocateInstallByHand(decision);
            }
        }

        /// <summary>
        /// The previously configured install directory, but only if it is still a Sims
        /// install; null otherwise.
        /// </summary>
        /// <remarks>
        /// <c>TS1HybridPath</c> doubles as the stored choice: it is the key the game already
        /// boots the hybrid content root from, and it is written by
        /// <see cref="RememberInstall"/> the moment a choice is made, so no second key is
        /// needed. Its ini default is "D:/Games/The Sims/", which is not an install on any
        /// machine, so the sentinel check doubles as the "was one ever chosen?" test and a
        /// player who has never configured anything gets null rather than a false claim.
        /// </remarks>
        private static string StoredInstallPath()
        {
            var stored = GlobalSettings.Default.TS1HybridPath;
            return WindowsLocator.IsSimsInstallDir(stored) ? stored : null;
        }

        /// <summary>
        /// Renders a discovery roster for the log, one entry per candidate. The log is the only
        /// record of what was on the machine when a choice was made, and "the selector
        /// appeared" is not something that can be diagnosed later without knowing what it was
        /// offering.
        /// </summary>
        private static string Describe(IReadOnlyList<SimsInstallCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return "";

            var rendered = new System.Text.StringBuilder();
            foreach (var candidate in candidates)
            {
                rendered.Append(" [").Append(candidate.Kind).Append(' ').Append(candidate.Path).Append(']');
            }
            return rendered.ToString();
        }

        /// <summary>
        /// Records the install Simitone settled on so later launches boot on it without asking.
        /// </summary>
        /// <remarks>
        /// Called only when a choice has actually been made - a single detected install, a
        /// picked one, or a folder the player pointed at. Deliberately not called for a
        /// <c>-path</c> run: a command line override is an instruction for that run, and
        /// recording it would silently redefine the installation for every later normal launch.
        ///
        /// <c>GlobalSettings.Default.Save()</c> rewrites the whole file, so this is the only
        /// place startup saves. The rest of the startup sequence must not start calling it: the
        /// first-run graphics defaults rely on config.ini not being rewritten under them, and the
        /// stamp is therefore done in <see cref="StartupSettings.ApplyGraphicsDefaults"/> before
        /// <c>Main</c> ever gets here - <c>ResolveGameInstall</c>, which calls this through
        /// <c>Settle</c>, is what settles the install, and it runs first. Anything added between
        /// the two would have to move.
        /// </remarks>
        private static void RememberInstall(SimsInstallCandidate install)
        {
            GlobalSettings.Default.TS1HybridPath = install.Path;
            GlobalSettings.Default.TS1IsSteamInstall = install.IsSteam;
            GlobalSettings.Default.TS1InstallationConfigured = true;
            GlobalSettings.Default.Save();
        }

        /// <summary>
        /// Finishes a decision that has produced a concrete install: remembers it, and tells
        /// the player what it resolved to when they were the ones who decided.
        /// </summary>
        private static GameInstall Settle(SimsInstallCandidate install, bool confirm)
        {
            RememberInstall(install);
            if (confirm) ShowInstallConfirmation(install);
            return new GameInstall { Path = install.Path, IsSteam = install.IsSteam };
        }

        /// <summary>
        /// Asks which of several installs to use, and what to do about a previous choice whose
        /// directory is no longer there.
        /// </summary>
        /// <param name="decision">What <see cref="TS1InstallSelection.Resolve"/> settled on.</param>
        /// <param name="stalePath">
        /// The previously configured directory when it is the reason for asking again, else
        /// null. Only meaningful to name it in the message; it is never a boot path.
        /// </param>
        private static GameInstall AskWhichInstall(SimsInstallDecision decision, string stalePath)
        {
            if (!string.IsNullOrEmpty(stalePath))
            {
                MessageBox.Show(
                    "The The Sims folder Simitone was set up with is no longer there:\n\n" +
                    stalePath + "\n\n" +
                    "Pick the install to use instead. If you have several, choose the one you play - " +
                    "their save files are not interchangeable.",
                    "Simitone - installation not found",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            using (var selector = new InstallationSelector(decision.Candidates))
            {
                if (selector.ShowDialog() == DialogResult.OK && selector.SelectedInstallation != null)
                {
                    SimitoneLog.Info("Startup: the player chose '" + selector.SelectedInstallation.Path + "' (" +
                        selector.SelectedInstallation.Kind + ").");
                    return Settle(selector.SelectedInstallation, confirm: true);
                }
            }

            // Bailing out is the only other option: a startup that silently picked something
            // after the player declined to pick would defeat the question.
            SimitoneLog.Info("Startup: installation selection was cancelled.");
            Environment.Exit(1);
            return default;
        }

        /// <summary>
        /// Nothing was found and nothing was remembered: say so, and offer to let the player
        /// point at the install, which is the one thing that can actually fix it.
        /// </summary>
        private static GameInstall LocateInstallByHand(SimsInstallDecision decision)
        {
            SimitoneLog.Warn("Startup: no The Sims installation was found (" + decision.Kind + ").");

            var locate = MessageBox.Show(
                "Simitone could not find your copy of The Sims.\n\n" +
                "It looks for The Sims: Complete Collection, The Sims: Legacy Collection (Steam or GOG), " +
                "and a \"The Sims\" folder placed next to Simitone.\n\n" +
                "Do you want to point Simitone at your The Sims folder now?",
                "Simitone - The Sims not found",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (locate != DialogResult.Yes)
            {
                Environment.Exit(1);
                return default;
            }

            using (var browser = new FolderBrowserDialog())
            {
                browser.Description = "Choose your The Sims folder (the one that contains GameData)";
                browser.UseDescriptionForTitle = true;
                browser.ShowNewFolderButton = false;
                if (browser.ShowDialog() != DialogResult.OK)
                {
                    Environment.Exit(1);
                    return default;
                }

                // Normalised to the form everything downstream expects, and validated against
                // the same sentinel: a folder that cannot boot is worth saying out loud here
                // rather than falling through to the generic "data not found" message.
                var picked = GamePathArgument.Normalize(browser.SelectedPath);
                if (!WindowsLocator.IsSimsInstallDir(picked))
                {
                    MessageBox.Show(
                        "That folder does not look like a The Sims installation:\n\n" +
                        picked + "\\GameData\\Behavior.iff\n\n" +
                        "was not found there. Choose the folder that contains GameData, and try again.",
                        "Simitone - not a The Sims folder",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Environment.Exit(1);
                    return default;
                }

                // The edition of a hand-picked folder is inferred rather than declared: a
                // Steam library path is the only layout the Legacy Collection is installed
                // into, and getting it right is what keeps Simitone from looking for saves
                // next to the game when they are in Saved Games.
                var chosen = new SimsInstallCandidate(
                    picked,
                    SimsInstallCandidate.LooksLikeSteamLibraryPath(picked) ? SimsInstallKind.Steam : SimsInstallKind.Unknown);
                return Settle(chosen, confirm: true);
            }
        }

        /// <summary>
        /// Reports the resolved install, and the two directories that follow from it. Shown only
        /// after the player chose something - see <see cref="InstallationInfoDialog"/> for why
        /// it is not shown when the game already knew the answer.
        /// </summary>
        private static void ShowInstallConfirmation(SimsInstallCandidate install)
        {
            using (var dialog = new InstallationInfoDialog(
                install.Description,
                install.Path,
                TS1InstallSelection.BootstrapSourceDirectory(install.Path, install.IsSteam, UserProfile()),
                TS1UserData.InstallNeighborhoodPath(FSOEnvironment.UserDir)))
            {
                dialog.ShowDialog();
            }
        }

        private static System.Reflection.Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = args.Name;
                if (name.StartsWith("FSO.Scripts"))
                {
                    //Script assemblies are generated at runtime and never exist on disk,
                    //so they must never fall through to a LoadFrom attempt.
                    return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.FullName == name);
                }

                //Prefer an assembly that is already loaded, otherwise LoadFrom can produce a
                //second copy of the same identity in the process.
                var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.FullName == name);
                if (alreadyLoaded != null) return alreadyLoaded;

                //args.Name is usually "Simple.Name, Version=..., Culture=..., PublicKeyToken=...",
                //but a bare name with no comma is legal - never index into a missing comma.
                var comma = name.IndexOf(',');
                var simpleName = (comma >= 0 ? name.Substring(0, comma) : name).Trim();
                if (simpleName.Length == 0) return null;

                var assemblyPath = Path.Combine(MonogameLinker.AssemblyDir, simpleName + ".dll");
                if (!File.Exists(assemblyPath)) return null;

                return Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception)
            {
                //Never propagate out of an AssemblyResolve handler: returning null lets the
                //runtime report the original load failure instead of a confusing nested one.
                return null;
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject;
            // Record the crash before showing the modal dialog: the dialog may be dismissed without
            // being copied anywhere, so the log is the only durable record of what went wrong.
            SimitoneLog.Error("Unhandled exception: " + (exception == null ? "(null)" : exception.ToString()));
            var text = exception == null ? "(null)" : exception.ToString();
            if (exception is OutOfMemoryException)
            {
                MessageBox.Show(text, "Out of Memory! Simitone needs to close.");
            }
            else
            {
                MessageBox.Show(text, "A fatal error occurred! Screenshot this dialog and report it.");
            }
        }
    }
#endif
}
