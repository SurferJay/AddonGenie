// Dependency Viewer: an AddonGenie addon. Place in Binaries\Win64\Addons\
// Shows what the selected package uses from other packages, where those packages live, and which mod ships them.
// Editor access and styling come from AddonGenie (Genie.*); the audit and folder rules are this addon's own.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AddonGenie;

namespace DependencyViewer {
    public sealed class Addon : IGenieAddon {
        private GenieWindowSlot _window, _folders, _about;

        public string Name {
            get { return "Dependency Viewer"; }
        }
        public string Tooltip {
            get { return "See what the selected package uses"; }
        }
        public string Version {
            get { return "1.0"; }
        }
        public string Author {
            get { return "SurferJay"; }
        }

        public void Initialize() {
            _window  = new GenieWindowSlot(CreateWindow);
            _folders = new GenieWindowSlot(CreateFoldersWindow);
            _about   = new GenieWindowSlot(CreateAboutWindow);
        }

        public void Invoke() {
            _window.Show();
        }

        // The addon info keeps its default corner, bottom-left, over the list box's corner as in Package Loader.
        private GenieWindow CreateWindow() {
            GenieWindow w = Genie.WindowUtils.CreateWindow("Dependency Viewer", 920, 600);
            w.MinWidth    = 640;
            w.MinHeight   = 360;
            // Both views in one window, one shown at a time. The mod view goes back on its own (ModPanel).
            Grid                        views   = new Grid();
            PackagePanel                package = null;
            ModPanel                    mod     = null;
            Action<ModFinder.Mod, bool> open    = delegate(ModFinder.Mod m, bool pinned) {
                package.Visibility = Visibility.Collapsed;
                mod.Open(m, pinned);
            };
            package = new PackagePanel(w, _folders.Show, _about.Show, open);
            mod = new ModPanel(w, _folders.Show, _about.Show, delegate { package.Visibility = Visibility.Visible; });
            views.Children.Add(package);
            views.Children.Add(mod);
            w.Content = views;
            return w;
        }

        private GenieWindow CreateFoldersWindow() {
            GenieWindow w   = Genie.WindowUtils.CreateWindow("Source Folders", 832, 560);
            w.MinWidth      = 560;
            w.MinHeight     = 360;
            w.ShowAddonInfo = false; // the main window carries it
            w.Content       = new SourceFoldersPanel(w, _about.Show);
            return w;
        }

        private GenieWindow CreateAboutWindow() {
            GenieWindow w   = Genie.WindowUtils.CreateWindow("About Sources", 728, 680);
            w.MinWidth      = 480;
            w.MinHeight     = 360;
            w.ShowAddonInfo = false; // the main window carries it
            w.Content       = new AboutSourcesPanel(w);
            return w;
        }
    }

    // == Folder Settings ==============================================================================
    // Where each kind of location is. Workshop, ModBuddy and Local Mod can be set (Source Folders); the rest follow
    // from the SDK's own place. ModBuddy and Local Mod can also hold more folders: other Windows users'
    // ModBuddy projects (found once, then the user's to edit), another game install. Saved in this addon's settings,
    // section Folders. A folder left to detection is saved as <auto-detect>, so the file says what happens; an empty
    // list's key is left out.

    internal static class FolderSettings {
        public sealed class Source {
            public string Name; // the source tag, MaxNameLength at most
            public string Path;
        }

        public const int MaxNameLength = 9;

        private const string Section   = "Folders";
        private const string CustomKey = "Custom";              // "Name|Path|Name|Path": '|' can't appear in either
        private const string ExtraKey  = "Extra";               // "<Kind>Extra": "Path|Path"
        private const string SeededKey = "ModBuddyExtraSeeded"; // other users' folders already added once

        // Can't be a folder: Windows paths can't hold < or >. A missing key or an empty value (older saves) means the
        // same.
        private const string AutoDetect = "<auto-detect>";

        // Source Folders order: where you work first.
        public static readonly LocationKind[] Settable = {
            LocationKind.ModBuddy,
            LocationKind.Workshop,
            LocationKind.LocalMod,
        };

        /// <summary>Kinds that can hold more folders than the one set.</summary>
        public static readonly LocationKind[] Expandable = {
            LocationKind.ModBuddy,
            LocationKind.LocalMod,
        };

        /// <summary>Raised after a save, once Locations reflects it.</summary>
        public static event Action Changed;

        // steamapps\common and steamapps, above the SDK's folder.
        private static readonly string Common = Parent(Genie.GetSdkFolder());
        private static readonly string Steam  = Parent(Common);

        public static readonly string[] StockRoots = {
            Path.Combine(Genie.GetSdkFolder(), @"XComGame\Content"),
            Path.Combine(Genie.GetSdkFolder(), @"Engine\Content"),
        };
        /// <summary>The folder found without settings, or null.</summary>
        public static string Detected(LocationKind kind) {
            switch (kind) {
                case LocationKind.Workshop:
                    return Steam == null ? null : Path.Combine(Steam, @"workshop\content\268500");
                case LocationKind.ModBuddy:
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                        "Firaxis ModBuddy");
                case LocationKind.SdkMod:
                    return Path.Combine(Genie.GetSdkFolder(), @"XComGame\Content\Mods");
                case LocationKind.Artifact:
                    return Path.Combine(Genie.GetSdkFolder(), @"XComGame\Mods");
                case LocationKind.LocalMod:
                    return Common == null ? null : Path.Combine(Common, @"XCOM 2\XCom2-WarOfTheChosen\XComGame\Mods");
                case LocationKind.OldSdk:
                    return Common == null ? null : Path.Combine(Common, "XCOM 2 SDK");
                default:
                    return null;
            }
        }

        /// <summary>The folder in use: the one set, else the detected one.</summary>
        public static string Get(LocationKind kind) {
            if (Array.IndexOf(Settable, kind) < 0) return Detected(kind); // ignores a value saved while it was settable
            string set = Genie.ConfigUtils.Get(kind.ToString(), "", Section);
            bool auto  = set.Length == 0 || string.Equals(set, AutoDetect, StringComparison.OrdinalIgnoreCase);
            return auto ? Detected(kind) : set;
        }

        /// <summary>
        /// The kind's saved other folders, never the folder Get returns, never twice. The first time ModBuddy's are
        /// asked for, every other Windows user's ModBuddy folder is added and saved.
        /// </summary>
        public static List<string> GetExtra(LocationKind kind) {
            if (kind == LocationKind.ModBuddy) SeedModBuddy();
            List<string> extra = new List<string>();
            string       own   = Get(kind);
            foreach (string folder in Genie.ConfigUtils.GetList(kind + ExtraKey, Section)) {
                if (!SameFolder(folder, own)) AddOnce(extra, folder);
            }
            return extra;
        }

        /// <summary>The other folders found without settings: other users' ModBuddy folders, else none.</summary>
        public static List<string> DetectedExtra(LocationKind kind) {
            return kind == LocationKind.ModBuddy ? OtherUsersModBuddy() : new List<string>();
        }

        public static List<Source> GetCustom() {
            // Name, path, name, path...
            List<Source> sources = new List<Source>();
            List<string> parts   = Genie.ConfigUtils.GetList(CustomKey, Section);
            for (int i = 0; i + 1 < parts.Count; i += 2)
                sources.Add(new Source { Name = parts[i], Path = parts[i + 1] });
            return sources;
        }

        /// <summary>Saves an edit: its folders (empty or detected: auto-detect), extra folders and custom
        /// sources.</summary>
        public static void Save(Edit edit) {
            foreach (KeyValuePair<LocationKind, string> f in edit.Folders) Store(f.Key, f.Value);
            foreach (KeyValuePair<LocationKind, List<string>> e in edit.Extra) WriteList(e.Key + ExtraKey, e.Value);
            List<string> parts = new List<string>();
            foreach (Source c in edit.Custom) {
                parts.Add(c.Name);
                parts.Add(c.Path);
            }
            Genie.ConfigUtils.SetList(CustomKey, parts, Section);
            _pending = null;
            Apply();
        }

        // == Unsaved edits ============================================================================
        // Source Folders shares what it holds before Save, so About Sources can show it. Locations never reads this:
        // tags follow saved settings only.

        /// <summary>The folders as Source Folders holds them. Folders: an empty one means detect.</summary>
        public sealed class Edit {
            public Dictionary<LocationKind, string>       Folders = new Dictionary<LocationKind, string>();
            public Dictionary<LocationKind, List<string>> Extra   = new Dictionary<LocationKind, List<string>>();
            public List<Source>                           Custom  = new List<Source>();
        }

        private static Edit _pending;

        /// <summary>Raised when the unsaved edit changes, appears or goes away.</summary>
        public static event Action PendingChanged;

        /// <summary>Shares the dialog's edit; null, or one matching the saved settings, drops it.</summary>
        public static void SetPending(Edit edit) {
            Edit next = (edit == null || Matches(edit)) ? null : edit;
            if (next == null && _pending == null) return;
            _pending = next;
            if (PendingChanged != null) PendingChanged();
        }

        /// <summary>True if the unsaved edit adds this folder: it isn't one of the kind's saved folders.</summary>
        public static bool IsUnsaved(LocationKind kind, string folder) {
            if (_pending == null || Array.IndexOf(Settable, kind) < 0) return false; // only Source Folders edits these
            return !SameFolder(folder, Get(kind)) && !GetExtra(kind).Exists(f => SameFolder(f, folder));
        }

        /// <summary>True if this custom source is new or changed in the unsaved edit.</summary>
        public static bool IsUnsaved(Source c) {
            return _pending != null && !GetCustom().Exists(s => s.Name == c.Name && SameFolder(s.Path, c.Path));
        }

        /// <summary>A kind's folder, from the unsaved edit if there is one.</summary>
        public static string Shown(LocationKind kind) {
            string folder;
            if (_pending == null || !_pending.Folders.TryGetValue(kind, out folder)) return Get(kind);
            return folder.Length > 0 ? folder : Detected(kind);
        }

        /// <summary>A kind's extra folders, from the unsaved edit if there is one.</summary>
        public static List<string> ShownExtra(LocationKind kind) {
            List<string> extra;
            if (_pending == null || !_pending.Extra.TryGetValue(kind, out extra)) return GetExtra(kind);
            return extra;
        }

        /// <summary>The custom sources, from the unsaved edit if there is one.</summary>
        public static List<Source> ShownCustom() {
            return _pending != null ? _pending.Custom : GetCustom();
        }

        private static bool SameFolders(List<string> a, List<string> b) {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) {
                if (!SameFolder(a[i], b[i])) return false;
            }
            return true;
        }

        // Same folders, extra folders and custom sources as saved, compared as Save would store them.
        private static bool Matches(Edit edit) {
            foreach (KeyValuePair<LocationKind, string> f in edit.Folders) {
                string folder = f.Value.Length > 0 ? f.Value : Detected(f.Key);
                if (!SameFolder(folder, Get(f.Key))) return false;
            }
            foreach (KeyValuePair<LocationKind, List<string>> e in edit.Extra) {
                if (!SameFolders(e.Value, GetExtra(e.Key))) return false;
            }
            List<Source> custom = GetCustom();
            if (custom.Count != edit.Custom.Count) return false;
            for (int i = 0; i < custom.Count; i++) {
                if (custom[i].Name != edit.Custom[i].Name || !SameFolder(custom[i].Path, edit.Custom[i].Path))
                    return false;
            }
            return true;
        }

        // Once per settings file: after this the list is the user's, so a folder they removed stays removed. Reset
        // all in Source Folders runs the detection again (DetectedExtra).
        private static void SeedModBuddy() {
            if (Genie.ConfigUtils.GetBool(SeededKey, false, Section)) return;
            List<string> folders = Genie.ConfigUtils.GetList(LocationKind.ModBuddy + ExtraKey, Section);
            folders.AddRange(OtherUsersModBuddy());
            WriteList(LocationKind.ModBuddy + ExtraKey, folders);
            Genie.ConfigUtils.SetBool(SeededKey, true, Section);
        }

        // Every other profile's Documents\Firaxis ModBuddy, also under OneDrive, which can take over Documents. A
        // profile this user can't read reads as having none.
        private static List<string> OtherUsersModBuddy() {
            List<string> found = new List<string>();
            string       me    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string       users = Parent(me);
            if (users == null) return found;
            string[] profiles;
            try {
                profiles = Directory.GetDirectories(users);
            } catch (Exception) {
                return found;
            }
            foreach (string profile in profiles) {
                if (Genie.FileUtils.IsSamePath(profile, me)) continue; // this user's is the ModBuddy row itself
                foreach (string documents in new[] { "Documents", @"OneDrive\Documents" }) {
                    string folder = Path.Combine(profile, documents + @"\Firaxis ModBuddy");
                    if (Directory.Exists(folder)) found.Add(folder);
                }
            }
            return found;
        }

        // A folder that is the detected one (an untouched or reset box) stays automatic, so it follows Steam or
        // Documents if they move.
        private static void Store(LocationKind kind, string folder) {
            folder    = string.IsNullOrEmpty(folder) ? "" : Full(folder.Trim());
            bool auto = folder.Length == 0 || SameFolder(folder, Detected(kind));
            Genie.ConfigUtils.Set(kind.ToString(), auto ? AutoDetect : folder, Section);
        }

        private static void WriteList(string key, List<string> folders) {
            List<string> kept = new List<string>();
            foreach (string folder in folders) AddOnce(kept, Full(folder.Trim()));
            Genie.ConfigUtils.SetList(key, kept, Section);
        }

        private static void AddOnce(List<string> list, string folder) {
            if (!string.IsNullOrEmpty(folder) && !list.Exists(f => SameFolder(f, folder))) list.Add(folder);
        }

        private static void Apply() {
            Locations.Reset();
            if (Changed != null) Changed();
        }

        /// <summary>Same folder, as Windows compares paths. No folder (unset, or not detected) matches only no
        /// folder.</summary>
        public static bool SameFolder(string a, string b) {
            return string.IsNullOrEmpty(a) ? string.IsNullOrEmpty(b) : Genie.FileUtils.IsSamePath(a, b);
        }

        // A malformed path is kept as typed, so Source Folders can still show it.
        private static string Full(string path) {
            return Genie.FileUtils.GetFullPath(path) ?? path;
        }

        private static string Parent(string folder) {
            DirectoryInfo parent = folder == null ? null : Directory.GetParent(folder.TrimEnd('\\'));
            return parent == null ? null : parent.FullName;
        }
    }

    // == Locations ====================================================================================
    // Where a package file lives. Each kind has root folders; a file belongs to the deepest root containing it,
    // so Content\Mods (SDK Mod) wins over the XComGame\Content stock root around it.

    // Best to worst; the info window and tag order follow it.
    internal enum LocationKind { ModBuddy, Stock, Workshop, LocalMod, Custom, Unknown, SdkMod, Artifact, OldSdk }

    internal sealed class Location {
        public LocationKind Kind;
        public string       Label; // the source tag ("Workshop", or a custom source's own name)
        public string       Root;  // full path; null for Unknown
    }

    internal static class Locations {
        private static List<Location> _roots;

        public static readonly Location Unknown = new Location { Kind = LocationKind.Unknown, Label = "Unknown" };

        /// <summary>The tag for a kind ("SDK Mod"); a custom source's tag is its own name.</summary>
        public static string LabelOf(LocationKind kind) {
            switch (kind) {
                case LocationKind.LocalMod:
                    return "Local Mod";
                case LocationKind.SdkMod:
                    return "SDK Mod";
                case LocationKind.OldSdk:
                    return "Old SDK";
                default:
                    return kind.ToString();
            }
        }

        /// <summary>Forgets the roots, so the next lookup reads the folder settings again.</summary>
        public static void Reset() {
            _roots = null;
        }

        private static List<Location> Roots {
            get {
                if (_roots != null) return _roots;
                _roots = new List<Location>();
                foreach (string stock in FolderSettings.StockRoots) Add(LocationKind.Stock, "Stock", stock);
                foreach (LocationKind kind in new[] { LocationKind.SdkMod, LocationKind.Artifact, LocationKind.ModBuddy,
                                                      LocationKind.Workshop, LocationKind.LocalMod,
                                                      LocationKind.OldSdk })
                    Add(kind, LabelOf(kind), FolderSettings.Get(kind));
                foreach (LocationKind kind in FolderSettings.Expandable) {
                    foreach (string folder in FolderSettings.GetExtra(kind)) Add(kind, LabelOf(kind), folder);
                }
                foreach (FolderSettings.Source c in FolderSettings.GetCustom())
                    Add(LocationKind.Custom, c.Name, c.Path);
                return _roots;
            }
        }

        private static void Add(LocationKind kind, string label, string folder) {
            if (string.IsNullOrEmpty(folder)) return;
            _roots.Add(new Location {
                Kind  = kind,
                Label = label,
                Root  = Genie.FileUtils.GetFullPath(folder) ?? folder,
            });
        }

        /// <summary>The location of a file: its deepest root, or Unknown.</summary>
        public static Location Of(string file) {
            if (string.IsNullOrEmpty(file)) return Unknown;
            Location best = null;
            foreach (Location l in Roots) {
                if (!Genie.FileUtils.IsUnder(file, l.Root)) continue;
                if (best == null || l.Root.Length > best.Root.Length) best = l;
            }
            return best ?? Unknown;
        }

        /// <summary>
        /// Roots that can hold mod packages; stock packages exist only once. A root inside another is left out, or
        /// its files would be found twice.
        /// </summary>
        public static List<string> ModRoots() {
            List<string> roots = new List<string>();
            foreach (Location l in Roots) {
                if (l.Kind != LocationKind.Stock && l.Kind != LocationKind.OldSdk && Directory.Exists(l.Root))
                    roots.Add(l.Root);
            }
            List<string> outer = new List<string>();
            foreach (string r in roots) {
                bool inside = roots.Exists(o => Genie.FileUtils.IsUnder(r, o));
                bool twice  = outer.Exists(o => Genie.FileUtils.IsSamePath(o, r));
                if (!inside && !twice) outer.Add(r);
            }
            return outer;
        }

        /// <summary>
        /// A path short enough for a row, from its drive: Workshop from the 268500 folder ("C:\...\268500\..."),
        /// ModBuddy from the solution folder ([solution]\[mod]\Content\...), anything else its last 4 levels.
        /// </summary>
        public static string Shorten(string file, Location at) {
            string[] parts = file.Split('\\');

            int from = parts.Length - 4; // the last 4 levels
            if (at.Kind == LocationKind.Workshop) from = Array.IndexOf(parts, "268500");
            if (at.Kind == LocationKind.ModBuddy) {
                int content = Array.LastIndexOf(parts, "Content");
                if (content >= 2) from = content - 2;
            }
            string root = Path.GetPathRoot(file).TrimEnd('\\'); // "C:", or a share's "\\pc\share"
            int    kept = root.Split('\\').Length;              // the parts the root takes
            return from <= kept ? file : root + "\\...\\" + string.Join("\\", parts, from, parts.Length - from);
        }
    }

    // == Copies on Disk ===============================================================================
    // Every package file under the mod roots, by package name, so a dependency can list each copy, not just the
    // one the editor loaded. Built off the UI thread: the Workshop folder alone can hold thousands of files.

    internal static class CopyIndex {
        private static Dictionary<string, List<string>> _byName;
        private static Task                             _building;

        public static bool IsReady {
            get { return _byName != null; }
        }

        /// <summary>Rebuilds the index, then calls onDone on the UI thread, also if it failed.</summary>
        public static void Rebuild(Action onDone) {
            if (_building != null && !_building.IsCompleted) return; // the running build calls its own onDone
            List<string>                             roots   = Locations.ModRoots();
            Action<Dictionary<string, List<string>>> onBuilt = delegate(Dictionary<string, List<string>> byName) {
                _byName = byName;
                if (onDone != null) onDone();
            };
            Action<Exception> onFailed = delegate(Exception ex) {
                Genie.Log("Couldn't list package files on disk: " + ex.Message);
                if (onDone != null) onDone();
            };
            _building = Genie.WorkUtils.RunInBackground(() => Build(roots), onBuilt, onFailed);
        }

        /// <summary>Every file of that package name under the mod roots. Empty until the index is ready.</summary>
        public static List<string> Find(string packageName) {
            List<string> files;
            if (_byName == null || !_byName.TryGetValue(packageName, out files)) return new List<string>();
            return files;
        }

        private static Dictionary<string, List<string>> Build(List<string> roots) {
            Dictionary<string, List<string>> byName =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Stack<string> folders = new Stack<string>(roots);
            while (folders.Count > 0) {
                string folder = folders.Pop();
                try {
                    foreach (string sub in Directory.GetDirectories(folder)) folders.Push(sub);
                    foreach (FileInfo file in new DirectoryInfo(folder).GetFiles()) {
                        if (!file.Extension.Equals(".upk", StringComparison.OrdinalIgnoreCase) &&
                            !file.Extension.Equals(".umap", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // An asset cook leaves empty placeholders in Content\Mods\<Mod> if its cleanup fails.
                        if (file.Length == 0) continue;
                        string       name = Path.GetFileNameWithoutExtension(file.Name);
                        List<string> list;
                        if (!byName.TryGetValue(name, out list)) byName[name] = list = new List<string>();
                        list.Add(file.FullName);
                    }
                } catch (UnauthorizedAccessException) {
                } catch (IOException) {} // a folder that can't be read or vanished: skip it, keep the rest
            }
            return byName;
        }
    }

    // == Stock Unshipped ==============================================================================
    // Stock packages the game never ships as their own file, only cooked into maps (PackagesToForceCookPerMap):
    // outside those maps, a mod must ship them. Generated once from the SDK against the game's cooked folders, since
    // the game won't change, and kept here so the addon stays one file.

    internal static class StockUnshipped {
        // Package, then the maps the cooker forces it into (PackagesToForceCookPerMap); most have none, being cooked
        // only into the maps that use them. One entry per line, sorted by package. From StockUnshippedTable.bat,
        // 2026-11-20: 496 of the SDK's 3857 stock packages have no file in the WotC game's cooked folders.
        // StartupPackages and PackagesToAlwaysCook are left out: the game always loads those.
        private static readonly MapTable Maps = new MapTable {
            { "AkResources" },
            { "Ambulance" },
            { "BoxTruckA" },
            { "BreadCrumbs" },
            { "ChosenAD_TempAssets" },
            { "ChosenShowdown_Spawners" },
            { "CIN_2KPrevis_ANIM" },
            { "CIN_AdventCaptain" },
            { "CIN_AdventVialBare" },
            { "CIN_AdvNoHelm" },
            { "CIN_AdvTrooper" },
            { "CIN_AlienDevice01" },
            { "CIN_AvatarAutopsy_ART" },
            { "CIN_Avenger" },
            { "CIN_AVG_Cabinet" },
            { "CIN_AVG_Door" },
            { "CIN_BarCounter" },
            { "CIN_BasicColors" },
            { "CIN_Betos" },
            { "CIN_Character_SetupPhase" },
            { "CIN_CharacterMaterials" },
            { "CIN_ChipDisplay" },
            { "CIN_Civilians" },
            { "CIN_CommanderSheet" },
            { "CIN_Conversation" },
            { "CIN_CyberusBrain" },
            { "CIN_DarkBed_Detail" },
            { "CIN_DenMother" },
            { "CIN_FaceMaskArm" },
            { "CIN_FaceMelt_ANIM" },
            { "CIN_FramedPictures" },
            { "CIN_GremCyberus" },
            { "CIN_HandTool01" },
            { "CIN_Headset" },
            { "CIN_IncubationChamber" },
            { "CIN_Keyboard01" },
            { "CIN_LightPanel" },
            { "CIN_LoadingCart" },
            { "CIN_MG_AlienFortressRevealed" },
            { "CIN_MG_AvengAtt" },
            { "CIN_MG_BlacksiteHorror" },
            { "CIN_MG_BroadcastTruth" },
            { "CIN_MG_CommAwake" },
            { "CIN_MG_Common" },
            { "CIN_MG_DarkVolunteer" },
            { "CIN_MG_DarkVolunteerPT2" },
            { "CIN_MG_FirstFlight" },
            { "CIN_MG_HumansObsolete" },
            { "CIN_MG_Intro02" },
            { "CIN_MG_LiberationNetwork" },
            { "CIN_MG_OldGodsPT1" },
            { "CIN_MG_OldGodsPT2" },
            { "CIN_MG_PreFinal" },
            { "CIN_MG_PsiNet" },
            { "CIN_MG_ShadowChamber" },
            { "CIN_MG_SP_RescueCommander" },
            { "CIN_MG_SP_SkyrangerEvac" },
            { "CIN_MG_Victory" },
            { "CIN_MG_WelcomeEng" },
            { "CIN_MG_WelcomeHQ" },
            { "CIN_MG_WelcomeLab" },
            { "CIN_MG_WelcomeResist" },
            { "CIN_MG_XCOM_OSmain" },
            { "CIN_ModelInterceptor" },
            { "CIN_ModelSkyranger" },
            { "CIN_Mox" },
            { "CIN_Objects" },
            { "CIN_PistolModern" },
            { "CIN_PostProcess" },
            { "CIN_PostProcesses" },
            { "CIN_Previs_ANIM" },
            { "CIN_Previs_ART" },
            { "CIN_Previs_AUDIO" },
            { "CIN_Previs_MissionExit" },
            { "CIN_RadioTower" },
            { "CIN_SectoidSkull" },
            { "CIN_SoldierTorsoMorph" },
            { "CIN_StasisReadout" },
            { "CIN_StasisVisor" },
            { "CIN_Tactical_Loot" },
            { "CIN_ThinManGlasses" },
            { "CIN_TP_AlienFortress_ANIM" },
            { "CIN_TP_AlienFortressRevealed_ART" },
            { "CIN_TP_AvatarAutopsy_ANIM" },
            { "CIN_TP_AvatarRevealed_ANIM" },
            { "CIN_TP_AvengerAttacked_ANIM" },
            { "CIN_TP_AvengerAttacked_ART" },
            { "CIN_TP_AvengerRamp_ANIM" },
            { "CIN_TP_BlacksiteHorror_ANIM" },
            { "CIN_TP_BlacksiteHorror_ART" },
            { "CIN_TP_BlacksiteVialGrab_ANIM" },
            { "CIN_TP_BroadcastTheTruth_ANIM" },
            { "CIN_TP_BroadcastTheTruth_ART" },
            { "CIN_TP_BuildOutpost_ANIM" },
            { "CIN_TP_BuildOutpost_CinART" },
            { "CIN_TP_CommanderAwakens_ANIM" },
            { "CIN_TP_CommanderAwakens_ART" },
            { "CIN_TP_DarkVolunteer_pt1_ANIM" },
            { "CIN_TP_DarkVolunteer_pt1_ART" },
            { "CIN_TP_DarkVolunteer_pt2_ART" },
            { "CIN_TP_DarkVolunteer_pt2_Bridge_ANIM" },
            { "CIN_TP_DarkVolunteer_pt2_Gear_ANIM" },
            { "CIN_TP_DarkVolunteer_pt2_PowerCore_ANIM" },
            { "CIN_TP_DarkVolunteer_pt2_Shadow_ANIM" },
            { "CIN_TP_EldersAppear_ANIM" },
            { "CIN_TP_FirstContact_ANIM" },
            { "CIN_TP_FirstContact_ART" },
            { "CIN_TP_FirstFlight_ANIM" },
            { "CIN_TP_FirstFlight_ART" },
            { "CIN_TP_FlightDeviceReveal_ANIM" },
            { "CIN_TP_Gameplay_ANIM" },
            { "CIN_TP_GroupShot_ANIM" },
            { "CIN_TP_HumansAreObsolete_ANIM" },
            { "CIN_TP_HumansAreObsolete_ART" },
            { "CIN_TP_Intro_ANIM" },
            { "CIN_TP_LiberationNetwork" },
            { "CIN_TP_LiberationNetwork_ANIM" },
            { "CIN_TP_OldGods_pt1_ANIM" },
            { "CIN_TP_OldGods_pt1_ART" },
            { "CIN_TP_OldGods_pt2_ANIM" },
            { "CIN_TP_OldGods_pt2_ART" },
            { "CIN_TP_PostIntro_ANIM" },
            { "CIN_TP_PostIntro_Art" },
            { "CIN_TP_PrefinalMission_ANIM" },
            { "CIN_TP_PreFinalMission_ART" },
            { "CIN_TP_PsiNetwork_ANIM" },
            { "CIN_TP_PsiNetwork_ART" },
            { "CIN_TP_ShadowChamber_ART" },
            { "CIN_TP_Shen_PullBack_ART" },
            { "CIN_TP_ShenGetsLoot_ANIM" },
            { "CIN_TP_ShenGetsLoot_ART" },
            { "CIN_TP_Victory_ANIM" },
            { "CIN_TP_Victory_ART" },
            { "CIN_TP_WelcomeEngineering_ANIM" },
            { "CIN_TP_WelcomeEngineering_ART" },
            { "CIN_TP_WelcomeHQ_ANIM" },
            { "CIN_TP_WelcomeHQ_ART" },
            { "CIN_TP_WelcomeLab_ANIM" },
            { "CIN_TP_WelcomeLab_ART" },
            { "CIN_TP_WelcomeLab2_ART" },
            { "CIN_TP_WelcomeResistance_ANIM" },
            { "CIN_TP_WelcomeResistance_ART" },
            { "CIN_Trailer_ANIM" },
            { "CIN_TroopTransport" },
            { "CIN_TUT_AdventResponse_ANIM" },
            { "CIN_TUT_CurbStomp_ANIM" },
            { "CIN_TUT_CurbStomp_ART" },
            { "CIN_TUT_FacilityReveal_ANIM" },
            { "CIN_TUT_RescueCommander_ANIM" },
            { "CIN_TUT_RescueCommander_ART" },
            { "CIN_TUT_SkyrangerEvac_ANIM" },
            { "CIN_TUT_Skyrangerevac_ART" },
            { "CIN_Tutorial1" },
            { "CIN_Volk" },
            { "CIN_WallDestroyed" },
            { "Cin_Wires" },
            { "CIN_WPN_Shotgun" },
            { "CIN_XEW_FX" },
            { "CIN_XP_01_PreIntro_ANIM" },
            { "CIN_XP_02_LostAbandonSetup_ANIM" },
            { "CIN_XP_03_SkirmisherReveal_ANIM" },
            { "CIN_XP_04_ReaperReveal_ANIM" },
            { "CIN_XP_05_RendezvousAssassin_ANIM" },
            { "CIN_XP_06_ChosenCallToAction_ANIM" },
            { "CIN_XP_07_AvgAssault_ANIM" },
            { "CIN_XP_07c_WrapUp_Templar_ANIM" },
            { "CIN_XP_08_SarcophagusReveal_ANIM" },
            { "CIN_XP_08a_SarcophagusReveal_ANIM" },
            { "CIN_XP_08b_SarcophagusReveal_ANIM" },
            { "CIN_XP_08c_SarcophagusReveal_ANIM" },
            { "CIN_XP_12a_ChosenDeath_Assassin_ANIM" },
            { "CIN_XP_12b_ChosenDeath_Hunter_ANIM" },
            { "CIN_XP_12c_ChosenDeath_Warlock_ANIM" },
            { "CIN_XP_16_ElderRage_ANIM" },
            { "CIN_XP_17a_WrapUp_Reaper_ANIM" },
            { "CIN_XP_17c_WrapUp_Templar_ANIM" },
            { "CIN_XP_17d_WrapUp_Skirmisher_ANIM" },
            { "CIN_XP_23_FogPodReveal_ANIM" },
            { "CIN_XP_24_LostEvac_ANIM" },
            { "CIN_XP_25_LostReveal_ANIM" },
            { "CIN_XP_26_PurifierReveal_ANIM" },
            { "CIN_XP_27_BigBang_ANIM" },
            { "CIN_XP_MG_AvengerAssault" },
            { "CIN_XP_MG_LostAbandonSetup" },
            { "CIN_XP_MG_PreIntro" },
            { "CompactCar" },
            { "CovertEscape_Spawners" },
            { "CovertEscape_TempAssets" },
            { "DestroyTransmitter_TempAssets" },
            { "DLC_1_ModShaderCache" },
            { "DLCMonikers" },
            { "EditorLandscapeResources" },
            { "EditorMaterials" },
            { "EditorMeshes" },
            { "EditorResources" },
            { "EditorResources_GFx" },
            { "EditorShellMaterials" },
            { "Engine_Lights" },
            { "Engine_MaterialFunctions01" },
            { "Engine_MaterialFunctions02" },
            { "Engine_MaterialFunctions03" },
            { "Engine_MI_Shaders" },
            { "EngineApexResources" },
            { "EngineBuildings" },
            { "EngineFonts_RUS" },
            { "EngineMeshes" },
            { "EngineProduction" },
            { "EngineResources" },
            { "EngineVolumetrics" },
            { "FemaleVoice1_French_Data" },
            { "FemaleVoice1_German_Data" },
            { "FemaleVoice1_Italian_Data" },
            { "FemaleVoice1_Russian_Data" },
            { "FemaleVoice1_Spanish_Data" },
            { "FemaleVoice2_French_Data" },
            { "FemaleVoice2_German_Data" },
            { "FemaleVoice2_Italian_Data" },
            { "FemaleVoice2_Spanish_Data" },
            { "FemaleVoice3_English_Data" },
            { "FemaleVoice3_French_Data" },
            { "FemaleVoice3_German_Data" },
            { "FemaleVoice3_Italian_Data" },
            { "FemaleVoice3_Spanish_Data" },
            { "FemaleVoice4_English_Data" },
            { "FemaleVoice4_French_Data" },
            { "FemaleVoice4_German_Data" },
            { "FemaleVoice4_Italian_Data" },
            { "FemaleVoice4_Spanish_Data" },
            { "FemaleVoice5_English_Data" },
            { "FemaleVoice5_French_Data" },
            { "FemaleVoice5_German_Data" },
            { "FemaleVoice5_Italian_Data" },
            { "FemaleVoice5_Spanish_Data" },
            { "FemaleVoice6_English_Data" },
            { "FemaleVoice6_French_Data" },
            { "FemaleVoice6_German_Data" },
            { "FemaleVoice6_Italian_Data" },
            { "FemaleVoice6_Spanish_Data" },
            { "FlatBedA" },
            { "FX_Abandoned_City_Dust" },
            { "FX_Abandoned_City_Dust_Env" },
            { "FX_Abandoned_City_Dust_Reduced" },
            { "FX_Advent_Security" },
            { "FX_Advent_Tower" },
            { "FX_Alien_Misc" },
            { "FX_Alien_Misc_Env" },
            { "FX_Animals_Terrestrial" },
            { "FX_Animals_Terrestrial_Env" },
            { "FX_Aquatic_Animals" },
            { "FX_Aquatic_Animals_Env" },
            { "FX_ATM_Holograms" },
            { "FX_ATM_Holograms_Env" },
            { "FX_Atmospherics" },
            { "FX_Atmospherics_Env" },
            { "FX_Bats_Env" },
            { "FX_Billboards" },
            { "FX_Billboards_Env" },
            { "FX_Birds_Flying" },
            { "FX_Birds_Flying_Env" },
            { "FX_Birds_on_Ground" },
            { "FX_Birds_on_Ground_Env" },
            { "FX_Cave_Deco_Env" },
            { "FX_CIN_Elder_Rage" },
            { "FX_CIN_XP_Elder_Projection" },
            { "FX_Cloth_Blowing" },
            { "FX_Cloth_Blowing_Env" },
            { "FX_Cloth_Blowing_Materials_Env" },
            { "FX_Decal_Textures_Hard_Soft" },
            { "FX_Decals_Blood_A" },
            { "FX_Decals_Blood_Env" },
            { "FX_Decals_Charring" },
            { "FX_Decals_Charring_Env" },
            { "FX_Destruction_Gen" },
            { "FX_Dev_Bazzell" },
            { "FX_Dev_Bazzell_2" },
            { "FX_Dev_Bazzell_TrainingTaskA" },
            { "FX_Dev_Debug_XPack" },
            { "FX_Dev_Motion_Vectors" },
            { "FX_Dev_Rick_XPack" },
            { "FX_Dev_Steve_X2_XPack" },
            { "FX_Electrical" },
            { "FX_Electrical_Env" },
            { "FX_Exhaust_Vapors" },
            { "FX_Exhaust_Vapors_Env" },
            { "FX_Fire" },
            { "FX_Fire_Env" },
            { "FX_Fog_Pod" },
            { "FX_Fog_Pod_Env" },
            { "FX_Fog_Pod_Tendrils" },
            { "FX_Fog_Pod_Tendrils_Env" },
            { "FX_Fogged_Bodies" },
            { "FX_Fogged_Bodies_Env" },
            { "FX_Foliage" },
            { "FX_Foliage_Env" },
            { "FX_Fountains" },
            { "FX_Glows" },
            { "FX_Glows_Env" },
            { "FX_HALON" },
            { "FX_HALON_Dev_01" },
            { "FX_HALON_Dev_02" },
            { "FX_HALON_Dev_03" },
            { "FX_Holograms" },
            { "FX_Holograms_Env" },
            { "FX_Insects" },
            { "FX_Insects_Env" },
            { "FX_Laser_Bar_Windows" },
            { "FX_Lens_Flares_Env" },
            { "FX_Light_Rays" },
            { "FX_Light_Rays_Env" },
            { "FX_LoadScreens_Textures" },
            { "FX_Misc_Deco" },
            { "FX_Misc_Deco_Env" },
            { "FX_Museum_Art" },
            { "FX_Museum_Art_Env" },
            { "FX_Ocean_Shore" },
            { "FX_Ocean_Shore_Env" },
            { "FX_Ocean_Water" },
            { "FX_Pigeons_on_Ground_Env" },
            { "FX_Plasma" },
            { "FX_Plasma_Env" },
            { "FX_Psi_Vapors" },
            { "FX_Psi_Vapors_Env" },
            { "FX_Rigid_Bodies_Blowing" },
            { "FX_Rigid_Bodies_Blowing_Env" },
            { "FX_Smoke" },
            { "FX_Snakes" },
            { "FX_Snakes_Env" },
            { "FX_Splash_Kickup" },
            { "FX_Trash_X2" },
            { "FX_Trash_X2_Env" },
            { "FX_Water_CityCenter" },
            { "FX_Water_CityCenter_Env" },
            { "FX_Water_Deco" },
            { "FX_Water_Deco_Env" },
            { "FX_Water_Slums" },
            { "FX_Water_Slums_Env" },
            { "FX_Weather_Global" },
            { "GameData_Units" },
            { "GameData_Weapons" },
            { "GatherSurvivors_TempAssets" },
            { "gfxGamepadIcons" },
            { "gfxGamepadImages" },
            { "Halon_CA_Animations_03" },
            { "Halon_CA_FallingMonitors" },
            { "Halon_Flashlight" },
            { "Halon_Materials" },
            { "Halon_Materials_02" },
            { "Halon_Props" },
            { "Halon_RET_Animations" },
            { "Halon_RET_Animations_02" },
            { "Halon_RET_Archetypes" },
            { "Halon_RET_Bradford" },
            { "Halon_RET_Characters" },
            { "Halon_RET_Materials" },
            { "Halon_RET_Particles" },
            { "Halon_RET_PostProcess" },
            { "Halon_RET_Props" },
            { "Halon_RET_Vehicles" },
            { "Halon_RET_Video" },
            { "LocalShaderCache-PC-D3D-SM4" },
            { "MaleVoice1_French_Data" },
            { "MaleVoice1_German_Data" },
            { "MaleVoice1_Italian_Data" },
            { "MaleVoice1_Russian_Data" },
            { "MaleVoice1_Spanish_Data" },
            { "MaleVoice2_French_Data" },
            { "MaleVoice2_German_Data" },
            { "MaleVoice2_Italian_Data" },
            { "MaleVoice2_Spanish_Data" },
            { "MaleVoice3_English_Data" },
            { "MaleVoice3_French_Data" },
            { "MaleVoice3_German_Data" },
            { "MaleVoice3_Italian_Data" },
            { "MaleVoice3_Spanish_Data" },
            { "MaleVoice4_French_Data" },
            { "MaleVoice4_German_Data" },
            { "MaleVoice4_Italian_Data" },
            { "MaleVoice4_Spanish_Data" },
            { "MaleVoice5_English_Data" },
            { "MaleVoice5_French_Data" },
            { "MaleVoice5_German_Data" },
            { "MaleVoice5_Italian_Data" },
            { "MaleVoice5_Spanish_Data" },
            { "MaleVoice6_English_Data" },
            { "MaleVoice6_French_Data" },
            { "MaleVoice6_German_Data" },
            { "MaleVoice6_Italian_Data" },
            { "MaleVoice6_Spanish_Data" },
            { "MaleVoice7_English_Data" },
            { "MaleVoice7_French_Data" },
            { "MaleVoice7_German_Data" },
            { "MaleVoice7_Italian_Data" },
            { "MaleVoice7_Spanish_Data" },
            { "MaleVoice8_English_Data" },
            { "MaleVoice8_French_Data" },
            { "MaleVoice8_German_Data" },
            { "MaleVoice8_Italian_Data" },
            { "MaleVoice8_Spanish_Data" },
            { "MaleVoice9_English_Data" },
            { "MaleVoice9_French_Data" },
            { "MaleVoice9_German_Data" },
            { "MaleVoice9_Italian_Data" },
            { "MaleVoice9_Spanish_Data" },
            { "MapTemplateIndex" },
            { "MapTemplates" },
            { "MaterialTemplates" },
            { "MECFemaleVoice1_English" },
            { "MECFemaleVoice1_French" },
            { "MECFemaleVoice1_French_Data" },
            { "MECFemaleVoice1_German" },
            { "MECFemaleVoice1_German_Data" },
            { "MECFemaleVoice1_Italian" },
            { "MECFemaleVoice1_Italian_Data" },
            { "MECFemaleVoice1_Polish" },
            { "MECFemaleVoice1_Russian" },
            { "MECFemaleVoice1_Russian_Data" },
            { "MECFemaleVoice1_Spanish" },
            { "MECFemaleVoice1_Spanish_Data" },
            { "MECFemaleVoice2_English" },
            { "MECFemaleVoice2_French" },
            { "MECFemaleVoice2_French_Data" },
            { "MECFemaleVoice2_German" },
            { "MECFemaleVoice2_German_Data" },
            { "MECFemaleVoice2_Italian" },
            { "MECFemaleVoice2_Italian_Data" },
            { "MECFemaleVoice2_Polish" },
            { "MECFemaleVoice2_Russian" },
            { "MECFemaleVoice2_Spanish" },
            { "MECFemaleVoice2_Spanish_Data" },
            { "MECMaleVoice1_English" },
            { "MECMaleVoice1_French" },
            { "MECMaleVoice1_French_Data" },
            { "MECMaleVoice1_German" },
            { "MECMaleVoice1_German_Data" },
            { "MECMaleVoice1_Italian" },
            { "MECMaleVoice1_Italian_Data" },
            { "MECMaleVoice1_Polish" },
            { "MECMaleVoice1_Russian" },
            { "MECMaleVoice1_Russian_Data" },
            { "MECMaleVoice1_Spanish" },
            { "MECMaleVoice1_Spanish_Data" },
            { "MECMaleVoice2_English" },
            { "MECMaleVoice2_French" },
            { "MECMaleVoice2_French_Data" },
            { "MECMaleVoice2_German" },
            { "MECMaleVoice2_German_Data" },
            { "MECMaleVoice2_Italian" },
            { "MECMaleVoice2_Italian_Data" },
            { "MECMaleVoice2_Polish" },
            { "MECMaleVoice2_Russian" },
            { "MECMaleVoice2_Spanish" },
            { "MECMaleVoice2_Spanish_Data" },
            { "MissionAssets_ChosenAD" },
            { "MissionAssets_ChosenShowdown" },
            { "MissionAssets_LostAndAbandoned" },
            { "MissionAssets_RecoverExpedition" },
            { "MissionAssets_SupplyExtraction" },
            { "MissionAssets_XPACK" },
            { "MobileResources" },
            { "ModernSedanA" },
            { "ModernSedanA_Cab" },
            { "ModernSedanA_Police" },
            { "ModernSedanA_Resistance" },
            { "Nate_DEV" },
            { "NeutralizeFieldCommander_TempAssets" },
            { "NodeBuddies" },
            { "PCP_Archetypes_XPACK" },
            { "PCP_Plates_XPACK" },
            { "Proxy_XPack" },
            { "PsiTransmitter" },
            { "RefContentTagsIndex" },
            { "RefShaderCache-PS3" },
            { "RefShaderCache-Xbox360" },
            { "Shell_Arid" },
            { "Shell_CityCenterNight" },
            { "SupplyExtraction_TempAssets" },
            { "UI_AroundCorner" },
            { "UI_Button" },
            { "UI_ClimbLadder" },
            { "UI_ClimbOver" },
            { "UI_Cover", "Avenger_Root", "XComShell", "XComShell_Multiplayer" },
            { "UI_Door01" },
            { "UI_Geoscape" },
            { "UI_Grapple" },
            { "UI_InteractWithComputer" },
            { "UI_Investigate" },
            { "UI_MaterialParents" },
            { "UI_PickUpSalvage" },
            { "UI_SimpleShapes" },
            { "UI_TargetCursor" },
            { "UI_Tutorial" },
            { "UI_Waypoint" },
            { "UI_Window" },
            { "UnitCursor" },
            { "X2StrategyWaveData" },
            { "XPACK_Skies" },
        };

        /// <summary>The maps a stock package is cooked into, or null when the game ships it as its own file.</summary>
        public static string[] MapsOf(string package) {
            string[] maps;
            return Maps.ByPackage.TryGetValue(package, out maps) ? maps : null;
        }

        // IEnumerable only so the table can be written as a collection initializer, whose entries call Add.
        private sealed class MapTable : IEnumerable {
            public readonly Dictionary<string, string[]> ByPackage =
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            public void Add(string package, params string[] maps) {
                ByPackage[package] = maps;
            }

            IEnumerator IEnumerable.GetEnumerator() {
                return ByPackage.GetEnumerator();
            }
        }
    }

    // == Audit ========================================================================================

    // Declared in sort order: Dependencies list by bucket, then name.
    internal enum Bucket { NotLoaded, StockUnshipped, OtherMod, Loose, SameMod, Stock }

    internal sealed class UsedObject {
        public string PathName;  // in the source package ("Pkg.Group.Obj")
        public string ClassName; // "Texture2D"

        // Exports in the audited package that use it, by path name.
        public List<string> Dependents = new List<string>();
    }

    internal sealed class Dependency {
        public string              Package;
        public Bucket              Bucket;
        public string              File; // the loaded copy; null when not loaded
        public string              Mod;  // the shared mod when Same mod, else the first; null for stock and loose
        public string[]            Maps; // Stock unshipped: the maps the game cooks it into
        public bool                ShippedByMod; // Stock unshipped, but the audited package's mod has its own copy
        public List<ModFinder.Mod> Mods    = new List<ModFinder.Mod>(); // every mod a copy is in; stock: none
        public List<UsedObject>    Objects = new List<UsedObject>();

        public int RefCount {
            get {
                int n = 0;
                foreach (UsedObject o in Objects) n += o.Dependents.Count;
                return n;
            }
        }
    }

    internal sealed class PackageAudit {
        // Why a package has no audit: it has no linker, so no tables. Open, the package file cache says which (spec,
        // Host facts: the probed table), and only the first is fixed by reloading. NotLoaded: not in memory, but a file
        // by its name is on disk; the user's Fully Load gives it a linker, as this addon never loads anything.
        // UnsavedChanges: a file is on disk, but the package changed in memory since its linker went (moving an asset
        // resets the loaders), so a linker made now would read the file's layout, not memory's. Saving fixes it.
        public enum Block { None, LinkerUnloaded, FileMissing, NotSavedYet, NotLoaded, UnsavedChanges }

        // Never listed as unlinked, so they never block a check or show on the card: the editor's own, not content.
        // Selected themselves, they're still audited.
        private static readonly string[] IgnoredUnlinked = { "DeletedRedirectors" };

        public string              Package;
        public string              File;
        public List<ModFinder.Mod> Mods = new List<ModFinder.Mod>(); // every mod a copy is in, best source first
        public string              Problem;                          // why there's no audit, or null
        public Block               Blocked;
        public string              CachedFile; // blocked: the file the cache lists, on disk or not; null if none

        // Open packages with no linker, sorted, by what brings their tables back. Reloadable: a file by their name on
        // disk and no unsaved changes; Reload linkers takes them. Unsaved: a file, but unsaved changes, so saving
        // first. Unreloadable: no file (never saved, or the file gone), and unsaved changes. The card and
        // Unreferenced name them.
        public List<string> Reloadable   = new List<string>();
        public List<string> Unsaved      = new List<string>();
        public List<string> Unreloadable = new List<string>();

        public string Mod {
            get { return Mods.Count > 0 ? Mods[0].Name : null; }
        }
        public string ModRoot {
            get { return Mods.Count > 0 ? Mods[0].Root : null; }
        }

        public bool             HasDependents = true; // false when the linker's DependsMap isn't in memory
        public List<Dependency> Dependencies  = new List<Dependency>();
        public List<Check>      Checks        = new List<Check>(); // every check, by state then name

        // Class of each export that uses something, by path name. From the export table, not the objects: a package
        // that isn't fully loaded has no objects for most of its exports.
        public Dictionary<string, string> ExportClasses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly ModFinder _mods = new ModFinder();

        public static PackageAudit Run(string packageName) {
            PackageAudit                   a = new PackageAudit { Package = packageName };
            a.FindUnlinked();
            Genie.LinkerUtils.LinkerStatus status;
            IntPtr                         linker = Genie.LinkerUtils.GetLinker(packageName, false, out status);
            if (status == Genie.LinkerUtils.LinkerStatus.NotLoaded) return a.BlockNotLoaded();
            if (status == Genie.LinkerUtils.LinkerStatus.NoLinker) return a.BlockFromCache();
            if (linker == IntPtr.Zero) return a.Fail("This package’s tables aren’t available.");

            a.File = Genie.LinkerUtils.GetFileName(linker);
            if (Locations.Of(a.File).Kind != LocationKind.Stock) a.Mods = a.ModsOf(packageName, a.File);
            a.ReadImports(linker);
            foreach (Dependency d in a.Dependencies) a.Classify(d);
            // Script packages are code, not content; only a mod's own script is worth showing. The loaded file's mod,
            // not its copies': a Highlander ships cooked copies of stock script (CookedPCConsole\Engine.upk).
            a.Dependencies.RemoveAll(
                d => d.File != null && a._mods.Find(d.File) == null &&
                     string.Equals(Path.GetExtension(d.File), ".u", StringComparison.OrdinalIgnoreCase));
            a.Dependencies.Sort(ByBucketThenName);
            a.Checks = Check.RunAll(linker, a);
            return a;
        }

        private static int ByBucketThenName(Dependency x, Dependency y) {
            int rank = Rank(x).CompareTo(Rank(y));
            return rank != 0 ? rank : string.Compare(x.Package, y.Package, StringComparison.OrdinalIgnoreCase);
        }

        // The bucket's place, except that one this mod ships is resolved: it sorts after Same mod, before Stock.
        private static int Rank(Dependency d) {
            return d.ShippedByMod ? (int)Bucket.SameMod * 2 + 1 : (int)d.Bucket * 2;
        }

        // A package with no file and no changes can't use anything: the editor's blank level (Untitled_0, there
        // every session) until something is placed in it. One with a file and changes isn't reloaded: its linker would
        // read the file, which lacks the changes (a moved asset still at its old path).
        private void FindUnlinked() {
            foreach (string p in Genie.LinkerUtils.GetUnlinkedPackages()) {
                if (Array.Exists(IgnoredUnlinked, i => string.Equals(i, p, StringComparison.OrdinalIgnoreCase)))
                    continue;
                bool dirty = IsDirty(p);
                if (Genie.PackageUtils.GetPackageFile(p) != null)
                    (dirty ? Unsaved : Reloadable).Add(p);
                else if (dirty)
                    Unreloadable.Add(p);
            }
            Reloadable.Sort(StringComparer.OrdinalIgnoreCase);
            Unsaved.Sort(StringComparer.OrdinalIgnoreCase);
            Unreloadable.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsDirty(string package) {
            return Genie.ObjectUtils.IsPackageDirty(Genie.ObjectUtils.FindPackage(package));
        }

        private PackageAudit Fail(string problem) {
            Problem = problem;
            return this;
        }

        // A file by its name on disk: reloading makes the linker again, once any unsaved changes are saved. A cache
        // entry whose file is gone: the file is missing. No entry: never saved. The header's mod and copies still
        // show, as they come from files. Dirtiness is read directly, not from Unsaved: an ignored package isn't there.
        private PackageAudit BlockFromCache() {
            File       = Genie.PackageUtils.GetPackageFile(Package);
            CachedFile = File ?? Genie.PackageUtils.GetCachedPackageFile(Package);
            if (File != null)
                Blocked = IsDirty(Package) ? Block.UnsavedChanges : Block.LinkerUnloaded;
            else
                Blocked = CachedFile != null ? Block.FileMissing : Block.NotSavedYet;
            if (CachedFile != null && Locations.Of(CachedFile).Kind != LocationKind.Stock) Mods = ModsOf(Package, File);
            return this;
        }

        // Not in memory. With a file by its name on disk (a stock package the browser lists, a mod package unloaded),
        // the card sends the user to Fully Load; with none there's nothing to load, so the empty state says so.
        private PackageAudit BlockNotLoaded() {
            File = Genie.PackageUtils.GetPackageFile(Package);
            if (File == null) return Fail("This package isn’t loaded, and no file by its name is on disk.");
            Blocked = Block.NotLoaded;
            if (Locations.Of(File).Kind != LocationKind.Stock) Mods = ModsOf(Package, File);
            return this;
        }

        // Not in memory, at a file the caller found (the mod view, in the mod's folder): the cache's file may be
        // another copy, or there's none for a folder the editor doesn't search.
        public static PackageAudit NotLoadedAt(string packageName, string file) {
            PackageAudit a = new PackageAudit { Package = packageName, File = file, Blocked = Block.NotLoaded };
            a.FindUnlinked();
            if (Locations.Of(file).Kind != LocationKind.Stock) a.Mods = a.ModsOf(packageName, file);
            return a;
        }

        // Groups the import table's objects by outermost package, then credits each export that references one as
        // its dependent: through its class (export table), and anything else it uses (DependsMap).
        private void ReadImports(IntPtr linker) {
            Dictionary<string, Dependency> byPackage =
                new Dictionary<string, Dependency>(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, UsedObject> byImport = new Dictionary<int, UsedObject>();

            int imports = Genie.LinkerUtils.GetImportCount(linker);
            for (int i = 0; i < imports; i++) {
                Genie.LinkerUtils.Import imp = Genie.LinkerUtils.GetImport(linker, i);
                if (imp.PathName == null || imp.ClassName == "Package") continue; // packages and groups hold objects
                if (string.Equals(imp.PackageName, Package, StringComparison.OrdinalIgnoreCase)) continue;

                Dependency dep;
                if (!byPackage.TryGetValue(imp.PackageName, out dep)) {
                    dep                        = new Dependency { Package = imp.PackageName };
                    byPackage[imp.PackageName] = dep;
                    Dependencies.Add(dep);
                }
                UsedObject obj = new UsedObject { PathName = imp.PathName, ClassName = imp.ClassName };
                dep.Objects.Add(obj);
                byImport[i] = obj;
            }

            int exports = Genie.LinkerUtils.GetExportCount(linker);
            for (int e = 0; e < exports; e++) {
                List<int> used = new List<int> { Genie.LinkerUtils.GetExportClassIndex(linker, e) };
                int[] refs     = Genie.LinkerUtils.GetExportDependencies(linker, e);
                if (refs != null)
                    used.AddRange(refs);
                else
                    HasDependents = false;
                string exportPath = null; // read only for exports that reference an import
                foreach (int index in used) {
                    UsedObject obj;
                    if (index >= 0 || !byImport.TryGetValue(-index - 1, out obj)) continue;
                    if (exportPath == null) {
                        exportPath                = Genie.LinkerUtils.GetExportPathName(linker, e);
                        ExportClasses[exportPath] = Genie.LinkerUtils.GetExportClassName(linker, e);
                    }
                    if (!obj.Dependents.Contains(exportPath)) obj.Dependents.Add(exportPath);
                }
            }
        }

        // Not loaded: no file behind it. Not in memory, or in memory with no file by its name on disk (made in the
        // editor and never saved, or its file moved or deleted). The package's file, not its linker's: a package
        // saved since it loaded has no linker, and is still loaded.
        private void Classify(Dependency d) {
            bool loaded = Genie.ObjectUtils.FindPackage(d.Package) != IntPtr.Zero;
            d.File      = loaded ? Genie.PackageUtils.GetPackageFile(d.Package) : null;
            if (d.File == null) {
                d.Bucket = Bucket.NotLoaded;
                return;
            }
            // Before the mods: a mod may ship a stock package (Content\MissingUncooked); that doesn't make it theirs.
            if (Locations.Of(d.File).Kind == LocationKind.Stock) {
                // Unshipped, unless this package is a map it's cooked into: there the game has it.
                string[] maps = StockUnshipped.MapsOf(d.Package);
                bool inMap =
                    maps != null && Array.Exists(maps, m => m.Equals(Package, StringComparison.OrdinalIgnoreCase));
                d.Bucket = maps != null && !inMap ? Bucket.StockUnshipped : Bucket.Stock;
                if (d.Bucket != Bucket.StockUnshipped) return;
                // Only this mod's own copy counts (MissingUncooked, usually): players may not have any other mod.
                d.Maps         = maps;
                d.Mods         = ModsOf(d.Package, d.File);
                d.ShippedByMod = d.Mods.Exists(m => Mods.Exists(o => SameMod(o, m)));
                return;
            }
            d.Mods               = ModsOf(d.Package, d.File);
            ModFinder.Mod shared = d.Mods.Find(m => Mods.Exists(o => SameMod(o, m)));
            if (shared != null) {
                d.Bucket = Bucket.SameMod;
                d.Mod    = shared.Name;
            } else if (d.Mods.Count > 0) {
                d.Bucket = Bucket.OtherMod;
                d.Mod    = d.Mods[0].Name;
            } else {
                d.Bucket = Bucket.Loose;
            }
        }

        // Best source first. A project, its builds and its uploads share one name, so the order only decides
        // between different mods shipping the same package name.
        private static readonly LocationKind[] ModSourceOrder = {
            LocationKind.ModBuddy,
            LocationKind.Workshop,
            LocationKind.LocalMod,
            LocationKind.Artifact,
        };

        // The mods any copy belongs to, each once, best source first. Not just the loaded copy: it can sit in a UPK
        // repo or SDK Mod while the project holds the copy that ships. Within a source, the loaded copy first.
        private List<ModFinder.Mod> ModsOf(string package, string loadedFile) {
            List<ModFinder.Mod> mods   = new List<ModFinder.Mod>();
            List<Copy>          copies = Copy.Of(package, loadedFile);
            for (int rank = 0; rank <= ModSourceOrder.Length; rank++) {
                foreach (Copy c in copies) {
                    if (RankOf(c.At.Kind) != rank) continue;
                    ModFinder.Mod mod = _mods.Find(c.File);
                    if (mod != null && !mods.Exists(m => SameMod(m, mod))) mods.Add(mod);
                }
            }
            return mods;
        }

        // SDK Mod, Custom and Unknown last: the build never puts a marker there, but one may be.
        private static int RankOf(LocationKind kind) {
            int rank = Array.IndexOf(ModSourceOrder, kind);
            return rank >= 0 ? rank : ModSourceOrder.Length;
        }

        private static bool SameMod(ModFinder.Mod a, ModFinder.Mod b) {
            return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // == Checks =======================================================================================
    // Rules over the audited package's own tables. They never load anything: the export table and DependsMap are in
    // memory once the package has a linker, whether its objects are loaded or not. Every check is listed for every
    // package, so the checks can be learned and a clean result is shown, not implied.

    // What a check's findings mean.
    internal enum Severity { Warning, Info }

    // Declared in sort order: found something (the check's severity), couldn't run, ran clean, not run by design.
    internal enum CheckState { Warning, Info, NotRun, Passed, Skipped }

    internal sealed class CheckItem {
        public string       PathName; // in the audited package ("Pkg.Group.Obj")
        public string       ClassName;
        public long         Bytes;                      // its size, where the check counts it
        public List<string> Lines = new List<string>(); // the Detail column, top to bottom
    }

    internal abstract class Check {
        public List<CheckItem> Items = new List<CheckItem>();
        public string          Problem;  // why it couldn't run, or null
        public string          SkipNote; // why it isn't run on this kind of package, or null

        // Packages whose linkers must come back before it can run: its note lists them over Reload linkers.
        public List<string> Reloadable = new List<string>();

        // Packages to save, then reload, before it can run: its note lists them after the reloadable ones.
        public List<string> Unsaved = new List<string>();

        // Objects it couldn't read, so it can't run: its note lists them. A partial list would read as complete.
        public List<string> Unread = new List<string>();

        // Packages to load first (the mod view's, not in memory): its note lists them last.
        public List<string> NotLoaded = new List<string>();

        private string _skipSummary;

        public CheckState State {
            get {
                if (SkipNote != null) return CheckState.Skipped;
                if (Problem != null) return CheckState.NotRun;
                if (Items.Count == 0) return CheckState.Passed;
                return Severity == Severity.Warning ? CheckState.Warning : CheckState.Info;
            }
        }

        /// <summary>The row's second line.</summary>
        public string Summary {
            get {
                switch (State) {
                    case CheckState.Skipped:
                        return _skipSummary;
                    case CheckState.NotRun:
                        return NotRunSummary;
                    case CheckState.Passed:
                        return "Nothing found";
                    default:
                        return FoundSummary;
                }
            }
        }

        /// <summary>
        /// Whether its list can hide objects by class (the chips over it): Unreferenced, which lists archetypes and
        /// the like that config loads by name.
        /// </summary>
        public virtual bool HidesByClass {
            get { return false; }
        }

        /// <summary>
        /// Summary with the hidden classes' objects out of its counts, and counted after: "4 objects, 1.9 MB, 3
        /// hidden". Only a list that hides by class; the state stays, as hiding is the user's choice, not a pass.
        /// </summary>
        public string SummaryHiding(Predicate<string> hidden) {
            CheckState s = State;
            if (!HidesByClass || (s != CheckState.Warning && s != CheckState.Info)) return Summary;
            List<CheckItem> shown = Shown(hidden);
            int             n     = Items.Count - shown.Count;
            return n == 0 ? Summary : SummaryOf(shown) + ", " + n + " hidden";
        }

        /// <summary>Its items whose class isn't hidden; all of them unless it hides by class.</summary>
        public List<CheckItem> Shown(Predicate<string> hidden) {
            if (!HidesByClass) return Items;
            return Items.FindAll(i => !hidden(i.ClassName ?? ""));
        }

        // The row's line for these of its items.
        protected virtual string SummaryOf(List<CheckItem> items) {
            return Genie.TextUtils.Count(items.Count, ItemNoun);
        }

        /// <summary>The note box's text: why it wasn't run or couldn't run, else why it matters.</summary>
        public string NoteText {
            get { return SkipNote ?? Problem ?? Note; }
        }

        /// <summary>Grey text under the note, or null.</summary>
        public virtual string Footnote {
            get { return null; }
        }

        public abstract string   Name { get; }
        public abstract Severity Severity { get; }
        public abstract string   Rule { get; }      // what it lists, shown under the name
        public abstract string   Note { get; }      // why it matters, without repeating the rule
        public abstract string   ListLabel { get; } // over the table

        /// <summary>The rule over a whole mod, in the mod view.</summary>
        public virtual string ModRule {
            get { return Rule; }
        }

        /// <summary>What it lists, for "3 objects in 2 packages".</summary>
        public virtual string ItemNoun {
            get { return "object"; }
        }

        /// <summary>
        /// Whether a mod's packages answer it together (Unreferenced: another package may be the user), so in the mod
        /// view one that can't be audited blocks it. Otherwise each package answers alone and that one is named.
        /// </summary>
        public virtual bool SpansPackages {
            get { return false; }
        }

        protected abstract string FoundSummary { get; } // the row's second line when it lists something
        protected virtual string  NotRunSummary {
            get { return "Couldn’t run"; }
        }

        // Fills Items, sets Problem, or calls Skip.
        protected abstract void Run(IntPtr linker, PackageAudit audit);

        // Not run by design on this kind of package: the row's line ("Not run on maps") and the note's why. Neither
        // says anything about the package.
        protected void Skip(string summary, string note) {
            _skipSummary = summary;
            SkipNote     = note;
        }

        /// <summary>One of each check, not run.</summary>
        public static List<Check> NewAll() {
            return new List<Check> { new UiTextureCheck(), new UnreferencedCheck() };
        }

        /// <summary>Every check, run; by state, then name.</summary>
        public static List<Check> RunAll(IntPtr linker, PackageAudit audit) {
            List<Check> all = NewAll();
            foreach (Check c in all) c.Run(linker, audit);
            all.Sort((x, y) => x.State != y.State ? x.State.CompareTo(y.State)
                                                  : string.Compare(x.Name, y.Name, StringComparison.Ordinal));
            return all;
        }
    }

    // Assets nothing open in the editor references. Packages that aren't open, code and config can still use one,
    // which this can't see (code and config rejected: too volatile to search), so it's Info.
    //   Asset: an export at the package root or in a group, not a group or redirector.
    //   Referenced in the package: another asset, or a subobject of one, lists it (or a subobject of it) in
    //   DependsMap, as its class or as its archetype. Subobjects (path "Asset:Sub", outer not a group) count as their
    //   asset, both ways, so an asset's own components don't keep it referenced.
    //   Referenced elsewhere: an asset in another open package lists its import of it (or a subobject) the same way. A
    //   shared materials package is used only this way (AAASJ_Shared_Mats.Head_MAT, used by a mesh in another
    //   package). An import no export lists counts too: nothing says it's unused.
    //   Through a redirector, here or elsewhere: its references count only once something uses the redirector (an
    //   asset in its package, along chains, or any open package importing its path), so redirectors a move left
    //   behind don't keep their target off the list. Unused ones pointing at a listed asset are named in the Detail
    //   column: deleting it breaks them.
    // An open package without a linker has no tables to read, so any of them may use these assets. While one can be
    // reloaded, or saved and then reloaded, the check doesn't run: a list it can't vouch for invites deleting what a
    // saved package uses. One that can't (never saved, or its file gone) is only named: there's nothing to read it
    // from.
    internal sealed class UnreferencedCheck : Check {
        private const string RedirectorClass = "ObjectRedirector";

        private List<string> _unread = new List<string>(); // open packages with no linker and no file

        public override string Name {
            get { return "Unreferenced"; }
        }
        public override Severity Severity {
            get { return Severity.Info; }
        }
        public override string Rule {
            get { return "Rule: Objects nothing in this package or another open package references"; }
        }
        public override string Note {
            get {
                const string note =
                    "Possible bloat, but packages that aren’t open, code and config can still use these.";
                return _unread.Count == 0 ? note : note + " " + NotCounted;
            }
        }
        public override string Footnote {
            get { return Blocked && _unread.Count > 0 ? NotCounted : null; }
        }
        public override string ModRule {
            get { return "Rule: Objects nothing in this mod or another open package references"; }
        }
        public override bool SpansPackages {
            get { return true; }
        }
        private string NotCounted {
            get { return "Not counted, as they have no file to reload from: " + string.Join(", ", _unread) + "."; }
        }
        private bool Blocked {
            get { return Reloadable.Count > 0 || Unsaved.Count > 0; }
        }
        public override string ListLabel {
            get { return "Objects"; }
        }
        public override bool HidesByClass {
            get { return true; }
        }
        protected override string FoundSummary {
            get { return SummaryOf(Items); }
        }
        protected override string SummaryOf(List<CheckItem> items) {
            long bytes = 0;
            foreach (CheckItem i in items) bytes += i.Bytes;
            return Genie.TextUtils.Count(items.Count, "object") + ", " + Genie.TextUtils.Bytes(bytes);
        }
        protected override string NotRunSummary {
            get {
                if (Reloadable.Count > 0) return "Linkers unloaded";
                return Unsaved.Count > 0 ? "Unsaved changes" : "Couldn’t run";
            }
        }

        protected override void Run(IntPtr linker, PackageAudit audit) {
            if (string.Equals(Path.GetExtension(audit.File ?? ""), ".umap", StringComparison.OrdinalIgnoreCase)) {
                Skip("Not run on maps", "Not run on maps: a map’s actors belong to its level, which nothing in the " +
                                            "map references, so all would be listed.");
                return;
            }
            _unread    = audit.Unreloadable.FindAll(p => !Same(p, audit.Package));
            Reloadable = audit.Reloadable.FindAll(p => !Same(p, audit.Package));
            Unsaved    = audit.Unsaved.FindAll(p => !Same(p, audit.Package));
            if (Reloadable.Count > 0) {
                Problem = PackagePanel.ReloadHeading;
                return;
            }
            if (Unsaved.Count > 0) {
                Problem = PackagePanel.UnsavedHeading;
                return;
            }
            Usage own = new Usage(linker);
            if (!own.HasDependencies) {
                Problem = "This package’s dependency table isn’t in memory, so references inside it can’t be read.";
                return;
            }
            int[] unusedRedirectors = new int[own.Count]; // per asset
            MarkUsedElsewhere(own, audit.Package, unusedRedirectors);
            own.Follow(unusedRedirectors);

            for (int e = 0; e < own.Count; e++) {
                if (own.Owner[e] != e || own.Used[e] || own.IsRedirector(e)) continue;
                CheckItem item =
                    new CheckItem { PathName = own.PathOf(e), ClassName = own.Class[e], Bytes = own.Bytes[e] };
                item.Lines.Add("0 referencers"); // always 0; a reminder of what the rule counted
                item.Lines.Add(Genie.TextUtils.Bytes(own.Bytes[e]));
                if (unusedRedirectors[e] > 0)
                    item.Lines.Add(Genie.TextUtils.Count(unusedRedirectors[e], "unused redirector") + " pointing here");
                Items.Add(item);
            }
            Items.Sort((x, y) => string.Compare(x.PathName, y.PathName, StringComparison.OrdinalIgnoreCase));
        }

        // Marks the assets other open packages use, and counts their unused redirectors pointing here. Only packages
        // whose imports name this one are read in full. Import paths join every level with '.', export paths put ':'
        // before a subobject, so both are compared with '.' throughout.
        private static void MarkUsedElsewhere(Usage own, string package, int[] unusedRedirectors) {
            Dictionary<string, int>             byPath   = null; // built on the first importer found
            Dictionary<string, HashSet<string>> imported = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase); // IsImported's, per package
            foreach (IntPtr other in Genie.LinkerUtils.GetLinkers()) {
                if (other == own.Linker || !ImportsPackage(other, package)) continue;
                if (byPath == null) {
                    byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int e = 0; e < own.Count; e++) {
                        if (own.Owner[e] >= 0) byPath[own.PathOf(e).Replace(':', '.')] = own.Owner[e];
                    }
                }
                // Its imports of our assets: import index -> asset.
                Dictionary<int, int> ours    = new Dictionary<int, int>();
                int                  imports = Genie.LinkerUtils.GetImportCount(other);
                for (int i = 0; i < imports; i++) {
                    Genie.LinkerUtils.Import imp = Genie.LinkerUtils.GetImport(other, i);
                    int                      asset;
                    if (imp.PathName != null && Same(imp.PackageName, package) &&
                        byPath.TryGetValue(imp.PathName, out asset))
                        ours[i] = asset;
                }
                if (ours.Count == 0) continue;

                Usage theirs = new Usage(other);
                if (theirs.HasDependencies) {
                    for (int a = 0; a < theirs.Count; a++) {
                        if (theirs.Owner[a] == a && theirs.IsRedirector(a) && !theirs.Used[a] &&
                            IsImported(theirs.PathOf(a), imported))
                            theirs.Used[a] = true;
                    }
                    theirs.Follow(null);
                }
                foreach (KeyValuePair<int, int> o in ours) {
                    List<int> users;
                    if (!theirs.HasDependencies || !theirs.ImportUsers.TryGetValue(o.Key, out users)) {
                        own.Used[o.Value] = true; // can't tell what uses it
                        continue;
                    }
                    foreach (int u in users) {
                        if (u < 0 || !theirs.IsRedirector(u) || theirs.Used[u])
                            own.Used[o.Value] = true;
                        else
                            unusedRedirectors[o.Value]++;
                    }
                }
            }
        }

        // Whether any open package imports this export path. A package never imports itself.
        private static bool IsImported(string path, Dictionary<string, HashSet<string>> cache) {
            path           = path.Replace(':', '.');
            string package = path.Substring(0, path.IndexOf('.'));
            HashSet<string> paths;
            if (!cache.TryGetValue(package, out paths)) {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IntPtr l in Genie.LinkerUtils.GetLinkers()) {
                    if (!ImportsPackage(l, package)) continue;
                    int imports = Genie.LinkerUtils.GetImportCount(l);
                    for (int i = 0; i < imports; i++) {
                        Genie.LinkerUtils.Import imp = Genie.LinkerUtils.GetImport(l, i);
                        if (imp.PathName != null && Same(imp.PackageName, package)) paths.Add(imp.PathName);
                    }
                }
                cache[package] = paths;
            }
            return paths.Contains(path);
        }

        private static bool ImportsPackage(IntPtr linker, string package) {
            return Genie.LinkerUtils.GetImportedPackages(linker).Exists(p => Same(p, package));
        }

        private static bool Same(string a, string b) {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // One package's exports grouped by asset, and what each asset references, from the linker's tables. A
        // redirector's references in the package are held until Follow, once what uses it is known.
        private sealed class Usage {
            public readonly IntPtr   Linker;
            public readonly int      Count;
            public readonly int[]    Owner; // the asset an export belongs to; -1 for a group
            public readonly string[] Class;
            public readonly long[]   Bytes;                  // per asset: its exports' serial sizes
            public readonly bool[]   Used;                   // per asset
            public readonly bool     HasDependencies = true; // false when DependsMap isn't in memory; the rest unread

            // Import index -> the assets that list it (-1: a group).
            public readonly Dictionary<int, List<int>> ImportUsers = new Dictionary<int, List<int>>();

            private readonly List<int>[] _targets; // per redirector: the assets it points at

            public Usage(IntPtr linker) {
                Linker = linker;
                Count  = Genie.LinkerUtils.GetExportCount(linker);
                Owner  = new int[Count];
                Class  = new string[Count];
                Bytes  = new long[Count];
                Used   = new bool[Count];

                _targets    = new List<int>[Count];
                int[]  outer = new int[Count];
                bool[] group = new bool[Count];
                for (int e = 0; e < Count; e++) {
                    outer[e] = Genie.LinkerUtils.GetExportOuterIndex(linker, e);
                    Class[e] = Genie.LinkerUtils.GetExportClassName(linker, e);
                    group[e] = Class[e] == "Package";
                }
                for (int e = 0; e < Count; e++) Owner[e] = AssetOf(e, outer, group);

                for (int e = 0; e < Count; e++) {
                    int[] refs = Genie.LinkerUtils.GetExportDependencies(linker, e);
                    if (refs == null) {
                        HasDependencies = false;
                        return;
                    }
                    List<int> all = new List<int>(refs);
                    all.Add(Genie.LinkerUtils.GetExportClassIndex(linker, e));
                    all.Add(Genie.LinkerUtils.GetExportArchetypeIndex(linker, e));
                    int  from     = Owner[e];
                    bool redirect = from >= 0 && IsRedirector(from);
                    foreach (int r in all) {
                        if (r < 0) {
                            int       import = -r - 1;
                            List<int> users;
                            if (!ImportUsers.TryGetValue(import, out users)) {
                                users               = new List<int>();
                                ImportUsers[import] = users;
                            }
                            if (!users.Contains(from)) users.Add(from);
                            continue;
                        }
                        if (r == 0 || r > Count) continue; // none, or out of range
                        int target = Owner[r - 1];
                        if (target < 0 || target == from) continue;
                        if (!redirect) {
                            Used[target] = true;
                        } else {
                            if (_targets[from] == null) _targets[from] = new List<int>();
                            if (!_targets[from].Contains(target)) _targets[from].Add(target);
                        }
                    }
                    if (from >= 0) Bytes[from] += Genie.LinkerUtils.GetExportSerialSize(linker, e);
                }
            }

            public bool IsRedirector(int asset) {
                return Class[asset] == RedirectorClass;
            }

            public string PathOf(int export) {
                return Genie.LinkerUtils.GetExportPathName(Linker, export);
            }

            // Passes each used redirector's targets on as used, along chains. unused, if given: per asset, adds the
            // unused redirectors pointing at it.
            public void Follow(int[] unused) {
                Queue<int> live = new Queue<int>();
                for (int a = 0; a < Count; a++) {
                    if (Used[a] && _targets[a] != null) live.Enqueue(a);
                }
                while (live.Count > 0) {
                    foreach (int t in _targets[live.Dequeue()]) {
                        if (Used[t]) continue;
                        Used[t] = true;
                        if (_targets[t] != null) live.Enqueue(t);
                    }
                }
                if (unused == null) return;
                for (int a = 0; a < Count; a++) {
                    if (Used[a] || _targets[a] == null) continue;
                    foreach (int t in _targets[a]) unused[t]++;
                }
            }

            // The asset an export belongs to: itself when its outer is the package or a group, else its outer's
            // asset. -1 for a group.
            private static int AssetOf(int e, int[] outer, bool[] group) {
                int a = e;
                for (int guard = 0; guard < 64; guard++) {
                    int o = outer[a];
                    if (o <= 0 || o > outer.Length || group[o - 1]) break;
                    a = o - 1;
                }
                return group[a] ? -1 : a;
            }
        }
    }

    // UI textures (LODGroup TEXTUREGROUP_UI) not at the package root: once a mod cooks the package, one inside a group
    // shows the missing-image texture in game. Uncooked it works. A package in its mod's ContentForCook ships only
    // cooked (X2ModBuildCommon), so there it's a Warning; anywhere else only Info, "if". From the r/xcom2mods wiki's
    // cooking guide ("Cook the assets"): UI images must be YourPackage.YourImage, not YourPackage.Group.YourImage.
    // Stock packages are skipped: the game cooks its own grouped UI textures and they work
    // (UILibrary_Common.AlienWeapons.*, cooked into Startup.upk). Why mod-cooked ones differ isn't traced.
    // A loaded texture's group is read in memory, where unsaved edits are; any other's from the package file, which
    // loads nothing (LinkerUtils.SavedProperties). A texture saved at the default group has no tag: World.
    // Paths come from the export table, so a move must be saved first: moving resets the loaders, and a package with
    // unsaved changes and no linker blocks the view (Block.UnsavedChanges) until it's saved and reloaded.
    internal sealed class UiTextureCheck : Check {
        // TEXTUREGROUP_UI as the enum byte in memory (TextureLodProbe: file and memory agreed on AACSJ_UI and
        // UILibrary_DLC3Images), and as the name the file saves.
        private const int    UiGroupByte = 16;
        private const string UiGroupName = "TEXTUREGROUP_UI";

        public override string Name {
            get { return "UI texture inside a group"; }
        }
        public override Severity Severity {
            get { return _cooked ? Severity.Warning : Severity.Info; }
        }
        public override string Rule {
            get { return "Rule: UI textures outside the package root"; }
        }
        public override string Note {
            get { return _lead + " **cooks this package**, these would show the missing-image texture."; }
        }

        private bool   _cooked;                 // in its mod's ContentForCook
        private string         _lead = "If a mod ever"; // who cooks it: the mod, or no mod yet
        public override string ListLabel {
            get { return "Affected"; }
        }
        public override string ItemNoun {
            get { return "texture"; }
        }
        protected override string FoundSummary {
            get { return Genie.TextUtils.Count(Items.Count, "texture"); }
        }
        protected override string NotRunSummary {
            get { return _changed ? "Changed on disk" : "Textures unread"; }
        }

        private bool _changed; // the file was rewritten since its linker read it

        // Only textures with an outer are read: one at the root passes whatever its group. The file is opened once,
        // the first time a texture isn't in memory.
        protected override void Run(IntPtr linker, PackageAudit audit) {
            if (Locations.Of(audit.File).Kind == LocationKind.Stock) {
                Skip("Not run on stock packages",
                     "Skipped; the game ships grouped UI textures cooked. Ex: Startup.upk");
                return;
            }
            _cooked = audit.Mods.Exists(
                m => Genie.FileUtils.IsUnder(audit.File, Path.Combine(m.Root, "ContentForCook")));
            if (audit.Mods.Count > 0) _lead = _cooked ? "Since the mod" : "If the mod ever";
            int lodGroup = Genie.ObjectUtils.GetPropertyOffset("Engine.Texture.LODGroup");
            Genie.LinkerUtils.SavedProperties saved = null;
            string why = null; // why the file can't be read
            try {
                int count = Genie.LinkerUtils.GetExportCount(linker);
                for (int e = 0; e < count; e++) {
                    if (Genie.LinkerUtils.GetExportOuterIndex(linker, e) == 0) continue;
                    string cls = Genie.LinkerUtils.GetExportClassName(linker, e);
                    if (!IsTexture(cls)) continue;
                    string path = Genie.LinkerUtils.GetExportPathName(linker, e);
                    IntPtr obj  = Genie.ObjectUtils.FindObject(path);
                    bool   ui;
                    if (obj != IntPtr.Zero && lodGroup >= 0) {
                        ui = Genie.ObjectUtils.ReadByte(obj, lodGroup) == UiGroupByte;
                    } else {
                        if (saved == null && why == null)
                            saved = Genie.LinkerUtils.SavedProperties.Open(linker, out why);
                        List<Genie.LinkerUtils.PropertyTag> tags = saved == null ? null : saved.Read(e);
                        if (tags == null) {
                            Unread.Add(path);
                            continue;
                        }
                        ui = tags.Exists(t => t.Name == "LODGroup" && t.Value == UiGroupName);
                    }
                    if (!ui) continue;
                    CheckItem item = new CheckItem { PathName = path, ClassName = cls };
                    item.Lines.Add("expected at root as " + Genie.PathUtils.GetLeafName(path.Replace(':', '.')));
                    Items.Add(item);
                }
            } finally {
                if (saved != null) saved.Dispose();
            }
            // Blocked, not a partial list. Fully loaded, they're read in memory. The note claims a cause only when it
            // knows it (the file changed); the log names the rest.
            if (Unread.Count > 0) {
                _changed = why == Genie.LinkerUtils.SavedProperties.FileChanged;
                Problem  = _changed
                               ? "This package has changed on disk. Fully load it again to read these textures:"
                               : "These textures couldn’t be read from the file. Fully load the package to read them:";
                Genie.Log(Name + ": couldn't read " + Genie.TextUtils.Count(Unread.Count, "texture") + " in " +
                          audit.Package + ": " + (why ?? "their saved data didn't parse") + ".");
                Unread.Sort(StringComparer.OrdinalIgnoreCase);
            }
            Items.Sort((x, y) => string.Compare(x.PathName, y.PathName, StringComparison.OrdinalIgnoreCase));
        }

        // Texture's subclasses: Texture2D, TextureCube, TextureMovie, TextureRenderTarget2D, LightMapTexture2D...
        private static bool IsTexture(string cls) {
            return cls != null && (cls.StartsWith("Texture", StringComparison.Ordinal) ||
                                   cls.EndsWith("Texture2D", StringComparison.Ordinal));
        }
    }

    // == Mod Audit ====================================================================================
    // Every package in one mod's folder, audited as the package view does, and what they need from outside the mod.
    // Nothing is loaded for it: a mod's packages can depend on packages scattered across folders, so loading them
    // isn't reliable. A package not in memory is listed, blocked, for the user to load.

    internal sealed class ModAudit {
        public ModFinder.Mod       Mod;
        public List<PackageAudit>  Packages     = new List<PackageAudit>(); // by name
        public List<ModDependency> Dependencies = new List<ModDependency>();
        public List<Check>         Checks       = new List<Check>(); // ModChecks, by state then name

        // Open packages with no linker, any audit's (each finds them all): Reload linkers takes them.
        public List<string> Reloadable = new List<string>();

        public static ModAudit Run(ModFinder.Mod mod) {
            ModAudit a = new ModAudit { Mod = mod };
            foreach (KeyValuePair<string, string> p in PackageFiles(mod.Root)) {
                // Not in memory: the file found here, not the cache's, which may be another copy or none.
                bool inMemory = Genie.ObjectUtils.FindPackage(p.Key) != IntPtr.Zero;
                try {
                    a.Packages.Add(inMemory ? PackageAudit.Run(p.Key) : PackageAudit.NotLoadedAt(p.Key, p.Value));
                } catch (Exception ex) {
                    a.Packages.Add(new PackageAudit { Package = p.Key, File = p.Value,
                                                      Problem = "Couldn’t audit it: " + ex.Message });
                    Genie.Log("Couldn't audit " + p.Key + " for mod " + mod.Name + ": " + ex);
                }
            }
            PackageAudit any = a.Packages.Find(x => x.Problem == null);
            if (any != null) a.Reloadable = any.Reloadable;
            a.FindDependencies();
            a.Checks = ModCheck.RunAll(a);
            return a;
        }

        public bool Owns(string package) {
            return Packages.Exists(p => string.Equals(p.Package, package, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Audited: not blocked, and nothing stopped it.</summary>
        public static bool Audited(PackageAudit p) {
            return p.Problem == null && p.Blocked == PackageAudit.Block.None;
        }

        // Package name -> file, every .upk and .umap under the mod's root, by name. The first found keeps a name: a
        // project holds one copy of each.
        private static SortedDictionary<string, string> PackageFiles(string root) {
            SortedDictionary<string, string> files =
                new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pattern in new[] { "*.upk", "*.umap" }) {
                string[] found;
                try {
                    found = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
                } catch (Exception ex) {
                    Genie.Log("Couldn't list the packages in " + root + ": " + ex.Message);
                    continue;
                }
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                foreach (string f in found) {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (!files.ContainsKey(name)) files[name] = f;
                }
            }
            return files;
        }

        // What players need beyond this mod: other mods (one row per mod), and packages that didn't load, the game
        // ships only cooked, or no known mod ships. The mod's own packages, and stock it ships a copy of, aren't.
        private void FindDependencies() {
            Dictionary<string, ModDependency> byKey =
                new Dictionary<string, ModDependency>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageAudit user in Packages) {
                if (!Audited(user)) continue;
                foreach (Dependency d in user.Dependencies) {
                    if (Owns(d.Package) || d.ShippedByMod) continue;
                    if (d.Bucket == Bucket.SameMod || d.Bucket == Bucket.Stock) continue;
                    bool          isMod = d.Bucket == Bucket.OtherMod;
                    string        key   = isMod ? "mod:" + d.Mod : d.Package;
                    ModDependency md;
                    if (!byKey.TryGetValue(key, out md)) {
                        md = new ModDependency { Bucket = d.Bucket, IsMod = isMod, Name = isMod ? d.Mod : d.Package };
                        byKey[key] = md;
                        Dependencies.Add(md);
                    }
                    md.Uses.Add(new ModDependency.Use { User = user, Dep = d });
                    if (isMod) {
                        foreach (ModFinder.Mod m in d.Mods) {
                            if (string.Equals(m.Name, d.Mod, StringComparison.OrdinalIgnoreCase) &&
                                !md.Roots.Exists(r => string.Equals(r, m.Root, StringComparison.OrdinalIgnoreCase)))
                                md.Roots.Add(m.Root);
                        }
                    }
                }
            }
            Dependencies.Sort((x, y) => x.Bucket != y.Bucket
                                            ? x.Bucket.CompareTo(y.Bucket)
                                            : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    // Something a mod's packages use from outside it: a package, or for Other mod the mod, its packages inside.
    internal sealed class ModDependency {
        public sealed class Use {
            public PackageAudit User; // the mod's package
            public Dependency   Dep;  // what it uses, as its audit has it
        }

        public Bucket       Bucket;
        public bool         IsMod;
        public string       Name;                       // the mod, or the package
        public List<string> Roots = new List<string>(); // Other mod: its folders
        public List<Use>    Uses  = new List<Use>();

        /// <summary>The package's own Dependency, for its look and copies. A mod's: its first package's.</summary>
        public Dependency First {
            get { return Uses[0].Dep; }
        }

        public int RefCount {
            get {
                int n = 0;
                foreach (Use u in Uses) n += u.Dep.RefCount;
                return n;
            }
        }
    }

    // One check over a whole mod, from its packages' own runs. Its items are theirs, each path naming its package.
    //   Spanning packages (Unreferenced): any package that can't be audited blocks it, as it may be the only user.
    //   Otherwise each package answers alone: one that can't be audited is named, the rest still listed.
    // One package that couldn't run blocks it either way: a partial list would read as complete.
    internal sealed class ModCheck : Check {
        private readonly Check        _template;
        private readonly List<Check>  _runs      = new List<Check>();  // its packages' own
        private readonly List<string> _unchecked = new List<string>(); // packages not audited: "Pkg.upk (not loaded)"

        private string   _note;
        private Severity _severity;

        public override string Name {
            get { return _template.Name; }
        }
        public override Severity Severity {
            get { return _severity; }
        }
        public override string Rule {
            get { return _template.ModRule; }
        }
        public override string Note {
            get { return _note; }
        }
        public override string ListLabel {
            get { return _template.ListLabel; }
        }
        public override string Footnote {
            get {
                if (_unchecked.Count == 0) return null;
                return "Not checked, as it can’t be audited: " + string.Join(", ", _unchecked) + ".";
            }
        }
        public override bool HidesByClass {
            get { return _template.HidesByClass; }
        }
        protected override string FoundSummary {
            get { return SummaryOf(Items); }
        }
        protected override string SummaryOf(List<CheckItem> items) {
            HashSet<string> packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CheckItem i in items) packages.Add(i.PathName.Split('.')[0]);
            return Genie.TextUtils.Count(items.Count, _template.ItemNoun) + " in " +
                   Genie.TextUtils.Count(packages.Count, "package");
        }
        protected override string NotRunSummary {
            get {
                if (NotLoaded.Count > 0 && Reloadable.Count == 0 && Unsaved.Count == 0) return "Packages not loaded";
                if (Reloadable.Count > 0) return "Linkers unloaded";
                return Unsaved.Count > 0 ? "Unsaved changes" : "Couldn’t run";
            }
        }

        private ModCheck(Check template) {
            _template = template;
            _severity = template.Severity;
            _note     = template.Note;
        }

        protected override void Run(IntPtr linker, PackageAudit audit) {} // built from the packages' runs

        /// <summary>Every check over the mod; by state, then name. Listed with no package audited too.</summary>
        public static List<Check> RunAll(ModAudit mod) {
            List<Check> all = new List<Check>();
            foreach (Check template in NewAll()) {
                ModCheck m = new ModCheck(template);
                foreach (PackageAudit p in mod.Packages) {
                    if (ModAudit.Audited(p)) m._runs.AddRange(p.Checks.FindAll(c => c.Name == template.Name));
                }
                m.Finish(mod);
                all.Add(m);
            }
            all.Sort((x, y) => x.State != y.State ? x.State.CompareTo(y.State)
                                                  : string.Compare(x.Name, y.Name, StringComparison.Ordinal));
            return all;
        }

        private void Finish(ModAudit mod) {
            bool spans = _template.SpansPackages;
            foreach (PackageAudit p in mod.Packages) {
                if (ModAudit.Audited(p)) continue;
                string file = Look.FileName(p.Package, p.File);
                string why  = p.Problem != null ? "couldn’t audit" : Look.TitleOf(p.Blocked).ToLowerInvariant();
                if (!spans)
                    _unchecked.Add(file + " (" + why + ")");
                else if (p.Blocked == PackageAudit.Block.NotLoaded)
                    NotLoaded.Add(p.Package);
                else if (p.Blocked == PackageAudit.Block.LinkerUnloaded)
                    Union(Reloadable, new List<string> { p.Package });
                else if (p.Blocked == PackageAudit.Block.UnsavedChanges)
                    Union(Unsaved, new List<string> { p.Package });
                else
                    _unchecked.Add(file + " (" + why + ")"); // nothing to read it from, as Unreferenced's own
            }
            foreach (Check c in _runs) {
                if (c.State == CheckState.NotRun) {
                    if (Problem == null) Problem = c.Problem;
                    Union(Reloadable, c.Reloadable);
                    Union(Unsaved, c.Unsaved);
                    Unread.AddRange(c.Unread);
                }
            }
            // With no run to say so (every package blocked), the lists pick the line, as Unreferenced's own.
            if (Problem == null && Reloadable.Count > 0) Problem = PackagePanel.ReloadHeading;
            if (Problem == null && Unsaved.Count > 0) Problem = PackagePanel.UnsavedHeading;
            if (Problem == null && NotLoaded.Count > 0) Problem = PackagePanel.NotLoadedHeading;
            if (Problem != null) return;

            List<Check> ran = _runs.FindAll(c => c.State != CheckState.Skipped);
            if (ran.Count == 0 && _runs.Count > 0) {
                Skip(_runs[0].Summary, _runs[0].SkipNote);
                return;
            }
            foreach (Check c in ran) {
                Items.AddRange(c.Items);
                if (c.Severity == Severity.Warning) {
                    _severity = Severity.Warning;
                    _note     = c.Note;
                }
            }
            Items.Sort((x, y) => string.Compare(x.PathName, y.PathName, StringComparison.OrdinalIgnoreCase));
        }

        private static void Union(List<string> into, List<string> from) {
            foreach (string s in from) {
                if (!into.Exists(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) into.Add(s);
            }
        }
    }

    // == Mod Identity =================================================================================
    // A mod's root holds <Name>.x2proj (ModBuddy) or <Name>.XComMod (built or installed). One finder per audit,
    // so markers added since are seen.

    internal sealed class ModFinder {
        public sealed class Mod {
            public string Name;
            public string Root; // the folder holding the marker
        }

        private readonly Dictionary<string, Mod> _byFolder =
            new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase); // null value: no mod above

        /// <summary>The mod the file belongs to (nearest marker going up), or null.</summary>
        public Mod Find(string file) {
            if (string.IsNullOrEmpty(file)) return null;
            List<string> visited = new List<string>();
            Mod          mod     = null;
            for (string folder = Path.GetDirectoryName(file); folder != null; folder = Path.GetDirectoryName(folder)) {
                if (_byFolder.TryGetValue(folder, out mod)) break;
                visited.Add(folder);
                string name = Marker(folder);
                if (name == null) continue;
                mod = new Mod { Name = name, Root = folder };
                break;
            }
            foreach (string folder in visited) _byFolder[folder] = mod;
            return mod;
        }

        private static string Marker(string folder) {
            try {
                foreach (string pattern in new[] { "*.x2proj", "*.XComMod" }) {
                    string[] found = Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
                    // The build names the .XComMod after the project minus whitespace and ';' (X2ModBuildCommon's
                    // modNameCanonical), so compare that form or a project and its build look like two mods.
                    if (found.Length > 0)
                        return Regex.Replace(Path.GetFileNameWithoutExtension(found[0]), @"[\s;]", "");
                }
            } catch (Exception) {} // unreadable or vanished folder: keep walking up
            return null;
        }
    }

    // == Presentation =================================================================================
    // What each bucket and location looks like and says.

    internal static class Look {
        // A dependency's row tag. Stock unshipped that this mod ships is resolved: its own green tag.
        public static Genie.ControlUtils.TagTone ToneOf(Dependency d) {
            return d.ShippedByMod ? Genie.ControlUtils.TagTone.Success : ToneOf(d.Bucket);
        }

        public static string LabelOf(Dependency d) {
            return d.ShippedByMod ? "Shipped by mod" : LabelOf(d.Bucket);
        }

        public static string TipOf(Dependency d) {
            return d.ShippedByMod ? "Game only ships it cooked; this mod ships a copy" : TipOf(d.Bucket);
        }

        public static Genie.ControlUtils.TagTone ToneOf(Bucket b) {
            switch (b) {
                case Bucket.NotLoaded:
                case Bucket.StockUnshipped:
                    return Genie.ControlUtils.TagTone.Danger;
                case Bucket.OtherMod:
                case Bucket.Loose:
                    return Genie.ControlUtils.TagTone.Caution;
                case Bucket.SameMod:
                    return Genie.ControlUtils.TagTone.Success;
                default:
                    return Genie.ControlUtils.TagTone.Neutral;
            }
        }

        public static string LabelOf(Bucket b) {
            switch (b) {
                case Bucket.NotLoaded:
                    return "Not loaded";
                case Bucket.StockUnshipped:
                    return "Stock unshipped";
                case Bucket.OtherMod:
                    return "Other mod";
                case Bucket.Loose:
                    return "Loose";
                case Bucket.SameMod:
                    return "Same mod";
                default:
                    return "Stock";
            }
        }

        public static string TipOf(Bucket b) {
            switch (b) {
                case Bucket.NotLoaded:
                    return "Editor couldn't load it";
                case Bucket.StockUnshipped:
                    return "Game only ships it cooked";
                case Bucket.OtherMod:
                    return "Players need this mod";
                case Bucket.Loose:
                    return "Not part of any mod";
                case Bucket.SameMod:
                    return "Part of this mod";
                default:
                    return "Ships with the game";
            }
        }

        /// <summary>The note box's text. Stock unshipped names the maps it's forced into, if any.</summary>
        public static string NoteOf(Dependency d) {
            if (d.Bucket != Bucket.StockUnshipped) return NoteOf(d.Bucket);
            string where = d.Maps != null && d.Maps.Length > 0 ? Series(d.Maps) : "the maps that use it";
            return "The game cooks this into " + where + ". " +
                   (d.ShippedByMod ? "This mod ships its own copy." : "Uncooked mods must ship it.");
        }

        // "A", "A and B", "A, B and C"
        private static string Series(string[] items) {
            if (items.Length == 1) return items[0];
            return string.Join(", ", items, 0, items.Length - 1) + " and " + items[items.Length - 1];
        }

        public static string NoteOf(Bucket b) {
            switch (b) {
                case Bucket.NotLoaded:
                    return "The editor couldn’t load this package, so these references are broken.";
                case Bucket.StockUnshipped:
                    return "The game cooks this into the maps that use it. Uncooked mods must ship it.";
                case Bucket.OtherMod:
                    return "Players will need this other mod enabled for these references to resolve.";
                case Bucket.Loose:
                    return "No copy is in a known mod, so there’s no telling which mod ships it.";
                case Bucket.SameMod:
                    return "This is another package in this mod.";
                default:
                    return "Stock content. Ships with the game.";
            }
        }

        // A check's row tag, dot and note. Orange: likely breaks in game. Blue: worth a look. Amber: couldn't run,
        // nothing broken. Green: ran clean. Grey with a hollow dot: not run on this kind of package, so it claims
        // nothing about it.
        public static Genie.ControlUtils.TagTone ToneOf(CheckState s) {
            switch (s) {
                case CheckState.Warning:
                    return Genie.ControlUtils.TagTone.Warning;
                case CheckState.Info:
                    return Genie.ControlUtils.TagTone.Info;
                case CheckState.NotRun:
                    return Genie.ControlUtils.TagTone.Caution;
                case CheckState.Passed:
                    return Genie.ControlUtils.TagTone.Success;
                default:
                    return Genie.ControlUtils.TagTone.Neutral;
            }
        }

        public static string LabelOf(CheckState s) {
            return s == CheckState.NotRun ? "Not run" : s.ToString();
        }

        public static string TipOf(CheckState s) {
            switch (s) {
                case CheckState.Warning:
                    return "Likely breaks in game";
                case CheckState.Info:
                    return "Worth a look";
                case CheckState.NotRun:
                    return "Couldn't run; see why";
                case CheckState.Passed:
                    return "Checked: nothing found";
                default:
                    return "Not run on this kind of package";
            }
        }

        // The blocked view's card. Amber throughout: the audit is incomplete, nothing is broken.
        public static string TitleOf(PackageAudit.Block b) {
            switch (b) {
                case PackageAudit.Block.LinkerUnloaded:
                    return "Linker unloaded";
                case PackageAudit.Block.FileMissing:
                    return "File missing";
                case PackageAudit.Block.NotLoaded:
                    return "Not loaded";
                case PackageAudit.Block.UnsavedChanges:
                    return "Unsaved changes";
                default:
                    return "Not saved yet";
            }
        }

        // Unsaved changes shares Not saved yet's icon: both are fixed by saving.
        public static Genie.ControlUtils.DialogSymbol SymbolOf(PackageAudit.Block b) {
            switch (b) {
                case PackageAudit.Block.LinkerUnloaded:
                case PackageAudit.Block.NotLoaded:
                    return Genie.ControlUtils.DialogSymbol.Reload;
                case PackageAudit.Block.FileMissing:
                    return Genie.ControlUtils.DialogSymbol.FileMissing;
                default:
                    return Genie.ControlUtils.DialogSymbol.FileNew;
            }
        }

        /// <summary>The header's blocked pill: its title is the block's, this says why.</summary>
        public static string BlockedTip(PackageAudit.Block b) {
            switch (b) {
                case PackageAudit.Block.LinkerUnloaded:
                    return "Its linker was unloaded, so it can't be audited";
                case PackageAudit.Block.FileMissing:
                    return "Its file is missing, so it can't be audited";
                case PackageAudit.Block.NotLoaded:
                    return "It isn't loaded, so it can't be audited";
                case PackageAudit.Block.UnsavedChanges:
                    return "It has unsaved changes, so it can't be audited";
                default:
                    return "It has no file yet, so it can't be audited";
            }
        }

        /// <summary>The mod view's pill for its packages blocked this way: "2 packages not loaded".</summary>
        public static string PackagesBlocked(PackageAudit.Block b, int count) {
            string n = Genie.TextUtils.Count(count, "package");
            switch (b) {
                case PackageAudit.Block.LinkerUnloaded:
                    return n + " with linker unloaded";
                case PackageAudit.Block.FileMissing:
                    return n + " with file missing";
                case PackageAudit.Block.NotLoaded:
                    return n + " not loaded";
                case PackageAudit.Block.UnsavedChanges:
                    return n + " with unsaved changes";
                default:
                    return n + " not saved yet";
            }
        }

        // Locations: green is where packages should come from, red is a folder that misleads.
        public static Genie.ControlUtils.TagTone ToneOf(LocationKind k) {
            switch (k) {
                case LocationKind.ModBuddy:
                case LocationKind.Stock:
                    return Genie.ControlUtils.TagTone.Success;
                case LocationKind.Unknown:
                    return Genie.ControlUtils.TagTone.Caution;
                case LocationKind.SdkMod:
                    return Genie.ControlUtils.TagTone.Warning;
                case LocationKind.Artifact:
                case LocationKind.OldSdk:
                    return Genie.ControlUtils.TagTone.Danger;
                default:
                    return Genie.ControlUtils.TagTone.Neutral;
            }
        }

        public static string TipOf(LocationKind k) {
            switch (k) {
                case LocationKind.ModBuddy:
                    return "Your mod projects";
                case LocationKind.Stock:
                    return "Ships with the game";
                case LocationKind.Workshop:
                    return "Steam Workshop subscriptions";
                case LocationKind.LocalMod:
                    return "Player mod directory";
                case LocationKind.Custom:
                    return "Your own source";
                case LocationKind.Unknown:
                    return "Matches no known folder";
                case LocationKind.SdkMod:
                    return "Listed in the Content Browser";
                case LocationKind.Artifact:
                    return "Edits here are lost";
                default:
                    return "Unsupported";
            }
        }

        public static string MeaningOf(LocationKind k) {
            switch (k) {
                case LocationKind.ModBuddy:
                    return "Your mod projects. Builds copy from here.";
                case LocationKind.Stock:
                    return "Ships with the game.";
                case LocationKind.Workshop:
                    return "Mods subscribed to on the Steam Workshop.";
                case LocationKind.LocalMod:
                    return "Player mod directory.";
                case LocationKind.Custom:
                    return "Your own source, added in Source Folders.";
                case LocationKind.Unknown:
                    return "Matches none of these folders. The full path is shown instead.";
                case LocationKind.SdkMod:
                    return "Partially loaded into the editor on startup.";
                case LocationKind.Artifact:
                    return "Mirrored to while compiling mods. Replaced on every build, so edits made here are lost.";
                default:
                    return "Vanilla XCOM 2 SDK. Unsupported.";
            }
        }

        /// <summary>A light outlined source tag ("Workshop"); the loaded copy gets a chip icon.</summary>
        public static GenieTag SourceTag(string label, bool loaded) {
            return Genie.ControlUtils.CreateTag(label, size: Genie.ControlUtils.TagSize.Small, outlined: true,
                                                icon: loaded ? Genie.ControlUtils.CreateChipIcon() : null);
        }

        /// <summary>A package as its file reads: "Pkg.upk", or "Pkg.umap" for a map. Not loaded: .upk.</summary>
        public static string FileName(string package, string file) {
            string ext = file == null ? "" : Path.GetExtension(file);
            return package + (ext.Length > 0 ? ext : ".upk");
        }
    }

    /// <summary>One copy of a package on disk.</summary>
    internal sealed class Copy {
        public string   File;
        public Location At;
        public bool     IsLoaded;

        /// <summary>
        /// The loaded file first, then every other copy found on disk, best location first. loaded false: the package
        /// isn't in memory, so loadedFile is only the file it would load from, listed first but not marked loaded.
        /// </summary>
        public static List<Copy> Of(string packageName, string loadedFile, bool loaded = true) {
            List<Copy> copies = new List<Copy>();
            if (loadedFile != null)
                copies.Add(new Copy { File = loadedFile, At = Locations.Of(loadedFile), IsLoaded = loaded });
            List<Copy> others = new List<Copy>();
            foreach (string file in CopyIndex.Find(packageName)) {
                if (loadedFile != null && string.Equals(file, loadedFile, StringComparison.OrdinalIgnoreCase)) continue;
                others.Add(new Copy { File = file, At = Locations.Of(file) });
            }
            others.Sort((x, y) => x.At.Kind.CompareTo(y.At.Kind));
            copies.AddRange(others);
            return copies;
        }
    }

    // == Package View =================================================================================

    internal sealed class PackagePanel : UserControl {
        private const double  DefaultSplit = 0.37; // the mockup's 342px list at the default width
        private const string  SelectHint   = "Select a dependency to see what this package uses from it.";
        internal const string ReloadNote   = "Reloads them all.";

        // A blocked check's line over the packages to reload.
        internal const string ReloadHeading =
            "Until these unloaded linkers are reloaded, their references can't be computed:";

        // A blocked check's line over the packages to save first; its Problem when nothing can be reloaded yet.
        internal const string UnsavedHeading =
            "These packages have unsaved changes in memory. Save them, then reload linkers:";

        // The same for packages not in memory (the mod view's), after any of the above.
        internal const string NotLoadedHeading =
            "These packages aren't loaded. Fully load them in the Content Browser:";

        private PackageAudit                _audit;
        private Action<ModFinder.Mod, bool> _openMod;

        // Header
        private readonly GenieHeader    _header;
        private readonly TextBlock      _modCaption;
        private readonly ContentControl _modName; // a link to the mod's folder, or plain text for none
        private readonly StackPanel     _modLine, _packageTags;
        private readonly GenieCountPill _sourcesPill, _warningsPill, _unshippedPill, _notLoadedPill, _blockedPill;
        private readonly Genie.ContentBrowserUtils.PackageFollower _follower;

        // List and details
        private readonly GenieListSection _dependencies, _checks;
        private readonly Button           _viewMod;
        private readonly GenieFilterChips _classChips; // Unreferenced's, shared with the mod view
        private readonly GenieMenu        _modMenu; // with more than one mod, View mod picks
        private readonly GenieList        _list;
        private readonly GenieEmptyState  _emptyState;
        private readonly ScrollViewer     _details;
        private readonly StackPanel       _detailsBody;

        // Blocked view: the panes as placeholders under a veil, one card on it.
        private readonly Grid           _veil;
        private readonly Border         _placeholders; // the details pane's stand-in
        private readonly GenieTitledCard _card;

        // Reload linkers pulses until clicked, and again once a package newly needs it.
        private List<string> _reloadable = new List<string>(); // as at the last audit
        private bool         _pulse;

        // Notifications, no polling:
        //   followed package changes           -> rebuild for the new package (the follower minds the pin)
        //   followed package modified or saved -> rebuild, keeping the selection
        //   folder settings saved              -> look for copies again, rebuild
        //   Reload linkers clicked             -> reload, rebuild
        // Each rebuild finds the unlinked packages again; saving another package isn't heard until then.

        // openMod: show the mod view, pinned if this view is.
        internal PackagePanel(GenieWindow window, Action openFolders, Action openLocations,
                              Action<ModFinder.Mod, bool> openMod) {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header: PACKAGE | MOD name and source tags, over the package's file name and pills; Reload linkers,
            // then refresh, pin and the gear right.
            _header                          = Genie.ControlUtils.CreatePanelHeader(window, "PACKAGE");
            _header.ShowFileExtension        = true;
            _header.Controls.RefreshToolTip  = "Audit the package again, and look for copies on disk.";
            _header.Controls.Refresh        += delegate {
                CopyIndex.Rebuild(RefreshKeepingSelection);
                _follower.Update();
                Refresh(null);
            };
            _header.Controls.Settings.AddMenuItem("Set source folders…", "Where to look for mods", openFolders);
            _header.Controls.Settings.AddMenuItem("About Sources", "What each source tag means", openLocations);

            // One mod: straight to it. More (copies in mods of other names): a menu, best source first.
            _viewMod             = Genie.ControlUtils.CreateButton("View mod");
            _viewMod.Margin      = new Thickness(0, 0, 8, 0);
            _viewMod.Visibility  = Visibility.Collapsed;
            _modMenu             = Genie.ControlUtils.CreateMenu(_viewMod);
            _viewMod.Click      += delegate {
                if (_audit == null || _audit.Mods.Count == 0) return;
                if (_audit.Mods.Count == 1)
                    openMod(_audit.Mods[0], _header.Controls.IsPinned);
                else
                    _modMenu.Open();
            };
            _header.AddAction(_viewMod);
            _openMod = openMod;

            _modLine       = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
            Border divider = Genie.ControlUtils.CreateSeparator(Orientation.Vertical);
            divider.Height = 11;
            divider.Margin = new Thickness(10, 0, 10, 0);
            divider.VerticalAlignment = VerticalAlignment.Center;
            _modLine.Children.Add(divider);
            _modCaption        = Genie.ControlUtils.CreateCaption("MOD");
            _modCaption.Margin = new Thickness(0, 0, 7, 0);
            _modLine.Children.Add(_modCaption);
            _modName = new ContentControl { Focusable = false, Margin = new Thickness(0, 0, 7, 0) };
            _modLine.Children.Add(_modName);
            _packageTags = new StackPanel { Orientation = Orientation.Horizontal };
            _modLine.Children.Add(_packageTags);
            _header.CaptionExtra = _modLine;

            _sourcesPill = Genie.ControlUtils.CreateCountPill("");
            _header.AddPill(_sourcesPill);
            _warningsPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Warning, true);
            _header.AddPill(_warningsPill);
            _unshippedPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Danger, true);
            _header.AddPill(_unshippedPill);
            _notLoadedPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Danger, true);
            _header.AddPill(_notLoadedPill);
            _blockedPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Caution);
            _header.AddPill(_blockedPill);
            Grid.SetRow(_header, 0);
            root.Children.Add(_header);

            _follower                 = Genie.ContentBrowserUtils.FollowPackage(_header);
            _follower.PackageChanged += delegate {
                Refresh(null);
            };
            _follower.PackageModified += RefreshKeepingSelection;

            // Package Loader's boxed list, its sections as header rows. The window's addon info sits over the box's
            // bottom-left corner.
            GenieListPane left      = Genie.ControlUtils.CreateListPane("Dependencies", true);
            left.Margin             = new Thickness(10, 10, 0, 10);
            _list                   = left.List;
            _dependencies           = _list.AddSection("Dependencies");
            _dependencies.EmptyText = "None.";
            _checks                 = _list.AddSection("Checks");
            _checks.EmptyText       = "None.";
            _list.SelectionChanged += delegate {
                ShowDetails();
            };
            _classChips                = HiddenClassChips();
            _classChips.HiddenChanged += ShowHiding;

            // The pane fills its column so the details' scrollbar sits on the window edge; the content inside keeps
            // the same inset from the splitter as from that scrollbar. A margin here would inset the scrollbar too.
            Grid      right = new Grid();
            Thickness inset = Genie.Theme.PaneInset;
            _emptyState     = Genie.ControlUtils.CreateEmptyState(
                "What’s a dependency?",
                "A package that this one uses objects from. Players need every one of them, from the game or a mod.",
                SelectHint);
            _emptyState.Margin = inset;
            right.Children.Add(_emptyState);

            _detailsBody = new StackPanel { Margin = inset };
            _details     = new ScrollViewer {
                Content                       = _detailsBody,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            right.Children.Add(_details);
            _placeholders = DetailsPlaceholder();
            right.Children.Add(_placeholders);

            Grid body = Genie.ControlUtils.CreateSplitPanel(left, right, DefaultSplit, 240, 320);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // The card: icon and title, then what to do, which depends on why (FillCard).
            _card            = Genie.ControlUtils.CreateTitledCard(400);
            _veil            = Genie.ControlUtils.CreateVeil(_card);
            _veil.Visibility = Visibility.Collapsed;
            Grid.SetRow(_veil, 1);
            root.Children.Add(_veil);
            Content = root;

            Genie.ControlUtils.WhileLoaded(this, Attach, Detach);
        }

        // == Notifications ============================================================================

        // The follower watches the Content Browser; this watches the folder settings.
        private void Attach() {
            FolderSettings.Changed += OnFoldersChanged;
            if (!CopyIndex.IsReady) CopyIndex.Rebuild(RefreshKeepingSelection);
        }

        private void Detach() {
            FolderSettings.Changed -= OnFoldersChanged;
        }

        private void OnFoldersChanged() {
            CopyIndex.Rebuild(RefreshKeepingSelection);
            RefreshKeepingSelection();
        }

        private void RefreshKeepingSelection() {
            if (!IsLoaded) return; // the index finished after the window closed
            Dependency d = Selected();
            Check      c = _list.SelectedTag<Check>();
            Refresh(d != null ? d.Package : c != null ? c.Name : null);
        }

        // == List =====================================================================================

        // Audits the followed package, reselecting keep if it's still there: a dependency's package or a check's
        // name.
        private void Refresh(string keep) {
            string pkg = _follower.Package;
            _dependencies.ClearItems();
            _checks.ClearItems();
            _checks.CountPill.Tone = Genie.ControlUtils.TagTone.Neutral; // ClearItems resets only its text
            _audit                 = null;
            ShowBlocked(null);
            ShowEmpty(SelectHint);
            UpdateHeader(null);
            if (pkg == null) return; // the header says so

            try {
                _audit = PackageAudit.Run(pkg);
            } catch (Exception ex) {
                ShowEmpty("Couldn’t audit the package: " + ex.Message);
                Genie.Log("Couldn't audit " + pkg + ": " + ex);
                return;
            }
            if (!_audit.Reloadable.TrueForAll(p => _reloadable.Contains(p))) _pulse = true;
            _reloadable = _audit.Reloadable;
            if (_audit.Problem != null) {
                ShowEmpty(_audit.Problem);
                return;
            }

            UpdateHeader(_audit);
            if (_audit.Blocked != PackageAudit.Block.None) {
                ShowBlocked(_audit);
                return;
            }
            ListBoxItem reselect = null;
            foreach (Dependency d in _audit.Dependencies) {
                ListBoxItem li = _dependencies.AddItem(Row(d), d);
                li.Padding     = new Thickness(4, 5, 4, 5);
                if (keep != null && string.Equals(d.Package, keep, StringComparison.OrdinalIgnoreCase)) reselect = li;
            }
            foreach (Check c in _audit.Checks) {
                ListBoxItem li = _checks.AddItem(CheckRow(c, c.SummaryHiding(_classChips.IsHidden)), c);
                li.Padding     = new Thickness(4, 5, 4, 5);
                if (c.Name == keep) reselect = li;
            }
            // Passed of all: one that found something, couldn't run or was skipped hasn't passed. Green when all did.
            int passed             = _audit.Checks.FindAll(c => c.State == CheckState.Passed).Count;
            _checks.CountPill.Text = passed + " / " + _audit.Checks.Count + " passed";
            _checks.CountPill.Tone = passed == _audit.Checks.Count ? Genie.ControlUtils.TagTone.Success
                                                                   : Genie.ControlUtils.TagTone.Neutral;
            if (_audit.Dependencies.Count == 0) ShowEmpty("This package uses nothing from other packages.");
            if (reselect != null) _list.SelectedItem = reselect;
        }

        // The pills and the mod line; the header names the package. audit null: no audit, so neither shows. Blocked:
        // one pill naming the block, and the mod line from its files.
        private void UpdateHeader(PackageAudit audit) {
            bool blocked   = audit != null && audit.Blocked != PackageAudit.Block.None;
            bool audited   = audit != null && !blocked;
            int  count     = audit == null ? 0 : audit.Dependencies.Count;
            int  unshipped = Count(audit, d => d.Bucket == Bucket.StockUnshipped && !d.ShippedByMod);
            int  lost      = Count(audit, d => d.Bucket == Bucket.NotLoaded);
            int  warnings  = 0;
            if (audit != null) {
                foreach (Check c in audit.Checks) {
                    if (c.State == CheckState.Warning) warnings += c.Items.Count;
                }
            }

            _sourcesPill.Text          = Genie.TextUtils.Count(count, "source");
            _sourcesPill.Visibility    = audited ? Visibility.Visible : Visibility.Collapsed;
            _warningsPill.Text         = Genie.TextUtils.Count(warnings, "warning");
            _warningsPill.Visibility   = warnings > 0 ? Visibility.Visible : Visibility.Collapsed;
            _unshippedPill.Text        = unshipped + " unshipped";
            _unshippedPill.Visibility  = unshipped > 0 ? Visibility.Visible : Visibility.Collapsed;
            _notLoadedPill.Text        = lost + " not loaded";
            _notLoadedPill.Visibility  = lost > 0 ? Visibility.Visible : Visibility.Collapsed;
            _blockedPill.Visibility    = blocked ? Visibility.Visible : Visibility.Collapsed;
            _viewMod.Visibility = audit != null && audit.Mods.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _modMenu.ClearMenu();
            if (audit != null && audit.Mods.Count > 1) {
                foreach (ModFinder.Mod m in audit.Mods) {
                    ModFinder.Mod pick = m;
                    _modMenu.AddMenuItem(pick.Name, pick.Root, delegate { _openMod(pick, _header.Controls.IsPinned); },
                                         true);
                }
            }
            if (audit != null && audit.Mods.Count == 1)
                Genie.ControlUtils.SetToolTip(_viewMod, "View every package in " + audit.Mods[0].Name);
            else
                Genie.ControlUtils.SetToolTip(_viewMod, "Pick a mod to view every package in");
            if (blocked) {
                _blockedPill.Text = Look.TitleOf(audit.Blocked);
                Genie.ControlUtils.SetToolTip(_blockedPill, Look.BlockedTip(audit.Blocked));
            }

            // A missing file still names its mod and the other copies; a package never saved has none.
            _packageTags.Children.Clear();
            string file = audit == null ? null : audit.File ?? audit.CachedFile;
            if (file == null) {
                _modLine.Visibility = Visibility.Collapsed;
                return;
            }
            _modLine.Visibility = Visibility.Visible;
            double    size      = Genie.Theme.FontSizeLabel;
            TextBlock mod;
            if (audit.ModRoot != null) {
                mod = Genie.ControlUtils.CreateFolderLink(audit.ModRoot, audit.Mod, size);
            } else {
                mod = Genie.ControlUtils.CreateMonoText(
                    Locations.Of(file).Kind == LocationKind.Stock ? "Stock" : "None", size);
            }
            mod.Margin       = new Thickness(0);
            _modName.Content = mod;
            _header.AlignWithCaption(mod, _modCaption); // the new name loads first
            bool inMemory = audit.Blocked != PackageAudit.Block.NotLoaded; // its file is on disk, not loaded
            foreach (Copy c in Copy.Of(audit.Package, audit.File, inMemory)) _packageTags.Children.Add(CopyTag(c));
        }

        private static int Count(PackageAudit audit, Predicate<Dependency> match) {
            return audit == null ? 0 : audit.Dependencies.FindAll(match).Count;
        }

        internal static GenieTag CopyTag(Copy c) {
            GenieTag tag = Look.SourceTag(c.At.Label, c.IsLoaded);
            Genie.ControlUtils.SetToolTip(tag, (c.IsLoaded ? "Loaded in the editor: " : "") + c.File);
            return tag;
        }

        // dot | [find] name ... [row tag]
        //       N references [source tags]
        private UIElement Row(Dependency d) {
            DockPanel        row = new DockPanel();
            FrameworkElement dot = Genie.ControlUtils.CreateDot(Look.ToneOf(d));
            dot.Margin           = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            StackPanel lines = new StackPanel();
            DockPanel  top   = new DockPanel();
            GenieTag   kind =
                Genie.ControlUtils.CreateTag(Look.LabelOf(d), Look.ToneOf(d), Look.TipOf(d));
            kind.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(kind, Dock.Right);
            top.Children.Add(kind);
            FrameworkElement lead = Lead(d);
            DockPanel.SetDock(lead, Dock.Left);
            top.Children.Add(lead);
            TextBlock name =
                Genie.ControlUtils.CreateMonoText(Look.FileName(d.Package, d.File), Genie.Theme.FontSizeLabel + 0.5);
            name.Margin       = new Thickness(4, 0, 0, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(name);
            lines.Children.Add(top);

            WrapPanel sub  = new WrapPanel { Margin = new Thickness(22, 3, 0, 0) }; // under the find button
            int       refs = d.RefCount;
            sub.Children.Add(new TextBlock {
                Text              = Genie.TextUtils.Count(refs, "reference"),
                FontSize          = Genie.Theme.FontSizeTag,
                Foreground        = Genie.Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            });
            foreach (Copy c in Copy.Of(d.Package, d.File)) sub.Children.Add(CopyTag(c));
            lines.Children.Add(sub);
            row.Children.Add(lines);
            return row;
        }

        // The find button, or for a package that didn't load a warning where it would be.
        private static FrameworkElement Lead(Dependency d) {
            if (d.Bucket == Bucket.NotLoaded) {
                FrameworkElement warn = Genie.ControlUtils.CreateWarningIcon();
                Border           slot = new Border { Width = 18, Child = warn, Background = Brushes.Transparent };
                Genie.ControlUtils.SetToolTip(slot, "Not loaded");
                return slot;
            }
            return Genie.ControlUtils.CreateFindPackageIcon(d.Package);
        }

        // dot | name ........ [state tag]
        //       summary
        // Skipped claims nothing about the package, so its name and dot read quieter.
        internal static UIElement CheckRow(Check c, string summary) {
            CheckState       s   = c.State;
            DockPanel        row = new DockPanel();
            FrameworkElement dot = Genie.ControlUtils.CreateDot(Look.ToneOf(s), hollow: s == CheckState.Skipped);
            dot.Margin           = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            GenieTag tag          = Genie.ControlUtils.CreateTag(Look.LabelOf(s), Look.ToneOf(s), Look.TipOf(s));
            tag.Margin            = new Thickness(6, 0, 0, 0);
            tag.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);

            StackPanel lines = new StackPanel();
            TextBlock  name  = new TextBlock {
                Text         = c.Name,
                FontSize     = Genie.Theme.FontSizeLabel + 0.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (s == CheckState.Skipped) name.Foreground = Genie.Theme.TextSecondary;
            lines.Children.Add(name);
            lines.Children.Add(new TextBlock {
                Text       = summary,
                FontSize   = Genie.Theme.FontSizeTag,
                Foreground = Genie.Theme.TextSecondary,
                Margin     = new Thickness(0, 3, 0, 0),
            });
            row.Children.Add(lines);
            return row;
        }

        private Dependency Selected() {
            return _list.SelectedTag<Dependency>();
        }

        // == Reload Linkers ===========================================================================
        // Saving a package detaches its linker (spec, Host facts), so its tables are gone until it's reloaded. Only a
        // package with a file by its name on disk and no unsaved changes can be: a linker reads the file, so one with
        // changes in memory (moving an asset also detaches it) would get tables that don't match. One never saved, or
        // whose file is gone, never counts. One action wherever it shows (the blocked card, a check that can't run):
        // it reloads them all.

        // Amber: the audit is incomplete, nothing is broken (orange would say likely breaks in game).
        private Button ReloadButton(object tooltip) {
            return CreateReloadButton(_audit.Reloadable.Count, tooltip, _pulse, ReloadLinkers);
        }

        internal static Button CreateReloadButton(int count, object tooltip, bool pulse, Action onClick) {
            Button b              = Genie.ControlUtils.CreateButton("Reload linkers", onClick: onClick);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            Genie.ControlUtils.SetTone(b, Genie.ControlUtils.TagTone.Caution, null);
            Genie.ControlUtils.SetButtonCount(b, count, Genie.ControlUtils.TagTone.Caution);
            Genie.ControlUtils.SetToolTip(b, tooltip);
            // Once shown: a cue goes with its target's lifetime, which starts when it loads.
            if (pulse) {
                b.Loaded += delegate {
                    Genie.TweenUtils.Cue(b, Genie.TweenUtils.CueOptions.Pulse(Genie.ControlUtils.TagTone.Caution));
                };
            }
            return b;
        }

        // The card's: it doesn't list them, so the tooltip does.
        //   Linkers unloaded, so these can't be audited:
        //     Pkg.upk ...
        //   Reloads them all.
        private UIElement ReloadTip() {
            double     size = Genie.Theme.FontSizeTag + 0.5;
            StackPanel tip  = new StackPanel();
            tip.Children.Add(new TextBlock {
                Text     = "Linkers unloaded, so these can't be audited:",
                FontSize = size,
                Margin   = new Thickness(0, 0, 0, 4),
            });
            foreach (string p in _audit.Reloadable) tip.Children.Add(PackageLine(p));
            tip.Children.Add(new TextBlock {
                Text       = ReloadNote,
                FontSize   = size,
                Foreground = Genie.Theme.TextSecondary,
                Margin     = new Thickness(0, 6, 0, 0),
            });
            return tip;
        }

        internal static TextBlock PackageLine(string package) {
            return Genie.ControlUtils.CreateMonoText(Look.FileName(package, Genie.PackageUtils.GetPackageFile(package)),
                                                     Genie.Theme.FontSizeCode);
        }

        // Remakes the linkers, loading no objects (LinkerUtils.ReloadLinkers); then the view audits again. Any still
        // without one stay listed.
        private void ReloadLinkers() {
            if (_audit == null) return;
            _pulse              = false;
            List<string> failed = Genie.LinkerUtils.ReloadLinkers(_audit.Reloadable);
            if (failed.Count > 0)
                Genie.Log("Reload linkers: still no linker for " + string.Join(", ", failed) +
                          ", so they can't be audited.");
            RefreshKeepingSelection();
        }

        // == Blocked View =============================================================================
        // The shown package has no linker, or isn't loaded at all, so nothing can be audited. Both panes keep their
        // shape as placeholders under a veil, and one card says why and what to do. Only the shown package blocks:
        // nothing here reads a dependency's tables.

        // audit null: lifts it.
        private void ShowBlocked(PackageAudit audit) {
            bool blocked             = audit != null;
            _veil.Visibility         = blocked ? Visibility.Visible : Visibility.Collapsed;
            _placeholders.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
            _list.Background         = blocked ? Genie.ControlUtils.CreatePlaceholderStripes() : Brushes.Transparent;
            _dependencies.EmptyText  = blocked ? null : "None.";
            _checks.EmptyText        = blocked ? null : "None.";
            if (!blocked) return;

            _emptyState.Visibility = Visibility.Collapsed;
            _details.Visibility    = Visibility.Collapsed;
            // Bar widths from the mockup: name, then the line under it.
            foreach (double[] bars in new[] { new[] { 150.0, 120 }, new[] { 190.0, 90 }, new[] { 130.0, 140 },
                                              new[] { 170.0, 110 }, new[] { 140.0, 100 } })
                PlaceholderRow(_dependencies, bars[0], bars[1], 52, true);
            foreach (double[] bars in new[] { new[] { 160.0, 70 }, new[] { 120.0, 90 } })
                PlaceholderRow(_checks, bars[0], bars[1], 44, false);
            _dependencies.CountPill.Text = "–"; // unknown, not none
            _checks.CountPill.Text       = "–";

            FillCard(_card, audit, delegate { return ReloadButton(ReloadTip()); });
        }

        // The card's icon, title and what to do, by why. reload makes its Reload linkers button. A package the browser
        // doesn't list (a ModBuddy project's, in the mod view) can't be fully loaded there, only opened.
        internal static void FillCard(GenieTitledCard card, PackageAudit audit, Func<Button> reload) {
            card.Icon =
                Genie.ControlUtils.CreateDialogIcon(Genie.ControlUtils.TagTone.Caution, Look.SymbolOf(audit.Blocked));
            card.Title      = Look.TitleOf(audit.Blocked);
            StackPanel body = card.Body;
            body.Children.Clear();
            switch (audit.Blocked) {
                case PackageAudit.Block.LinkerUnloaded:
                    body.Children.Add(Sentence("This package's linker was unloaded. Reload linkers to see its " +
                                               "dependencies and checks."));
                    Button button = reload();
                    button.Margin = new Thickness(0, 14, 0, 0);
                    body.Children.Add(button);
                    break;
                case PackageAudit.Block.FileMissing:
                    body.Children.Add(MissingFileSentence(audit.CachedFile));
                    TextBlock then = Sentence("Restore its file or save the package, then reload linkers.");
                    then.Margin    = new Thickness(0, 10, 0, 0);
                    body.Children.Add(then);
                    break;
                case PackageAudit.Block.NotLoaded:
                    body.Children.Add(Sentence(Genie.ContentBrowserUtils.IsPackageListed(audit.Package)
                                                   ? "This package isn't loaded. Fully load it in the Content " +
                                                         "Browser to see its dependencies and checks."
                                                   : "This package isn't loaded, and the Content Browser doesn't " +
                                                         "list it. Open it there to see its dependencies and checks."));
                    break;
                case PackageAudit.Block.UnsavedChanges:
                    body.Children.Add(
                        Sentence("This package has unsaved changes in memory. Save it, then reload linkers."));
                    break;
                default:
                    body.Children.Add(Sentence("Save the file to disk and then reload linkers."));
                    break;
            }
        }

        // "...and its file cannot be found.", its file a link to the folder the cache names. With that folder gone
        // too, the link is off: opening would create it.
        private static GenieParagraph MissingFileSentence(string file) {
            string             folder = Path.GetDirectoryName(file);
            GenieParagraphLink link   = Genie.ControlUtils.CreateParagraphLink(
                "its file", delegate { Genie.FileUtils.OpenFolder(folder); }, file + "\nClick to open its folder.",
                Directory.Exists(folder ?? "") ? null : file + "\nIts folder is missing too.");
            return Genie.ControlUtils.CreateParagraph("This package's linker was unloaded and ", link,
                                                      " cannot be found.");
        }

        internal static TextBlock Sentence(string text) {
            return new TextBlock {
                Text         = text,
                FontSize     = Genie.Theme.FontSizeBody,
                Foreground   = Genie.Theme.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 19,
            };
        }

        // dot | name bar ...... [tag bar]       a dependency row: the tag on the name's line
        //       sub bar
        // dot | name bar ........ [tag bar]     a check row: the tag centred on both lines
        //       sub bar
        private static void PlaceholderRow(GenieListSection section, double name, double sub, double tag,
                                           bool tagOnTop) {
            DockPanel row = new DockPanel { Margin = new Thickness(4, 4, 0, 4) };
            Border    dot = Genie.ControlUtils.CreatePlaceholderBar(8, 8);
            dot.Margin    = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            Border tagBar = Genie.ControlUtils.CreatePlaceholderBar(tag, 14);
            tagBar.Margin = new Thickness(8, 0, 0, 0);

            StackPanel lines = new StackPanel();
            DockPanel  top   = new DockPanel();
            if (tagOnTop) {
                DockPanel.SetDock(tagBar, Dock.Right);
                top.Children.Add(tagBar);
            } else {
                DockPanel.SetDock(tagBar, Dock.Right);
                row.Children.Add(tagBar);
            }
            top.Children.Add(Genie.ControlUtils.CreatePlaceholderBar(name));
            lines.Children.Add(top);
            Border under = Genie.ControlUtils.CreatePlaceholderBar(sub);
            under.Margin = new Thickness(0, 7, 0, 0);
            lines.Children.Add(under);
            row.Children.Add(lines);

            ListBoxItem li      = section.AddItem(row, null);
            li.Padding          = new Thickness(4, 5, 4, 5);
            li.Focusable        = false;
            li.IsHitTestVisible = false; // under the veil anyway; never selected or hovered
        }

        // The details pane as bars: caption, name, location lines, the note box, the table's label and frame.
        internal static Border DetailsPlaceholder() {
            StackPanel bars = new StackPanel();
            foreach (double[] bar in new[] { new[] { 70.0, 7 }, new[] { 230.0, 12 }, new[] { 360.0, 8 },
                                             new[] { 300.0, 8 }, new[] { double.NaN, 40 }, new[] { 90.0, 7 } }) {
                Border b = Genie.ControlUtils.CreatePlaceholderBar(bar[0], bar[1]);
                b.Margin = new Thickness(0, bars.Children.Count == 0 ? 0 : 10, 0, 0);
                bars.Children.Add(b);
            }
            bars.Children.Add(new Border {
                Height          = 150,
                Margin          = new Thickness(0, 10, 0, 0),
                BorderBrush     = Genie.Theme.PlaceholderFill,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(Genie.Theme.BoxCornerRadius),
            });
            return new Border {
                Child      = bars,
                Padding    = Genie.Theme.PaneInset,
                Background = Genie.ControlUtils.CreatePlaceholderStripes(),
                Visibility = Visibility.Collapsed,
            };
        }

        // == Details ==================================================================================

        private void ShowEmpty(string hint) {
            _details.Visibility    = Visibility.Collapsed;
            _emptyState.Visibility = Visibility.Visible;
            _emptyState.Hint       = hint;
        }

        private void ShowDetails() {
            Check check = _list.SelectedTag<Check>();
            if (check != null && _audit != null) {
                ShowCheck(check);
                return;
            }
            Dependency d = Selected();
            if (d == null || _audit == null) {
                ShowEmpty(_audit != null && _audit.Dependencies.Count == 0
                              ? "This package uses nothing from other packages."
                              : SelectHint);
                return;
            }
            _emptyState.Visibility = Visibility.Collapsed;
            _details.Visibility    = Visibility.Visible;
            _detailsBody.Children.Clear();

            TextBlock label = Genie.ControlUtils.CreateCaption("SOURCE PACKAGE");
            label.Margin    = new Thickness(0, 0, 0, 4);
            _detailsBody.Children.Add(label);

            DockPanel        title = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            FrameworkElement lead  = Lead(d);
            DockPanel.SetDock(lead, Dock.Left);
            title.Children.Add(lead);
            GenieTag kind =
                Genie.ControlUtils.CreateTag(Look.LabelOf(d), Look.ToneOf(d), Look.TipOf(d));
            kind.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(kind, Dock.Right);
            TextBlock name =
                Genie.ControlUtils.CreateMonoText(Look.FileName(d.Package, d.File), Genie.Theme.FontSizeValue + 1);
            name.FontWeight       = FontWeights.SemiBold;
            name.Margin           = new Thickness(4, 0, 0, 0);
            name.TextTrimming     = TextTrimming.CharacterEllipsis;
            StackPanel nameAndTag = new StackPanel { Orientation = Orientation.Horizontal };
            nameAndTag.Children.Add(name);
            nameAndTag.Children.Add(kind);
            title.Children.Add(nameAndTag);
            _detailsBody.Children.Add(title);

            if (d.Mod != null) {
                StackPanel mod =
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                TextBlock cap = Genie.ControlUtils.CreateCaption("SOURCE MOD");
                cap.Margin    = new Thickness(0, 0, 7, 0);
                mod.Children.Add(cap);
                mod.Children.Add(Genie.ControlUtils.CreateMonoText(d.Mod, Genie.Theme.FontSizeLabel));
                _detailsBody.Children.Add(mod);
            }

            foreach (Copy c in Copy.Of(d.Package, d.File)) _detailsBody.Children.Add(LocationLine(c));

            Border note = Genie.ControlUtils.CreateNote(Look.ToneOf(d), null, Look.NoteOf(d));
            note.Margin = new Thickness(0, 8, 0, 0);
            _detailsBody.Children.Add(note);

            // Objects used ............ [copy]
            // The copy icon makes this line taller than the label alone, so less margin keeps the old spacing.
            DockPanel usedLine = new DockPanel { Margin = new Thickness(0, 10, 0, 3) };
            Border    copy     = Genie.ControlUtils.CreateCopyIcon(delegate { return ListText(d); },
                                                                   "Copy the objects used, and what uses them");
            DockPanel.SetDock(copy, Dock.Right);
            usedLine.Children.Add(copy);
            TextBlock used         = Genie.ControlUtils.CreateFieldLabel("Objects used");
            used.Margin            = new Thickness(0);
            used.VerticalAlignment = VerticalAlignment.Center;
            usedLine.Children.Add(used);
            _detailsBody.Children.Add(usedLine);
            if (!_audit.HasDependents)
                _detailsBody.Children.Add(Genie.ControlUtils.CreateNoteText(
                    "This package's dependency table isn’t in memory, so only uses through a class are known."));
            _detailsBody.Children.Add(ObjectsTable(d));
        }

        // CHECK, name and state, the rule, the note, then what it lists: empty but for "Nothing found" once passed.
        // One that couldn't run or wasn't run says why in the note, with no list.
        private void ShowCheck(Check c) {
            CheckState s           = c.State;
            _emptyState.Visibility = Visibility.Collapsed;
            _details.Visibility    = Visibility.Visible;
            _detailsBody.Children.Clear();

            TextBlock label = Genie.ControlUtils.CreateCaption("CHECK");
            label.Margin    = new Thickness(0, 0, 0, 4);
            _detailsBody.Children.Add(label);

            StackPanel title = new StackPanel {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 4),
            };
            TextBlock name = new TextBlock {
                Text              = c.Name,
                FontSize          = Genie.Theme.FontSizeValue + 1,
                FontWeight        = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.Children.Add(name);
            GenieTag tag = Genie.ControlUtils.CreateTag(Look.LabelOf(s), Look.ToneOf(s), Look.TipOf(s));
            tag.Margin   = new Thickness(8, 0, 0, 0);
            title.Children.Add(tag);
            _detailsBody.Children.Add(title);
            _detailsBody.Children.Add(new TextBlock {
                Text       = c.Rule,
                FontSize   = Genie.Theme.FontSizeLabel,
                Foreground = Genie.Theme.TextSecondary,
            });

            Border note = IsBlocked(c) ? BlockedNoteBox(c, delegate { return ReloadButton(ReloadNote); })
                                       : Genie.ControlUtils.CreateNote(Look.ToneOf(s), null, c.NoteText);
            note.Margin = new Thickness(0, 8, 0, 0);
            _detailsBody.Children.Add(note);
            if (c.Footnote != null) {
                TextBlock foot = Genie.ControlUtils.CreateNoteText(c.Footnote);
                foot.Margin    = new Thickness(0, 6, 0, 0);
                _detailsBody.Children.Add(foot);
            }
            if (s == CheckState.NotRun || s == CheckState.Skipped) return;

            // Chips, then Objects ............ [copy], as Objects used: the label stays on what it labels.
            List<CheckItem> shown = AddClassChips(_detailsBody, c, _classChips);

            DockPanel listLine = new DockPanel { Margin = new Thickness(0, 10, 0, 3) };
            Border    copy     = Genie.ControlUtils.CreateCopyIcon(delegate { return CheckText(c); }, "Copy the list");
            DockPanel.SetDock(copy, Dock.Right);
            listLine.Children.Add(copy);
            TextBlock list         = Genie.ControlUtils.CreateFieldLabel(c.ListLabel);
            list.Margin            = new Thickness(0);
            list.VerticalAlignment = VerticalAlignment.Center;
            listLine.Children.Add(list);
            _detailsBody.Children.Add(listLine);
            GenieRelationTable table =
                Genie.ControlUtils.CreateRelationTable(Look.FileName(_audit.Package, _audit.File), "Detail");
            foreach (CheckItem item in shown)
                table.AddRow(Genie.ControlUtils.CreateAssetCell(item.PathName, item.ClassName),
                             Genie.ControlUtils.CreateLinesCell(item.Lines));
            AddEmptyNote(table, c, shown);
            _detailsBody.Children.Add(table);
            AddHiddenLine(_detailsBody, c, shown, _classChips);
        }

        // == Hiding by Class ==========================================================================
        // Unreferenced lists archetypes and the like that config loads by name, so the user can hide whole classes.
        // Addon-wide: one setting both views share; a change in either redraws both.

        internal static GenieFilterChips HiddenClassChips() {
            GenieFilterChips chips =
                Genie.ControlUtils.CreateFilterChips("HiddenClasses", Genie.ControlUtils.TagTone.Type, "Unreferenced");
            chips.Margin = new Thickness(0, 10, 0, -6); // the chips' own bottom margin, taken back over the label
            return chips;
        }

        // The checks' row lines and the details, without auditing again: hiding changes neither.
        private void ShowHiding() {
            if (_audit == null || _audit.Blocked != PackageAudit.Block.None) return;
            foreach (ListBoxItem li in _checks.Rows) {
                Check c = li.Tag as Check;
                if (c != null && c.HidesByClass) li.Content = CheckRow(c, c.SummaryHiding(_classChips.IsHidden));
            }
            ShowDetails();
        }

        // Its chips, over the list's label, when it hides by class and found something; returns the items to list.
        internal static List<CheckItem> AddClassChips(StackPanel body, Check c, GenieFilterChips chips) {
            if (!c.HidesByClass || c.Items.Count == 0) return c.Items;
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CheckItem item in c.Items) {
                int n;
                counts.TryGetValue(item.ClassName ?? "", out n);
                counts[item.ClassName ?? ""] = n + 1;
            }
            chips.SetItems(counts);
            body.Children.Add(chips);
            return c.Shown(chips.IsHidden);
        }

        // "Nothing found" for a clean check; with everything hidden, that would claim it came up clean.
        internal static void AddEmptyNote(GenieRelationTable table, Check c, List<CheckItem> shown) {
            if (c.Items.Count == 0)
                table.AddNote("Nothing found");
            else if (shown.Count == 0)
                table.AddNote("Every object here is of a hidden class");
        }

        internal static void AddHiddenLine(StackPanel body, Check c, List<CheckItem> shown, GenieFilterChips chips) {
            string line = HiddenLine(c, shown, chips);
            if (line == null) return;
            TextBlock text = Genie.ControlUtils.CreateNoteText(line + " Click a class to show it again.");
            text.Margin    = new Thickness(0, 6, 0, 0);
            body.Children.Add(text);
        }

        // "3 objects hidden by class (XComWeapon)", or null with none hidden.
        internal static string HiddenLine(Check c, List<CheckItem> shown, GenieFilterChips chips) {
            int n = c.Items.Count - shown.Count;
            if (n == 0) return null;
            List<string> classes = new List<string>();
            foreach (CheckItem item in c.Items) {
                string cls = item.ClassName ?? "";
                if (chips.IsHidden(cls) && !classes.Contains(cls)) classes.Add(cls);
            }
            classes.Sort(StringComparer.OrdinalIgnoreCase);
            return Genie.TextUtils.Count(n, c.ItemNoun) + " hidden by class (" + string.Join(", ", classes) + ").";
        }

        // A blocked check: one line saying why and what to do, then what it couldn't read, then Reload linkers when
        // reloading fixes it. Packages to save first follow, under their own line unless the note's is theirs. Amber,
        // as the blocked card: incomplete, not broken.
        // Packages not in memory come last, under their own line unless the note's is theirs.
        internal static bool IsBlocked(Check c) {
            return c.Reloadable.Count > 0 || c.Unsaved.Count > 0 || c.Unread.Count > 0 || c.NotLoaded.Count > 0;
        }

        internal static Border BlockedNoteBox(Check c, Func<Button> reload) {
            StackPanel below = new StackPanel();
            foreach (string p in c.Reloadable) below.Children.Add(PackageLine(p));
            foreach (string path in c.Unread)
                below.Children.Add(Genie.ControlUtils.CreateMonoText(path, Genie.Theme.FontSizeCode));
            if (c.Reloadable.Count > 0) {
                Button button = reload();
                button.Margin = new Thickness(0, 8, 0, 0);
                below.Children.Add(button);
            }
            if (c.Unsaved.Count > 0 && c.Reloadable.Count > 0) {
                below.Children.Add(new TextBlock {
                    Text         = UnsavedHeading,
                    FontSize     = Genie.Theme.FontSizeLabel,
                    Foreground   = Genie.Theme.TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 10, 0, 2),
                });
            }
            foreach (string p in c.Unsaved) below.Children.Add(PackageLine(p));
            if (c.NotLoaded.Count > 0 && (c.Reloadable.Count > 0 || c.Unsaved.Count > 0))
                below.Children.Add(new TextBlock {
                    Text         = NotLoadedHeading,
                    FontSize     = Genie.Theme.FontSizeLabel,
                    Foreground   = Genie.Theme.TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 10, 0, 2),
                });
            foreach (string p in c.NotLoaded) below.Children.Add(PackageLine(p));
            return Genie.ControlUtils.CreateNote(Genie.ControlUtils.TagTone.Caution, null, c.Problem, null, below);
        }

        // [source tag, fixed width]  path (opens Explorer) [Loaded]
        internal static UIElement LocationLine(Copy c) {
            DockPanel line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            GenieTag  tag  = CopyTag(c);
            tag.Margin     = new Thickness(0); // fills the slot, so stacked tags share one width
            Border slot    = new Border { Width = 86, Child = tag, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(slot, Dock.Left);
            line.Children.Add(slot);

            // Left-aligned with Loaded docked first, so Loaded follows a short path and a long one trims to leave it
            // room.
            DockPanel path = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
            if (c.IsLoaded) {
                GenieTag loaded = Genie.ControlUtils.CreateTag("Loaded", Genie.ControlUtils.TagTone.Success,
                                                               "The copy the editor loaded");
                loaded.Margin   = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(loaded, Dock.Right);
                path.Children.Add(loaded);
            }
            TextBlock link =
                Genie.ControlUtils.CreateFileLink(c.File, Locations.Shorten(c.File, c.At), Genie.Theme.FontSizeTag);
            link.TextTrimming      = TextTrimming.CharacterEllipsis;
            link.VerticalAlignment = VerticalAlignment.Center;
            Genie.ControlUtils.SetToolTip(link, "Show in File Explorer: " + c.File);
            path.Children.Add(link);
            line.Children.Add(path);
            return line;
        }

        // Source package's object -> the objects in the audited package that use it, one row per object used.
        private UIElement ObjectsTable(Dependency d) {
            GenieRelationTable table = Genie.ControlUtils.CreateRelationTable(
                Look.FileName(d.Package, d.File), Look.FileName(_audit.Package, _audit.File), true);
            List<UsedObject> objects = new List<UsedObject>(d.Objects);
            objects.Sort((x, y) => string.Compare(x.PathName, y.PathName, StringComparison.OrdinalIgnoreCase));
            foreach (UsedObject o in objects) {
                StackPanel dependents = new StackPanel();
                foreach (string path in o.Dependents) {
                    string className;
                    _audit.ExportClasses.TryGetValue(path, out className);
                    dependents.Children.Add(Genie.ControlUtils.CreateAssetCell(path, className));
                }
                if (o.Dependents.Count == 0)
                    dependents.Children.Add(new TextBlock {
                        Text       = _audit.HasDependents ? "No export lists it" : "Unknown",
                        FontSize   = Genie.Theme.FontSizeLabel,
                        Foreground = Genie.Theme.TextSecondary,
                        Margin     = new Thickness(6, 5, 6, 5),
                    });
                table.AddRow(Genie.ControlUtils.CreateAssetCell(o.PathName, o.ClassName), dependents);
            }
            return table;
        }

        // == Copy =====================================================================================

        // The objects table as text, in its order and direction: each object used, then what uses it.
        //   Pkg_DLC.upk uses from Pkg.upk (Other mod):
        //     Pkg.Group.Used (Class)
        //       -> Pkg_DLC.Group.User (Class)
        private string ListText(Dependency d) {
            if (_audit == null) return null;
            StringBuilder text = new StringBuilder();
            text.AppendLine(Look.FileName(_audit.Package, _audit.File) + " uses from " +
                            Look.FileName(d.Package, d.File) + " (" + Look.LabelOf(d) + "):");
            List<UsedObject> objects = new List<UsedObject>(d.Objects);
            objects.Sort((x, y) => string.Compare(x.PathName, y.PathName, StringComparison.OrdinalIgnoreCase));
            foreach (UsedObject o in objects) {
                text.AppendLine("  " + WithClass(o.PathName, o.ClassName));
                foreach (string path in o.Dependents) {
                    string className;
                    _audit.ExportClasses.TryGetValue(path, out className);
                    text.AppendLine("    -> " + WithClass(path, className));
                }
                if (o.Dependents.Count == 0)
                    text.AppendLine("    (" + (_audit.HasDependents ? "no export lists it" : "unknown") + ")");
            }
            return text.ToString();
        }

        // A check's list as text, in the table's order:
        //   Pkg.upk: Unreferenced (Info)
        //     Pkg.Group.Obj (Texture2D)
        //       0 referencers
        //       1.4 MB
        private string CheckText(Check c) {
            if (_audit == null) return null;
            StringBuilder text = new StringBuilder();
            text.AppendLine(Look.FileName(_audit.Package, _audit.File) + ": " + c.Name + " (" +
                            Look.LabelOf(c.State) + ")");
            List<CheckItem> shown = c.Shown(_classChips.IsHidden);
            foreach (CheckItem item in shown) {
                text.AppendLine("  " + WithClass(item.PathName, item.ClassName));
                foreach (string line in item.Lines) text.AppendLine("    " + line);
            }
            if (c.Items.Count == 0) text.AppendLine("  Nothing found");
            string hidden = HiddenLine(c, shown, _classChips);
            if (hidden != null) text.AppendLine("  " + hidden);
            return text.ToString();
        }

        internal static string WithClass(string pathName, string className) {
            return string.IsNullOrEmpty(className) ? pathName : pathName + " (" + className + ")";
        }
    }

    // == Mod View =====================================================================================
    // Every package in one mod, what they need from outside it, and the checks across them. Opened from the package
    // view's View mod, in the same window. It follows the Content Browser like the package view: selecting another
    // package (a find button here, or the user's own click) goes back to the package view, unless pinned.

    internal sealed class ModPanel : UserControl {
        private const string SelectHint = "Select a dependency, check or package.";

        private readonly Action _back;
        private ModFinder.Mod   _mod;
        private ModAudit        _audit;
        private bool            _active; // shown; hidden, it ignores every notification

        private readonly Genie.ContentBrowserUtils.PackageSetWatch _watch;      // the mod's packages
        private readonly GenieFilterChips                          _classChips; // as the package view's

        // Header
        private readonly GenieHeader    _header;
        private readonly ContentControl _rootTag;
        private readonly GenieCountPill _packagesPill, _warningsPill, _unshippedPill, _notLoadedPill;
        private readonly StackPanel     _blockedPills; // one per kind of block
        private readonly Genie.ContentBrowserUtils.PackageFollower _follower;

        // List and details
        private readonly GenieList        _list;
        private readonly GenieListSection _dependencies, _checks, _packages;
        private readonly GenieEmptyState  _emptyState;
        private readonly ScrollViewer     _details;
        private readonly StackPanel       _detailsBody;

        // A blocked package: the details pane as placeholders under a veil, the package view's card on it.
        private readonly Grid           _veil;
        private readonly Border         _placeholders;
        private readonly GenieTitledCard _card;

        // Notifications, no polling, while shown:
        //   followed package changes       -> back to the package view (the follower minds the pin)
        //   followed package modified      -> rebuild, keeping the selection
        //   a mod package loads, unloads,
        //   or is modified or saved        -> rebuild, keeping the selection
        //   folder settings saved          -> look for copies again, rebuild
        //   Reload linkers clicked         -> reload, rebuild

        internal ModPanel(GenieWindow window, Action openFolders, Action openLocations, Action back) {
            _back      = back;
            Visibility = Visibility.Collapsed;
            Grid root  = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header: MOD and the mod's source tag, over its name and pills; Back to package, then refresh, pin and
            // the gear.
            _header                          = Genie.ControlUtils.CreatePanelHeader(window, "MOD");
            _header.Controls.RefreshToolTip  = "Audit the mod's packages again, and look for copies on disk.";
            _header.Controls.PinToolTip      = "Pin: stay on this mod";
            _header.Controls.Refresh        += delegate {
                CopyIndex.Rebuild(RefreshKeepingSelection);
                RefreshKeepingSelection();
            };
            _header.Controls.Settings.AddMenuItem("Set source folders…", "Where to look for mods", openFolders);
            _header.Controls.Settings.AddMenuItem("About Sources", "What each source tag means", openLocations);
            Button backButton = Genie.ControlUtils.CreateButton("Back to package", onClick: Exit);
            backButton.Margin = new Thickness(0, 0, 8, 0);
            _header.AddAction(backButton);

            _rootTag             = new ContentControl { Focusable = false, Margin = new Thickness(7, 0, 0, 0) };
            _header.CaptionExtra = _rootTag;
            _packagesPill        = Genie.ControlUtils.CreateCountPill("");
            _header.AddPill(_packagesPill);
            _warningsPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Warning, true);
            _header.AddPill(_warningsPill);
            _unshippedPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Danger, true);
            _header.AddPill(_unshippedPill);
            _notLoadedPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Danger, true);
            _header.AddPill(_notLoadedPill);
            _blockedPills = new StackPanel { Orientation = Orientation.Horizontal };
            _header.AddPill(_blockedPills);
            Grid.SetRow(_header, 0);
            root.Children.Add(_header);

            // Its Package is only for the pin: the header shows the mod's name (Title).
            _follower                 = Genie.ContentBrowserUtils.FollowPackage(_header);
            _follower.PackageChanged += delegate {
                if (_active) Exit();
            };
            _follower.PackageModified += delegate {
                if (_active) RefreshKeepingSelection();
            };

            GenieListPane left       = Genie.ControlUtils.CreateListPane("Dependencies", true);
            left.Margin              = new Thickness(10, 10, 0, 10);
            _list                    = left.List;
            _dependencies            = _list.AddSection("Dependencies");
            _dependencies.EmptyText  = "None.";
            _checks                  = _list.AddSection("Checks");
            _checks.EmptyText        = "None.";
            _packages                = _list.AddSection("Packages");
            _packages.EmptyText      = "None.";
            _list.SelectionChanged  += delegate {
                ShowDetails();
            };
            _classChips                = PackagePanel.HiddenClassChips();
            _classChips.HiddenChanged += ShowHiding;
            _watch                     = Genie.ContentBrowserUtils.WatchPackages(this, RefreshKeepingSelection);

            Grid      right = new Grid();
            Thickness inset = Genie.Theme.PaneInset;
            _emptyState     = Genie.ControlUtils.CreateEmptyState(
                "What’s the mod view?",
                "Every package in this mod, what they need from outside it, and the checks across them.", SelectHint);
            _emptyState.Margin = inset;
            right.Children.Add(_emptyState);
            _detailsBody = new StackPanel { Margin = inset };
            _details     = new ScrollViewer {
                Content                       = _detailsBody,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            right.Children.Add(_details);
            _placeholders = PackagePanel.DetailsPlaceholder();
            right.Children.Add(_placeholders);
            _card            = Genie.ControlUtils.CreateTitledCard(360);
            _veil            = Genie.ControlUtils.CreateVeil(_card);
            _veil.Visibility = Visibility.Collapsed;
            right.Children.Add(_veil);

            Grid body = Genie.ControlUtils.CreateSplitPanel(left, right, 0.37, 240, 320);
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            Content = root;

            Genie.ControlUtils.WhileLoaded(this, Attach, Detach);
        }

        /// <summary>Shows the mod, pinned or not.</summary>
        internal void Open(ModFinder.Mod mod, bool pinned) {
            _mod                      = mod;
            _active                   = true;
            _header.Controls.IsPinned = pinned;
            _header.Title             = mod.Name;
            _rootTag.Content          = PackagePanel.CopyTag(new Copy { File = mod.Root, At = Locations.Of(mod.Root) });
            Visibility                = Visibility.Visible;
            _list.SelectedItem        = null;
            Refresh(null);
        }

        private void Exit() {
            _active = false;
            _watch.SetPackages(null);
            Visibility = Visibility.Collapsed;
            _back();
        }

        // == Notifications ============================================================================

        private void Attach() {
            FolderSettings.Changed += OnFoldersChanged;
        }

        private void Detach() {
            FolderSettings.Changed -= OnFoldersChanged;
        }

        private void OnFoldersChanged() {
            if (!_active) return;
            CopyIndex.Rebuild(RefreshKeepingSelection);
            RefreshKeepingSelection();
        }

        private void RefreshKeepingSelection() {
            if (!_active || !IsLoaded) return;
            Refresh(KeyOf(_list.SelectedTag<object>()));
        }

        // A row's identity across rebuilds.
        private static string KeyOf(object tag) {
            ModDependency d = tag as ModDependency;
            if (d != null) return "dep:" + d.Name;
            Check c = tag as Check;
            if (c != null) return "check:" + c.Name;
            PackageAudit p = tag as PackageAudit;
            return p != null ? "package:" + p.Package : null;
        }

        // == List =====================================================================================

        private void Refresh(string keep) {
            _dependencies.ClearItems();
            _checks.ClearItems();
            _packages.ClearItems();
            _checks.CountPill.Tone = Genie.ControlUtils.TagTone.Neutral;
            _audit                 = null;
            ShowEmpty(SelectHint);
            UpdateHeader();
            try {
                _audit = ModAudit.Run(_mod);
            } catch (Exception ex) {
                ShowEmpty("Couldn’t audit the mod: " + ex.Message);
                Genie.Log("Couldn't audit mod " + _mod.Name + ": " + ex);
                return;
            }
            _watch.SetPackages(_audit.Packages.ConvertAll(p => p.Package));
            UpdateHeader();

            ListBoxItem                 reselect = null;
            Action<ListBoxItem, object> add      = delegate(ListBoxItem li, object tag) {
                li.Padding = new Thickness(4, 5, 4, 5);
                if (keep != null && KeyOf(tag) == keep) reselect = li;
            };
            foreach (ModDependency d in _audit.Dependencies) add(_dependencies.AddItem(DependencyRow(d), d), d);
            foreach (Check c in _audit.Checks)
                add(_checks.AddItem(PackagePanel.CheckRow(c, c.SummaryHiding(_classChips.IsHidden)), c), c);
            foreach (PackageAudit p in _audit.Packages) add(_packages.AddItem(PackageRow(p), p), p);

            int passed             = _audit.Checks.FindAll(c => c.State == CheckState.Passed).Count;
            _checks.CountPill.Text = passed + " / " + _audit.Checks.Count + " passed";
            _checks.CountPill.Tone =
                passed == _audit.Checks.Count ? Genie.ControlUtils.TagTone.Success : Genie.ControlUtils.TagTone.Neutral;
            if (_audit.Packages.Count == 0) ShowEmpty("No packages were found in this mod's folder.");
            if (reselect != null) _list.SelectedItem = reselect;
        }

        private void UpdateHeader() {
            int                                 unshipped = 0, lost = 0, warnings = 0;
            Dictionary<PackageAudit.Block, int> blocked = new Dictionary<PackageAudit.Block, int>();
            if (_audit != null) {
                unshipped = _audit.Dependencies.FindAll(d => d.Bucket == Bucket.StockUnshipped).Count;
                lost      = _audit.Dependencies.FindAll(d => d.Bucket == Bucket.NotLoaded).Count;
                foreach (Check c in _audit.Checks) {
                    if (c.State == CheckState.Warning) warnings += c.Items.Count;
                }
                foreach (PackageAudit p in _audit.Packages) {
                    if (p.Blocked == PackageAudit.Block.None) continue;
                    int n;
                    blocked.TryGetValue(p.Blocked, out n);
                    blocked[p.Blocked] = n + 1;
                }
            }
            _packagesPill.Text        = Genie.TextUtils.Count(_audit == null ? 0 : _audit.Packages.Count, "package");
            _packagesPill.Visibility  = _audit != null ? Visibility.Visible : Visibility.Collapsed;
            _warningsPill.Text        = Genie.TextUtils.Count(warnings, "warning");
            _warningsPill.Visibility  = warnings > 0 ? Visibility.Visible : Visibility.Collapsed;
            _unshippedPill.Text       = unshipped + " unshipped";
            _unshippedPill.Visibility = unshipped > 0 ? Visibility.Visible : Visibility.Collapsed;
            _notLoadedPill.Text       = lost + " not loaded";
            _notLoadedPill.Visibility = lost > 0 ? Visibility.Visible : Visibility.Collapsed;
            _blockedPills.Children.Clear();
            foreach (KeyValuePair<PackageAudit.Block, int> b in blocked) {
                GenieCountPill pill = Genie.ControlUtils.CreateCountPill(Look.PackagesBlocked(b.Key, b.Value),
                                                                         Genie.ControlUtils.TagTone.Caution);
                Genie.ControlUtils.SetToolTip(pill, "So they can't be audited");
                _blockedPills.Children.Add(pill);
            }
        }

        // dot | [find] name ... [row tag]
        //       N references [source tags]
        // An Other mod row names the mod: no find button, its folders' tags.
        private UIElement DependencyRow(ModDependency d) {
            Dependency       first = d.First;
            DockPanel        row   = new DockPanel();
            FrameworkElement dot   = Genie.ControlUtils.CreateDot(Look.ToneOf(d.Bucket));
            dot.Margin             = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            StackPanel lines = new StackPanel();
            DockPanel  top   = new DockPanel();
            GenieTag   kind =
                Genie.ControlUtils.CreateTag(Look.LabelOf(d.Bucket), Look.ToneOf(d.Bucket), Look.TipOf(d.Bucket));
            kind.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(kind, Dock.Right);
            top.Children.Add(kind);
            if (!d.IsMod) {
                FrameworkElement lead = Lead(d.Name, d.Bucket == Bucket.NotLoaded);
                DockPanel.SetDock(lead, Dock.Left);
                top.Children.Add(lead);
            }
            TextBlock name    = Genie.ControlUtils.CreateMonoText(Display(d), Genie.Theme.FontSizeLabel + 0.5);
            name.Margin       = new Thickness(d.IsMod ? 0 : 4, 0, 0, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(name);
            lines.Children.Add(top);

            WrapPanel sub = new WrapPanel { Margin = new Thickness(d.IsMod ? 0 : 22, 3, 0, 0) };
            sub.Children.Add(SubText(Genie.TextUtils.Count(d.RefCount, "reference")));
            foreach (Copy c in CopiesOf(d)) sub.Children.Add(PackagePanel.CopyTag(c));
            lines.Children.Add(sub);
            row.Children.Add(lines);
            return row;
        }

        // dot | [find] name ... [row tag]
        //       N sources [source tags]
        // Its tag: why it isn't audited, else what it ships broken (unshipped, not loaded), else its warnings.
        private UIElement PackageRow(PackageAudit p) {
            Genie.ControlUtils.TagTone tone;
            string                     label, tip;
            Summarize(p, out tone, out label, out tip);
            DockPanel        row = new DockPanel();
            FrameworkElement dot = Genie.ControlUtils.CreateDot(tone);
            dot.Margin           = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            StackPanel lines = new StackPanel();
            DockPanel  top   = new DockPanel();
            if (label != null) {
                GenieTag kind = Genie.ControlUtils.CreateTag(label, tone, tip);
                kind.Margin   = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(kind, Dock.Right);
                top.Children.Add(kind);
            }
            FrameworkElement lead = Lead(p.Package, false);
            DockPanel.SetDock(lead, Dock.Left);
            top.Children.Add(lead);
            TextBlock name =
                Genie.ControlUtils.CreateMonoText(Look.FileName(p.Package, p.File), Genie.Theme.FontSizeLabel + 0.5);
            name.Margin       = new Thickness(4, 0, 0, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(name);
            lines.Children.Add(top);

            WrapPanel sub = new WrapPanel { Margin = new Thickness(22, 3, 0, 0) };
            if (ModAudit.Audited(p)) sub.Children.Add(SubText(Genie.TextUtils.Count(p.Dependencies.Count, "source")));
            foreach (Copy c in Copy.Of(p.Package, p.File, IsInMemory(p))) sub.Children.Add(PackagePanel.CopyTag(c));
            lines.Children.Add(sub);
            row.Children.Add(lines);
            return row;
        }

        private static void Summarize(PackageAudit p, out Genie.ControlUtils.TagTone tone, out string label,
                                      out string tip) {
            tip = null;
            if (p.Problem != null) {
                tone  = Genie.ControlUtils.TagTone.Caution;
                label = "Couldn’t audit";
                tip   = p.Problem;
                return;
            }
            if (p.Blocked != PackageAudit.Block.None) {
                tone  = Genie.ControlUtils.TagTone.Caution; // nothing broken: not audited yet
                label = Look.TitleOf(p.Blocked);
                tip   = Look.BlockedTip(p.Blocked);
                return;
            }
            int unshipped = p.Dependencies.FindAll(d => d.Bucket == Bucket.StockUnshipped && !d.ShippedByMod).Count;
            int lost      = p.Dependencies.FindAll(d => d.Bucket == Bucket.NotLoaded).Count;
            int warnings  = 0;
            foreach (Check c in p.Checks) {
                if (c.State == CheckState.Warning) warnings += c.Items.Count;
            }
            tone  = Genie.ControlUtils.TagTone.Danger;
            label = unshipped > 0 ? unshipped + " unshipped" : lost > 0 ? lost + " not loaded" : null;
            if (label != null) return;
            tone  = warnings > 0 ? Genie.ControlUtils.TagTone.Warning : Genie.ControlUtils.TagTone.Neutral;
            label = warnings > 0 ? Genie.TextUtils.Count(warnings, "warning") : null;
        }

        // The find button, or for a package that didn't load a warning where it would be.
        private FrameworkElement Lead(string package, bool notLoaded) {
            if (!notLoaded) return Genie.ControlUtils.CreateFindPackageIcon(package, AfterFind);
            Border slot = new Border {
                Width      = 18,
                Child      = Genie.ControlUtils.CreateWarningIcon(),
                Background = Brushes.Transparent,
            };
            Genie.ControlUtils.SetToolTip(slot, "Not loaded");
            return slot;
        }

        private static TextBlock SubText(string text) {
            return new TextBlock {
                Text              = text,
                FontSize          = Genie.Theme.FontSizeTag,
                Foreground        = Genie.Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };
        }

        private static string Display(ModDependency d) {
            return d.IsMod ? d.Name : Look.FileName(d.Name, d.First.File);
        }

        // A package's copies on disk; a mod's folders.
        private static List<Copy> CopiesOf(ModDependency d) {
            if (!d.IsMod) return Copy.Of(d.Name, d.First.File);
            return d.Roots.ConvertAll(r => new Copy { File = r, At = Locations.Of(r) });
        }

        private static bool IsInMemory(PackageAudit p) {
            return p.Blocked != PackageAudit.Block.NotLoaded;
        }

        // == Details ==================================================================================

        private void ShowEmpty(string hint) {
            ShowCard(null);
            _details.Visibility    = Visibility.Collapsed;
            _emptyState.Visibility = Visibility.Visible;
            _emptyState.Hint       = hint;
        }

        private void ShowBody() {
            ShowCard(null);
            _emptyState.Visibility = Visibility.Collapsed;
            _details.Visibility    = Visibility.Visible;
            _detailsBody.Children.Clear();
        }

        // A blocked package's card over the details pane; null lifts it. Only this pane: the mod's rows are whole.
        private void ShowCard(PackageAudit p) {
            bool on                  = p != null;
            _veil.Visibility         = on ? Visibility.Visible : Visibility.Collapsed;
            _placeholders.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            _emptyState.Visibility = Visibility.Collapsed;
            _details.Visibility    = Visibility.Collapsed;
            PackagePanel.FillCard(_card, p, ReloadButton);
        }

        private void ShowDetails() {
            if (_audit == null) return;
            object        tag = _list.SelectedTag<object>();
            ModDependency d   = tag as ModDependency;
            Check c           = tag as Check;
            PackageAudit p    = tag as PackageAudit;
            if (d != null)
                ShowDependency(d);
            else if (c != null)
                ShowCheck(c);
            else if (p != null && p.Problem != null)
                ShowEmpty(p.Problem);
            else if (p != null && p.Blocked != PackageAudit.Block.None)
                ShowCard(p);
            else if (p != null)
                ShowPackage(p);
            else
                ShowEmpty(SelectHint);
        }

        // Caption, then [find] name [tag] (an Other mod: its name), its copies or folders, the note, and what uses it.
        private void ShowDependency(ModDependency d) {
            ShowBody();
            AddCaption(d.IsMod ? "OTHER MOD" : d.Bucket == Bucket.StockUnshipped ? "STOCK PACKAGE" : "SOURCE PACKAGE");
            GenieTag kind =
                Genie.ControlUtils.CreateTag(Look.LabelOf(d.Bucket), Look.ToneOf(d.Bucket), Look.TipOf(d.Bucket));
            AddTitle(d.IsMod ? null : Lead(d.Name, d.Bucket == Bucket.NotLoaded), Display(d), true, kind);
            foreach (Copy copy in CopiesOf(d)) _detailsBody.Children.Add(PackagePanel.LocationLine(copy));
            AddNote(Genie.ControlUtils.CreateNote(Look.ToneOf(d.Bucket), null,
                                                  d.IsMod ? Look.NoteOf(d.Bucket) : Look.NoteOf(d.First)));
            AddListLine("Used by", delegate { return UsedByText(d); }, "Copy what uses it");
            _detailsBody.Children.Add(UsedByTable(d));
        }

        // As the package view's, over the mod: its rule, and each object's package beside it.
        private void ShowCheck(Check c) {
            CheckState s = c.State;
            ShowBody();
            AddCaption("CHECK");
            AddTitle(null, c.Name, false, Genie.ControlUtils.CreateTag(Look.LabelOf(s), Look.ToneOf(s), Look.TipOf(s)));
            _detailsBody.Children.Add(new TextBlock {
                Text       = c.Rule,
                FontSize   = Genie.Theme.FontSizeLabel,
                Foreground = Genie.Theme.TextSecondary,
            });
            AddNote(PackagePanel.IsBlocked(c) ? PackagePanel.BlockedNoteBox(c, ReloadButton)
                                              : Genie.ControlUtils.CreateNote(Look.ToneOf(s), null, c.NoteText));
            if (c.Footnote != null) {
                TextBlock foot = Genie.ControlUtils.CreateNoteText(c.Footnote);
                foot.Margin    = new Thickness(0, 6, 0, 0);
                _detailsBody.Children.Add(foot);
            }
            if (s == CheckState.NotRun || s == CheckState.Skipped) return;
            List<CheckItem> shown = PackagePanel.AddClassChips(_detailsBody, c, _classChips);
            AddListLine(c.ListLabel, delegate { return CheckText(c); }, "Copy the list");
            _detailsBody.Children.Add(CheckTable(c, shown));
            PackagePanel.AddHiddenLine(_detailsBody, c, shown, _classChips);
        }

        // The checks' row lines and the details, as the package view's.
        private void ShowHiding() {
            if (_audit == null) return;
            foreach (ListBoxItem li in _checks.Rows) {
                Check c = li.Tag as Check;
                if (c != null && c.HidesByClass)
                    li.Content = PackagePanel.CheckRow(c, c.SummaryHiding(_classChips.IsHidden));
            }
            ShowDetails();
        }

        // Caption, [find] name [tag], its copies, then what it uses from outside itself.
        private void ShowPackage(PackageAudit p) {
            Genie.ControlUtils.TagTone tone;
            string                     label, tip;
            Summarize(p, out tone, out label, out tip);
            ShowBody();
            AddCaption("PACKAGE");
            AddTitle(Lead(p.Package, false), Look.FileName(p.Package, p.File), true,
                     label != null ? Genie.ControlUtils.CreateTag(label, tone, tip) : null);
            foreach (Copy copy in Copy.Of(p.Package, p.File))
                _detailsBody.Children.Add(PackagePanel.LocationLine(copy));
            AddListLine("Dependencies", delegate { return PackageText(p); }, "Copy its dependencies");
            _detailsBody.Children.Add(DependencyTable(p));
        }

        private void AddCaption(string text) {
            TextBlock label = Genie.ControlUtils.CreateCaption(text);
            label.Margin    = new Thickness(0, 0, 0, 4);
            _detailsBody.Children.Add(label);
        }

        // [lead] name [tag]
        private void AddTitle(FrameworkElement lead, string text, bool mono, GenieTag tag) {
            DockPanel title = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            if (lead != null) {
                DockPanel.SetDock(lead, Dock.Left);
                title.Children.Add(lead);
            }
            TextBlock name         = mono ? Genie.ControlUtils.CreateMonoText(text, Genie.Theme.FontSizeValue + 1)
                                          : new TextBlock { Text = text, FontSize = Genie.Theme.FontSizeValue + 1 };
            name.FontWeight        = FontWeights.SemiBold;
            name.Margin            = new Thickness(lead != null ? 4 : 0, 0, 0, 0);
            name.TextTrimming      = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            StackPanel nameAndTag  = new StackPanel { Orientation = Orientation.Horizontal };
            nameAndTag.Children.Add(name);
            if (tag != null) {
                tag.Margin = new Thickness(8, 0, 0, 0);
                nameAndTag.Children.Add(tag);
            }
            title.Children.Add(nameAndTag);
            _detailsBody.Children.Add(title);
        }

        private void AddNote(Border note) {
            note.Margin = new Thickness(0, 8, 0, 0);
            _detailsBody.Children.Add(note);
        }

        // Label ............ [copy], as the package view's.
        private void AddListLine(string label, Func<string> text, string tooltip) {
            DockPanel line = new DockPanel { Margin = new Thickness(0, 10, 0, 3) };
            Border    copy = Genie.ControlUtils.CreateCopyIcon(text, tooltip);
            DockPanel.SetDock(copy, Dock.Right);
            line.Children.Add(copy);
            TextBlock caption         = Genie.ControlUtils.CreateFieldLabel(label);
            caption.Margin            = new Thickness(0);
            caption.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(caption);
            _detailsBody.Children.Add(line);
        }

        // == Tables ===================================================================================
        // The framework's relation tables, as the package view's.

        // Its package -> the mod's packages that use it, with their reference counts; one row per package it names.
        private UIElement UsedByTable(ModDependency d) {
            GenieRelationTable table = Genie.ControlUtils.CreateRelationTable(Display(d), _mod.Name, true);
            foreach (KeyValuePair<Dependency, List<ModDependency.Use>> g in ByProvider(d)) {
                StackPanel users = new StackPanel();
                foreach (ModDependency.Use u in g.Value)
                    users.Children.Add(PackageCell(u.User.Package, u.User.File, false,
                                                   Genie.TextUtils.Count(u.Dep.RefCount, "reference")));
                table.AddRow(PackageCell(g.Key.Package, g.Key.File, g.Key.Bucket == Bucket.NotLoaded, null), users);
            }
            return table;
        }

        // Each package it names, by name, with the mod's packages using it, by name.
        private static List<KeyValuePair<Dependency, List<ModDependency.Use>>> ByProvider(ModDependency d) {
            List<KeyValuePair<Dependency, List<ModDependency.Use>>> groups =
                new List<KeyValuePair<Dependency, List<ModDependency.Use>>>();
            foreach (ModDependency.Use u in d.Uses) {
                int i = groups.FindIndex(
                    g => string.Equals(g.Key.Package, u.Dep.Package, StringComparison.OrdinalIgnoreCase));
                if (i < 0) {
                    groups.Add(
                        new KeyValuePair<Dependency, List<ModDependency.Use>>(u.Dep, new List<ModDependency.Use>()));
                    i = groups.Count - 1;
                }
                groups[i].Value.Add(u);
            }
            groups.Sort((x, y) => string.Compare(x.Key.Package, y.Key.Package, StringComparison.OrdinalIgnoreCase));
            foreach (KeyValuePair<Dependency, List<ModDependency.Use>> g in groups)
                g.Value.Sort((x, y) =>
                                 string.Compare(x.User.Package, y.User.Package, StringComparison.OrdinalIgnoreCase));
            return groups;
        }

        // Object | Package: each object's package beside it, its detail lines under that.
        private UIElement CheckTable(Check c, List<CheckItem> shown) {
            GenieRelationTable table = Genie.ControlUtils.CreateRelationTable("Object", "Package");
            foreach (CheckItem item in shown) {
                PackageAudit owner = OwnerOf(item.PathName);
                StackPanel   right = new StackPanel();
                right.Children.Add(PackageCell(owner.Package, owner.File, false, null));
                if (item.Lines.Count > 0) {
                    FrameworkElement lines = Genie.ControlUtils.CreateLinesCell(item.Lines);
                    lines.Margin           = new Thickness(26, 0, 6, 5); // under the name, past the find icon
                    right.Children.Add(lines);
                }
                FrameworkElement obj =
                    Genie.ControlUtils.CreateAssetCell(item.PathName, item.ClassName, false, AfterFind);
                table.AddRow(obj, right);
            }
            PackagePanel.AddEmptyNote(table, c, shown);
            return table;
        }

        // Source package | its bucket and reference count, in the package view's order.
        private UIElement DependencyTable(PackageAudit p) {
            GenieRelationTable table = Genie.ControlUtils.CreateRelationTable("Source package", "Bucket");
            foreach (Dependency d in p.Dependencies) {
                StackPanel right = new StackPanel {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(6, 7, 6, 5),
                };
                GenieTag kind          = Genie.ControlUtils.CreateTag(Look.LabelOf(d), Look.ToneOf(d), Look.TipOf(d));
                kind.VerticalAlignment = VerticalAlignment.Top;
                right.Children.Add(kind);
                TextBlock refs = SubText(Genie.TextUtils.Count(d.RefCount, "reference"));
                refs.Margin    = new Thickness(6, 1, 0, 0);
                right.Children.Add(refs);
                table.AddRow(PackageCell(d.Package, d.File, d.Bucket == Bucket.NotLoaded, null), right);
            }
            if (p.Dependencies.Count == 0) table.AddNote("Uses nothing from other packages");
            return table;
        }

        // [find] Pkg.upk, an optional grey line under it. One that didn't load: a warning in the find icon's place.
        private FrameworkElement PackageCell(string package, string file, bool notLoaded, string line) {
            return Genie.ControlUtils.CreatePackageCell(package, Look.FileName(package, file), line,
                                                        notLoaded ? "Not loaded" : null, AfterFind);
        }

        // A find selects in the browser, and the view follows it back to the package view. A package or asset already
        // selected there raises nothing, so go back here too. Pinned, it stays.
        private void AfterFind() {
            if (_active && !_header.Controls.IsPinned) Exit();
        }

        // The mod's package an object path names ("Pkg.Group.Obj"), or a stand-in with no file.
        private PackageAudit OwnerOf(string pathName) {
            int          dot     = pathName.IndexOf('.');
            string       package = dot > 0 ? pathName.Substring(0, dot) : pathName;
            PackageAudit p =
                _audit.Packages.Find(x => string.Equals(x.Package, package, StringComparison.OrdinalIgnoreCase));
            return p ?? new PackageAudit { Package = package };
        }

        // == Reload Linkers ===========================================================================
        // As the package view's: one action, every reloadable package.

        private Button ReloadButton() {
            return PackagePanel.CreateReloadButton(_audit.Reloadable.Count, PackagePanel.ReloadNote, false,
                                                   ReloadLinkers);
        }

        private void ReloadLinkers() {
            if (_audit == null) return;
            List<string> failed = Genie.LinkerUtils.ReloadLinkers(_audit.Reloadable);
            if (failed.Count > 0)
                Genie.Log("Reload linkers: still no linker for " + string.Join(", ", failed) +
                          ", so they can't be audited.");
            RefreshKeepingSelection();
        }

        // == Copy =====================================================================================

        // As the table, in its order:
        //   MyMod_DLC uses from FX_Fire_Env.upk (Stock unshipped):
        //     FX_Fire_Env.upk
        //       -> MyMod_DLC_Range.umap (4 references)
        private string UsedByText(ModDependency d) {
            if (_audit == null) return null;
            StringBuilder text = new StringBuilder();
            text.AppendLine(_mod.Name + " uses from " + Display(d) + " (" + Look.LabelOf(d.Bucket) + "):");
            foreach (KeyValuePair<Dependency, List<ModDependency.Use>> g in ByProvider(d)) {
                text.AppendLine("  " + Look.FileName(g.Key.Package, g.Key.File));
                foreach (ModDependency.Use u in g.Value)
                    text.AppendLine("    -> " + Look.FileName(u.User.Package, u.User.File) + " (" +
                                    Genie.TextUtils.Count(u.Dep.RefCount, "reference") + ")");
            }
            return text.ToString();
        }

        //   MyMod_DLC: UI texture inside a group (Warning)
        //     Pkg.Group.Obj (Texture2D)
        //       Pkg.upk
        //       expected at root as Obj
        private string CheckText(Check c) {
            if (_audit == null) return null;
            StringBuilder text = new StringBuilder();
            text.AppendLine(_mod.Name + ": " + c.Name + " (" + Look.LabelOf(c.State) + ")");
            List<CheckItem> shown = c.Shown(_classChips.IsHidden);
            foreach (CheckItem item in shown) {
                PackageAudit owner = OwnerOf(item.PathName);
                text.AppendLine("  " + PackagePanel.WithClass(item.PathName, item.ClassName));
                text.AppendLine("    " + Look.FileName(owner.Package, owner.File));
                foreach (string line in item.Lines) text.AppendLine("    " + line);
            }
            if (c.Items.Count == 0) text.AppendLine("  Nothing found");
            string hidden = PackagePanel.HiddenLine(c, shown, _classChips);
            if (hidden != null) text.AppendLine("  " + hidden);
            return text.ToString();
        }

        //   MyMod_DLC_Weapons.upk uses:
        //     MyMod_Mats.upk (Other mod, 3 references)
        private static string PackageText(PackageAudit p) {
            StringBuilder text = new StringBuilder();
            text.AppendLine(Look.FileName(p.Package, p.File) + " uses:");
            foreach (Dependency d in p.Dependencies)
                text.AppendLine("  " + Look.FileName(d.Package, d.File) + " (" + Look.LabelOf(d) + ", " +
                                Genie.TextUtils.Count(d.RefCount, "reference") + ")");
            if (p.Dependencies.Count == 0) text.AppendLine("  Nothing from other packages");
            return text.ToString();
        }
    }

    // == Source Folders ===============================================================================
    // Where to look for each kind of mod, and the user's own sources. Nothing changes until Save.

    internal sealed class SourceFoldersPanel : UserControl {
        private const double LabelWidth = 84; // [ModBuddy | ...] and [name | ...]: every folder starts level
        private const double MarkWidth  = 16; // a row's warning or unsaved mark, left of it
        private const double Indent     = 18; // child rows: past the chevron, so their marks sit under the parent's
        private const double Gap        = 6;

        private readonly GenieWindow  _window;
        private readonly GenieRowList _customList;
        private readonly Button       _save;
        private readonly TextBlock    _changes;

        private readonly Dictionary<LocationKind, Folder> _primary     = new Dictionary<LocationKind, Folder>();
        private readonly Dictionary<LocationKind, List<Folder>> _extra = new Dictionary<LocationKind, List<Folder>>();
        private readonly Dictionary<LocationKind, GenieCountPill> _extraPill =
            new Dictionary<LocationKind, GenieCountPill>();
        private readonly List<Folder> _custom = new List<Folder>();

        // As saved and as detected when the dialog opened, to tell what's unsaved and what Reset would change. The
        // detected extras scan every Windows profile, too slow to repeat on each key.
        private readonly Dictionary<LocationKind, string> _savedFolder = new Dictionary<LocationKind, string>();
        private readonly Dictionary<LocationKind, List<string>> _savedExtra =
            new Dictionary<LocationKind, List<string>>();
        private readonly Dictionary<LocationKind, List<string>> _detectedExtra =
            new Dictionary<LocationKind, List<string>>();
        private readonly List<FolderSettings.Source> _savedCustom;

        private bool _ready; // rows built in the constructor aren't edits

        // One folder's row. Group takes the Warning tone while the folder is missing: the [label | folder] group, or
        // the box alone on an extra folder's row.
        private sealed class Folder {
            public LocationKind          Kind;
            public bool                  IsPrimary; // a standard row, not one of its extra folders
            public GeniePathBox          Box;
            public TextBox               Name;  // custom sources only
            public FolderSettings.Source Saved; // a custom source as saved; null for one added since
            public FrameworkElement      Group;
            public Border                Mark, Find;
            public GenieTag              Auto;  // standard rows only
            public Border                Reset; // standard rows only
            public GenieRow              Row;
        }

        internal SourceFoldersPanel(GenieWindow window, Action openLocations) {
            _window   = window;
            Grid root = new Grid();
            Grid.SetIsSharedSizeScope(root, true); // the pill and icon columns line up across both lists
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            foreach (LocationKind kind in FolderSettings.Settable) {
                _savedFolder[kind] = FolderSettings.Get(kind);
                _savedExtra[kind]  = FolderSettings.GetExtra(kind);
            }
            foreach (LocationKind kind in FolderSettings.Expandable)
                _detectedExtra[kind] = FolderSettings.DetectedExtra(kind);
            _savedCustom = FolderSettings.GetCustom();

            StackPanel   body     = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            GenieRowList standard = SourceList("Standard");
            standard.ChildIndent  = Indent;
            foreach (LocationKind kind in FolderSettings.Settable) StandardRow(standard, kind);
            SdkModRow(standard);
            StockRow(standard);
            body.Children.Add(standard);

            _customList                         = SourceList("Custom");
            _customList.Margin                  = new Thickness(0, 12, 0, 0);
            _customList.KeepExpanderSpace       = true; // its folders line up with the standard rows'
            _customList.EmptyText               = "No custom sources.";
            _customList.CaptionCount.Visibility = Visibility.Visible;
            Action addCustom                    = delegate {
                AddCustom(null);
            };
            _customList.AddCaptionAction(
                Genie.ControlUtils.CreateAddIcon(addCustom, "Add a folder of your own; its name becomes the tag."));
            foreach (FolderSettings.Source c in _savedCustom) AddCustom(c);
            body.Children.Add(_customList);

            // Under both lists: the overlap rule is for every folder, the name for custom ones.
            TextBlock foot = Genie.ControlUtils.CreateNoteText(
                "The name becomes the source tag. When folders overlap, the deepest one wins.");
            foot.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(foot);
            root.Children.Add(new ScrollViewer {
                Content                       = body,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });

            // Footer: About Sources left, where other windows show the addon's version; the unsaved count, Cancel
            // and Save right.
            Button cancel  = Genie.ControlUtils.CreateButton("Cancel");
            cancel.Click  += delegate {
                FolderSettings.SetPending(null);
                _window.Close();
            };
            _save         = Genie.ControlUtils.CreateButton("Save", Genie.ControlUtils.ButtonTone.Primary,
                                                            tooltip: "Save the folders and look again.");
            _save.Margin  = new Thickness(0);
            _save.Click  += delegate {
                Save();
            };
            ToolTipService.SetShowOnDisabled(_save, true); // its tooltip says why it's off
            _changes = new TextBlock {
                FontSize          = Genie.Theme.FontSizeLabel,
                Foreground        = Genie.Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 10, 0),
            };
            StackPanel buttons = Genie.ControlUtils.CreateButtonRow(cancel, _save);
            DockPanel  bar     = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            DockPanel.SetDock(_changes, Dock.Right);
            bar.Children.Add(_changes);
            StackPanel about = new StackPanel {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            FrameworkElement icon = Genie.ControlUtils.CreateInfoIcon(Genie.Theme.TextSecondary, 13);
            icon.Margin           = new Thickness(0, 0, 5, 0);
            about.Children.Add(icon);
            about.Children.Add(
                Genie.ControlUtils.CreateLink("About Sources", openLocations, "What each source tag means"));
            bar.Children.Add(about);
            Border footer = Genie.ControlUtils.CreateFooter(bar);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            Content = root;

            // Closing without Save, by Cancel or the window's X, drops the unsaved edit.
            Unloaded += delegate {
                FolderSettings.SetPending(null);
            };
            _ready = true;
            Update();
        }

        // == Rows =====================================================================================
        // [>] mark [label | folder  Auto] [+N] [browse | find | reset]
        // The mark: a warning for a missing folder, else a dot while the row is unsaved. Missing also tones the
        // folder. Find opens the folder in File Explorer.

        // ModBuddy and Local Mod expand to their extra folders and an add link. Reset on those asks what to reset.
        private void StandardRow(GenieRowList list, LocationKind kind) {
            string label      = Locations.LabelOf(kind);
            string detected   = FolderSettings.Detected(kind);
            Folder f          = NewFolder(kind, FolderSettings.Get(kind));
            f.IsPrimary       = true;
            string autoTip    = "Follows detection, so it moves with Steam or Documents: " + detected;
            f.Auto            = Genie.ControlUtils.CreateTag("Auto", Genie.ControlUtils.TagTone.Neutral, autoTip,
                                                             size: Genie.ControlUtils.TagSize.Small, outlined: true);
            f.Group           = LabeledGroup(label, f.Box, f.Auto);
            bool      expands = Array.IndexOf(FolderSettings.Expandable, kind) >= 0;
            GenieMenu menu    = null;
            f.Reset           = Genie.ControlUtils.CreateResetIcon(delegate {
                if (menu != null)
                    menu.Open();
                else
                    f.Box.Text = detected ?? "";
            }, detected == null ? "Nothing detected" : "Reset to the detected folder: " + detected);
            if (detected == null) Genie.ControlUtils.Block(f.Reset, "Nothing detected");
            _primary[kind] = f;
            if (!expands) {
                f.Row = list.AddRow(f.Mark, f.Group, null, Tools(f, f.Reset, label));
                return;
            }

            GenieCountPill count   = Genie.ControlUtils.CreateCountPill("");
            count.Margin           = new Thickness(0);
            _extraPill[kind]       = count;
            _extra[kind]           = new List<Folder>();
            f.Row                  = list.AddRow(f.Mark, f.Group, count, Tools(f, f.Reset, label));
            f.Row.ExpandToolTip    = "Show the extra " + label + " folders";
            f.Row.ExpandedChanged += delegate {
                Update(); // a closed row carries its extra folders' marks
            };
            string tip = kind == LocationKind.ModBuddy ? "Add a ModBuddy projects folder." : "Add a local mods folder.";
            f.Row.AddBar(AddLink("Add " + label + " folder", tip, delegate { AddExtra(kind, ""); }));
            foreach (string folder in _savedExtra[kind]) AddExtra(kind, folder);

            // Primary only, or that and the extra folders back to what detection finds (none for Local Mod). Like
            // every edit here, nothing is kept until Save.
            const string MoreUsers =
                "Also replaces the extra ModBuddy folders with those detected for every user on this PC.";
            string allNote = kind == LocationKind.ModBuddy ? MoreUsers : "Also removes the extra Local Mod folders.";
            menu           = Genie.ControlUtils.CreateMenu(f.Reset);
            menu.AddMenuItem("Reset primary", detected ?? "Nothing detected", delegate { f.Box.Text = detected ?? ""; },
                             detected != null);
            menu.AddMenuItem("Reset all", allNote, delegate {
                _ready     = false; // one edit, not one per row
                f.Box.Text = detected ?? "";
                foreach (Folder x in _extra[kind]) x.Row.Remove();
                _extra[kind].Clear();
                foreach (string folder in _detectedExtra[kind]) AddExtra(kind, folder);
                _ready = true;
                Changed();
            });
        }

        // Another folder of an expandable kind, under the primary's folder.
        private void AddExtra(LocationKind kind, string path) {
            string label = Locations.LabelOf(kind);
            Folder f     = NewFolder(kind, path);
            Action drop  = delegate {
                _extra[kind].Remove(f);
                f.Row.Remove();
                Changed();
            };
            // No group around it: a group's height, and past the label and its line above.
            f.Box.MinHeight = 24;
            f.Box.Margin    = new Thickness(LabelWidth + 1, 0, 0, 0);
            f.Group         = f.Box;
            Border remove   = Genie.ControlUtils.CreateDeleteIcon(drop, "Remove this extra " + label + " folder");
            f.Row           = _primary[kind].Row.AddRow(f.Mark, f.Box, null, Tools(f, remove, "extra " + label));
            f.Row.Fill      = Genie.Theme.PanelBackground;
            _extra[kind].Add(f);
            Changed();
        }

        // [SDK Mod | its folder, read-only  SDK]. It follows the SDK, like Stock.
        private void SdkModRow(GenieRowList list) {
            string   folder = FolderSettings.Detected(LocationKind.SdkMod);
            GenieRow row    = list.AddRow(
                null,
                SdkGroup("SDK Mod", folder, "SDK Mod is the SDK's own Content\\Mods folder, so it can't be changed."),
                null, SdkTools(folder));
            row.Fill = Genie.Theme.PanelBackground;
        }

        // [>] [Stock | the SDK, read-only  SDK]. Expands to its two content roots.
        private void StockRow(GenieRowList list) {
            string   sdk = Genie.GetSdkFolder();
            GenieRow row =
                list.AddRow(null, SdkGroup("Stock", sdk, "Stock is wherever the SDK is, so it can't be changed."), null,
                            SdkTools(sdk));
            row.Fill          = Genie.Theme.PanelBackground;
            row.ExpandToolTip = "Show the stock folders";
            foreach (string stock in FolderSettings.StockRoots) {
                GeniePathBox box = Genie.ControlUtils.CreatePathBox(stock, true);
                box.MinHeight    = 24;
                box.Margin       = new Thickness(LabelWidth + 1, 0, 0, 0);
                GenieRow root    = row.AddRow(null, box, null, SdkTools(stock));
                root.Fill        = Genie.Theme.PanelBackground;
            }
        }

        // [name | folder]: the name is the source tag. saved: the source as saved, or null for a new row.
        private void AddCustom(FolderSettings.Source saved) {
            Folder f = NewFolder(LocationKind.Custom, saved != null ? saved.Path : "");
            f.Saved  = saved;
            f.Name   = Genie.ControlUtils.CreateTextBox(
                saved != null ? saved.Name : "", false,
                "Up to " + FolderSettings.MaxNameLength + " characters; shown as the source tag.");
            f.Name.MaxLength      = FolderSettings.MaxNameLength;
            GenieFieldGroup group = Genie.ControlUtils.CreateFieldGroup(f.Name, InGroup(f.Box, null));
            group.LabelWidth      = LabelWidth;
            f.Group               = group;
            Track(f.Name);
            Action drop = delegate {
                _custom.Remove(f);
                f.Row.Remove();
                Changed();
            };
            Border remove = Genie.ControlUtils.CreateDeleteIcon(drop, "Remove this source");
            f.Row         = _customList.AddRow(f.Mark, group, null, Tools(f, remove, "custom source"));
            _custom.Add(f);
            Changed();
        }

        private Folder NewFolder(LocationKind kind, string path) {
            Folder f = new Folder { Kind = kind };
            f.Box    = Genie.ControlUtils.CreatePathBox(path);
            f.Mark   = new Border { Width = MarkWidth, Background = Brushes.Transparent }; // its tooltip's hover
            f.Find   = Genie.ControlUtils.CreateFindIcon(delegate { Genie.FileUtils.OpenFolder(FolderOf(f)); },
                                                         "Show in File Explorer");
            Track(f.Box);
            return f;
        }

        // [browse | find | last], last being reset or remove.
        private Border Tools(Folder f, Border last, string what) {
            Border browse = Genie.ControlUtils.CreateBrowseIcon(delegate {
                string picked = Genie.WindowUtils.PickFolder(this, "Choose the " + what + " folder", f.Box.Text.Trim());
                if (picked != null) f.Box.Text = picked;
            }, "Choose a folder…");
            return Genie.ControlUtils.CreateIconGroup(Genie.ControlUtils.ButtonSize.Small, browse, f.Find, last);
        }

        // A row that follows the SDK: browse and reset stay, disabled, so its find lines up with the rest.
        private static Border SdkTools(string folder) {
            Border browse = Genie.ControlUtils.CreateBrowseIcon(null);
            Border reset  = Genie.ControlUtils.CreateResetIcon(null);
            Genie.ControlUtils.Block(browse, "Follows the SDK");
            Genie.ControlUtils.Block(reset, "Follows the SDK");
            Border find = Genie.ControlUtils.CreateFindIcon(delegate { Genie.FileUtils.OpenFolder(folder); },
                                                            "Show in File Explorer");
            if (!Directory.Exists(folder ?? "")) Genie.ControlUtils.Block(find, "Not found"); // OpenFolder only logs
            return Genie.ControlUtils.CreateIconGroup(Genie.ControlUtils.ButtonSize.Small, browse, find, reset);
        }

        // [label | folder  SDK], grey: it can't be set here.
        private static GenieFieldGroup SdkGroup(string label, string folder, string why) {
            GenieTag sdk = Genie.ControlUtils.CreateTag("SDK", Genie.ControlUtils.TagTone.Neutral, why,
                                                        size: Genie.ControlUtils.TagSize.Small, outlined: true,
                                                        icon: Genie.ControlUtils.CreateLockIcon());
            return LabeledGroup(label, Genie.ControlUtils.CreatePathBox(folder, true), sdk);
        }

        private static GenieFieldGroup LabeledGroup(string label, GeniePathBox box, GenieTag chip) {
            GenieFieldGroup group = Genie.ControlUtils.CreateFieldGroup(label, InGroup(box, chip));
            group.LabelWidth      = LabelWidth;
            return group;
        }

        // The box and a chip after it, as a group's field. The group strips a text box's border only when the box is
        // the whole field, so here it's done by hand. A read-only box keeps its grey.
        private static UIElement InGroup(GeniePathBox box, GenieTag chip) {
            box.BorderThickness   = new Thickness(0);
            box.VerticalAlignment = VerticalAlignment.Stretch;
            if (!box.IsReadOnly) box.Background = Brushes.Transparent;
            DockPanel field = new DockPanel { Background = box.IsReadOnly ? Genie.Theme.PanelBackground : null };
            if (chip != null) {
                chip.Margin            = new Thickness(4, 0, 5, 0);
                chip.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(chip, Dock.Right);
                field.Children.Add(chip);
            }
            field.Children.Add(box);
            return field;
        }

        // [+] Add ModBuddy folder, under the extra folders' boxes.
        private static UIElement AddLink(string text, string tooltip, Action onAdd) {
            StackPanel add = new StackPanel {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(MarkWidth + Gap + LabelWidth + 1 - 4, 0, 0, 0), // the + icon's inset
            };
            add.Children.Add(Genie.ControlUtils.CreateAddIcon(onAdd, tooltip));
            TextBlock link = Genie.ControlUtils.CreateLink(text, onAdd, tooltip);
            link.Margin    = new Thickness(3, 0, 0, 0);
            add.Children.Add(link);
            return add;
        }

        // mark | folder group | pill | icons, the same in both lists so they line up (the root's scope).
        private static GenieRowList SourceList(string caption) {
            GenieRowList list  = Genie.ControlUtils.CreateRowList();
            list.Caption       = caption;
            list.SharedColumns = false;
            list.ColumnGap     = Gap;
            list.SetColumns(GenieColumn.Fixed(MarkWidth), GenieColumn.Fill, GenieColumn.Auto, GenieColumn.Auto);
            return list;
        }

        // == State ====================================================================================

        // The folder a row stands for: a standard row's empty box is the detected folder, as Save stores it.
        private static string FolderOf(Folder f) {
            string folder = f.Box.Text.Trim();
            if (folder.Length > 0 || !f.IsPrimary) return folder;
            return FolderSettings.Detected(f.Kind) ?? "";
        }

        // A custom row as Save would store it, or null while it lacks a name or a folder. Nothing names a source for
        // the user: one without a name can't be saved.
        private static FolderSettings.Source SourceOf(Folder f) {
            string folder = f.Box.Text.Trim();
            string name   = f.Name.Text.Trim();
            if (folder.Length == 0 || name.Length == 0) return null;
            return new FolderSettings.Source { Name = name, Path = folder };
        }

        // Every row's look after an edit: the tones and marks, Auto, Reset, the pills, the unsaved count and Save.
        private void Update() {
            int changes = 0;
            foreach (KeyValuePair<LocationKind, Folder> p in _primary) {
                LocationKind kind     = p.Key;
                Folder       f        = p.Value;
                string       folder   = FolderOf(f);
                string       detected = FolderSettings.Detected(kind);
                bool auto             = detected != null && FolderSettings.SameFolder(folder, detected);
                bool unsaved          = !FolderSettings.SameFolder(folder, _savedFolder[kind]);
                if (unsaved) changes++;
                f.Auto.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;

                // Extra folders, if the kind has them. A closed row shows their marks on itself, and a removed
                // folder has no row left to show one.
                bool         resettable = !auto;
                int          hiddenLost = 0;
                List<Folder> extra;
                if (_extra.TryGetValue(kind, out extra)) {
                    bool         open = f.Row.IsExpanded;
                    List<string> now  = new List<string>();
                    int          lost = 0;
                    foreach (Folder x in extra) {
                        string path = FolderOf(x);
                        bool   xNew =
                            path.Length > 0 && !_savedExtra[kind].Exists(s => FolderSettings.SameFolder(s, path));
                        if (path.Length > 0) now.Add(path);
                        if (xNew) changes++;
                        if (xNew && !open) unsaved = true;
                        if (Paint(x, path, xNew, null)) lost++;
                    }
                    int removed =
                        _savedExtra[kind].FindAll(s => !now.Exists(n => FolderSettings.SameFolder(n, s))).Count;
                    changes += removed;
                    if (removed > 0) unsaved = true;
                    if (!open) hiddenLost = lost;
                    resettable |= !SameFolders(now, _detectedExtra[kind]);

                    GenieCountPill pill = _extraPill[kind];
                    pill.Text           = "+" + extra.Count;
                    pill.Visibility     = extra.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    pill.Tone =
                        hiddenLost > 0 ? Genie.ControlUtils.TagTone.Warning : Genie.ControlUtils.TagTone.Neutral;
                    Genie.ControlUtils.SetToolTip(
                        pill, Genie.TextUtils.Count(extra.Count, "extra " + Locations.LabelOf(kind) + " folder") +
                                  (lost > 0 ? ", " + lost + " not found" : ""));
                }
                string lostNote =
                    hiddenLost == 0 ? null : Genie.TextUtils.Count(hiddenLost, "extra folder") + " not found";
                Paint(f, folder, unsaved, lostNote);
                if (detected == null) continue; // Reset stays blocked: nothing detected
                if (resettable)
                    Genie.ControlUtils.Unblock(f.Reset);
                else
                    Genie.ControlUtils.Block(f.Reset, "Already the detected folder");
            }

            // Each custom row against its saved self, so an edited row is one change, not a removal and an add.
            bool incomplete = false;
            int  kept       = 0;
            foreach (Folder f in _custom) {
                FolderSettings.Source s = SourceOf(f);
                if (s == null) incomplete = true;
                if (f.Saved != null) kept++;
                bool unsaved = f.Saved == null || s == null || !SameSource(f.Saved, s);
                if (unsaved) changes++;
                Paint(f, f.Box.Text.Trim(), unsaved, null);
            }
            changes                       += _savedCustom.Count - kept; // removed rows
            _customList.CaptionCount.Text  = _custom.Count.ToString();

            _changes.Text   = changes == 0 ? "" : Genie.TextUtils.Count(changes, "unsaved change");
            _save.IsEnabled = changes > 0 && !incomplete;
            Genie.ControlUtils.SetToolTip(_save, incomplete    ? "Name each new source and choose its folder first."
                                                 : changes > 0 ? "Save the folders and look again."
                                                               : "Nothing to save.");
        }

        // A folder's tone, find icon and mark. hiddenLost: why a closed row's own mark warns for its extra folders,
        // or null. Returns whether the folder itself is missing.
        private static bool Paint(Folder f, string folder, bool unsaved, string hiddenLost) {
            bool missing = folder.Length > 0 && !Directory.Exists(folder);
            if (missing)
                Genie.ControlUtils.SetTone(f.Group, Genie.ControlUtils.TagTone.Warning, "Not found: " + folder);
            else
                Genie.ControlUtils.ClearTone(f.Group);
            // OpenFolder only logs for a missing folder.
            if (folder.Length == 0)
                Genie.ControlUtils.Block(f.Find, "No folder yet");
            else if (missing)
                Genie.ControlUtils.Block(f.Find, "Not found");
            else
                Genie.ControlUtils.Unblock(f.Find);

            List<string> why = new List<string>();
            if (missing) why.Add("Not found");
            if (hiddenLost != null) why.Add(hiddenLost);
            if (unsaved) why.Add(why.Count == 0 ? "Unsaved. Save to keep it." : "Unsaved");
            if (why.Count == 0) {
                f.Mark.Child = null;
                Genie.ControlUtils.SetToolTip(f.Mark, null);
                return false;
            }
            f.Mark.Child = (missing || hiddenLost != null)
                               ? Genie.ControlUtils.CreateWarningIcon(Genie.Theme.WarningTagText, 12)
                               : Genie.ControlUtils.CreateDot(Genie.ControlUtils.TagTone.Warning);
            Genie.ControlUtils.SetToolTip(f.Mark, string.Join(". ", why.ToArray()));
            return missing;
        }

        private static bool SameSource(FolderSettings.Source a, FolderSettings.Source b) {
            return a.Name == b.Name && FolderSettings.SameFolder(a.Path, b.Path);
        }

        private static bool SameFolders(List<string> a, List<string> b) {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) {
                if (!FolderSettings.SameFolder(a[i], b[i])) return false;
            }
            return true;
        }

        // == Save =====================================================================================

        // Every edit is shared as it happens, so About Sources shows the dialog's folders, not the saved ones.
        private void Track(TextBox box) {
            box.TextChanged += delegate {
                Changed();
            };
        }

        private void Changed() {
            if (!_ready) return;
            FolderSettings.SetPending(Collect());
            Update();
        }

        private void Save() {
            FolderSettings.Save(Collect());
            _window.Close();
        }

        // What Save would store. A row with no folder, or a custom one with no name, is left out: Save stays off
        // while a custom row lacks either, so only About Sources' preview ever skips one.
        private FolderSettings.Edit Collect() {
            FolderSettings.Edit edit = new FolderSettings.Edit();
            foreach (KeyValuePair<LocationKind, Folder> p in _primary) edit.Folders[p.Key] = p.Value.Box.Text.Trim();
            foreach (KeyValuePair<LocationKind, List<Folder>> e in _extra) {
                List<string> paths = new List<string>();
                foreach (Folder x in e.Value) {
                    if (x.Box.Text.Trim().Length > 0) paths.Add(x.Box.Text.Trim());
                }
                edit.Extra[e.Key] = paths;
            }
            foreach (Folder f in _custom) {
                FolderSettings.Source s = SourceOf(f);
                if (s != null) edit.Custom.Add(s);
            }
            return edit;
        }
    }

    // == About Sources ================================================================================
    // What each source tag means and the folder behind it, best to worst. Opened from the gear menu or Source
    // Folders' footer; shows Source Folders' unsaved edits while that dialog holds any, each row tagged Unsaved.

    internal sealed class AboutSourcesPanel : UserControl {
        private const double TagWidth = 86; // the details panel's tag column

        private readonly StackPanel _rows = new StackPanel();

        internal AboutSourcesPanel(GenieWindow window) {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel body    = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
            TextBlock  caption = Genie.ControlUtils.CreateCaption("WHERE A PACKAGE CAN LOAD FROM");
            caption.Margin     = new Thickness(0, 0, 0, 2);
            body.Children.Add(caption);
            body.Children.Add(_rows);
            // Scrolls only once custom sources outgrow the window.
            root.Children.Add(new ScrollViewer {
                Content                       = body,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });

            Button close               = Genie.ControlUtils.CreateButton("Close");
            close.Margin               = new Thickness(0);
            close.HorizontalAlignment  = HorizontalAlignment.Right;
            close.Click               += delegate {
                window.Close();
            };
            Border footer = Genie.ControlUtils.CreateFooter(close);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            Content = root;

            Genie.ControlUtils.WhileLoaded(this, Attach, Detach);
        }

        private void Attach() {
            FolderSettings.Changed        += Fill;
            FolderSettings.PendingChanged += Fill;
            Fill();
        }

        private void Detach() {
            FolderSettings.Changed        -= Fill;
            FolderSettings.PendingChanged -= Fill;
        }

        private void Fill() {
            _rows.Children.Clear();
            foreach (LocationKind kind in Enum.GetValues(typeof(LocationKind))) {
                if (kind != LocationKind.Custom) {
                    AddRow(kind, Locations.LabelOf(kind), FoldersOf(kind), f => FolderSettings.IsUnsaved(kind, f));
                    continue;
                }
                foreach (FolderSettings.Source c in FolderSettings.ShownCustom()) {
                    bool unsaved = FolderSettings.IsUnsaved(c);
                    AddRow(kind, c.Name, new[] { c.Path }, f => unsaved);
                }
            }
        }

        // Null entries: no folder to derive (no Steam folder above the SDK, say).
        private static string[] FoldersOf(LocationKind kind) {
            switch (kind) {
                case LocationKind.Stock:
                    return FolderSettings.StockRoots;
                case LocationKind.Unknown:
                    return new string[0];
                default:
                    List<string> folders = new List<string> { FolderSettings.Shown(kind) };
                    if (Array.IndexOf(FolderSettings.Expandable, kind) >= 0)
                        folders.AddRange(FolderSettings.ShownExtra(kind));
                    return folders.ToArray();
            }
        }

        // [tag] folder [Unsaved], one per line
        //       what it means
        private void AddRow(LocationKind kind, string label, string[] folders, Predicate<string> isUnsaved) {
            DockPanel row           = new DockPanel();
            GenieTag  tag           = Genie.ControlUtils.CreateTag(label, Look.ToneOf(kind), Look.TipOf(kind));
            tag.Margin              = new Thickness(0);
            tag.HorizontalAlignment = HorizontalAlignment.Left;
            tag.VerticalAlignment   = VerticalAlignment.Top;
            Border slot             = new Border { Width = TagWidth, Child = tag, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(slot, Dock.Left);
            row.Children.Add(slot);

            StackPanel lines = new StackPanel();
            if (kind == LocationKind.Unknown) lines.Children.Add(Line(Muted("Anywhere else", true), false));
            foreach (string folder in folders) {
                FrameworkElement shown =
                    Directory.Exists(folder ?? "") // as Source Folders' warning marks say it
                        ? (FrameworkElement)Genie.ControlUtils.CreatePathText(folder, Genie.Theme.FontSizeTag + 0.5)
                        : Muted("Not found", false);
                lines.Children.Add(Line(shown, isUnsaved(folder)));
            }
            lines.Children.Add(Genie.ControlUtils.CreateNoteText(Look.MeaningOf(kind)));
            row.Children.Add(lines);

            _rows.Children.Add(new Border {
                Child           = row,
                BorderBrush     = Genie.Theme.Separator,
                BorderThickness = new Thickness(0, _rows.Children.Count == 0 ? 0 : 1, 0, 0),
                Padding         = new Thickness(0, 9, 0, 9),
            });
        }

        // A folder line, tagged Unsaved right after its end. Left-aligned and the tag docked first, so a long path
        // is cut to leave the tag room, and a short one keeps the tag beside it rather than at the far edge.
        private static FrameworkElement Line(FrameworkElement shown, bool unsaved) {
            shown.Margin = new Thickness(0);
            if (!unsaved) {
                shown.Margin = new Thickness(0, 0, 0, 3);
                return shown;
            }
            DockPanel line = new DockPanel {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 3),
            };
            GenieTag mark = Genie.ControlUtils.CreateTag("Unsaved", Genie.ControlUtils.TagTone.Warning,
                                                         "Unsaved in Source Folders. Save there to keep it.");
            mark.Margin   = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(mark, Dock.Right);
            line.Children.Add(mark);
            line.Children.Add(shown);
            return line;
        }

        // A path line that isn't a path.
        private static TextBlock Muted(string text, bool mono) {
            TextBlock t  = mono ? Genie.ControlUtils.CreateMonoText(text, Genie.Theme.FontSizeTag + 0.5)
                                : new TextBlock { Text = text, FontSize = Genie.Theme.FontSizeTag + 0.5 };
            t.Foreground = Genie.Theme.TextSecondary;
            return t;
        }
    }
}
