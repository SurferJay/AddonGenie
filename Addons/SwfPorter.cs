// SWF Porter: an AddonGenie addon. Place in Binaries\Win64\Addons\
// Exports the Scaleform movies (SwfMovie) in a package to files that JPEXS (FFDec) opens, with the movies
// they import, laid out as Package\Movie.swf so the imports' relative URLs resolve in JPEXS.
// Editor access and styling come from AddonGenie (Genie.*); the movie logic is this addon's own (AddonUtils).
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AddonGenie;

namespace SwfPorter {
    public sealed class Addon : IGenieAddon {
        private GenieWindowSlot _window;

        public string Name {
            get { return "SWF Porter"; }
        }
        public string Tooltip {
            get { return "Exports and imports Scaleform movies"; }
        }
        public string Version {
            get { return "1.0"; }
        }
        public string Author {
            get { return "SurferJay"; }
        }

        public void Initialize() {
            _window = new GenieWindowSlot(CreateWindow);
        }

        public void Invoke() {
            _window.Show();
        }

        private GenieWindow CreateWindow() {
            GenieWindow w = Genie.WindowUtils.CreateWindow("SWF Porter", 720, 440);
            w.MinWidth    = 420;
            w.MinHeight   = 260;
            w.Content     = new PorterPanel(w);
            return w;
        }
    }

    // == Movie Logic ==================================================================================
    // A SwfMovie keeps the whole movie as a byte array, RawDataFiraxis, declared on its parent GFxRawData.
    // Its first bytes say what it is: FWS/CWS/ZWS for SWF, GFX/CFX for Scaleform's GFx.

    internal static class AddonUtils {
        public const string MovieClass   = "GFxUI.SwfMovie";
        public const string MovieType    = "SwfMovie"; // the Content Browser's name for the class
        public const string DataProperty = "GFxUI.GFxRawData.RawDataFiraxis";

        private static int  _dataOffset = -1;
        private static bool _reportedMissing;

        /// <summary>
        /// The package's movies in memory: those in its own file (if loaded from one), those the editor knows in it
        /// (GetPackageAssets), and alsoPaths. Objects can be in memory without their package's file, so each is looked
        /// up directly. notLoaded gets the paths of the rest: known in the package, or in alsoPaths, but not loaded.
        /// </summary>
        public static List<IntPtr> GetMoviesInPackage(string packageName, IEnumerable<string> alsoPaths,
                                                      List<string> notLoaded) {
            List<IntPtr>    movies = new List<IntPtr>();
            HashSet<IntPtr> seen   = new HashSet<IntPtr>();
            HashSet<string> absent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IntPtr obj in Genie.ObjectUtils.GetObjectsInPackage(packageName)) {
                if (Genie.ObjectUtils.IsClass(obj, MovieClass) && seen.Add(obj)) movies.Add(obj);
            }
            Genie.ContentBrowserUtils.BrowserAsset[] known =
                Genie.ContentBrowserUtils.GetPackageAssets(packageName, MovieType);
            List<string> paths = new List<string>(alsoPaths);
            foreach (Genie.ContentBrowserUtils.BrowserAsset asset in known) paths.Add(asset.Path);
            foreach (string path in paths) {
                IntPtr obj = Genie.ObjectUtils.FindObject(path);
                if (obj == IntPtr.Zero) {
                    if (notLoaded != null && absent.Add(path)) notLoaded.Add(path);
                    continue;
                }
                if (Genie.ObjectUtils.IsClass(obj, MovieClass) && seen.Add(obj)) movies.Add(obj);
            }
            return movies;
        }

        /// <summary>A copy of the movie's bytes, or null if they can't be read.</summary>
        public static byte[] GetMovieData(IntPtr movie) {
            if (!EnsureDataOffset()) return null;
            return Genie.ObjectUtils.ReadByteArray(movie, _dataOffset);
        }

        /// <summary>Replaces the movie's bytes in memory and marks its package dirty. False on failure.</summary>
        public static bool SetMovieData(IntPtr movie, byte[] data) {
            if (!EnsureDataOffset()) return false;
            if (!Genie.ObjectUtils.WriteByteArray(movie, _dataOffset, data)) return false;
            Genie.ObjectUtils.MarkPackageDirty(movie);
            return true;
        }

        private static bool EnsureDataOffset() {
            if (_dataOffset >= 0) return true;
            _dataOffset = Genie.ObjectUtils.GetPropertyOffset(DataProperty);
            if (_dataOffset >= 0) return true;
            if (!_reportedMissing) Genie.Log("Can't find " + DataProperty + ", so movies can't be read.");
            _reportedMissing = true;
            return false;
        }

        /// <summary>"SWF", "SWF (zlib)", "SWF (LZMA)", "GFx", "GFx (zlib)", or "Unknown".</summary>
        public static string GetFormat(byte[] data) {
            switch (Signature(data)) {
                case "FWS":
                    return "SWF";
                case "CWS":
                    return "SWF (zlib)";
                case "ZWS":
                    return "SWF (LZMA)";
                case "GFX":
                    return "GFx";
                case "CFX":
                    return "GFx (zlib)";
                default:
                    return "Unknown";
            }
        }

        private static string Signature(byte[] data) {
            return (data == null || data.Length < 3)
                       ? ""
                       : new string(new char[] { (char)data[0], (char)data[1], (char)data[2] });
        }

        // == Imports: Movies Whose Symbols This Movie Uses ============================================
        // The movie names each import by a URL relative to its own file ("..\gfxGamepadIcons\GamepadIcons.swf"),
        // in ImportAssets / ImportAssets2 tags. Scaleform-in-UE3 keeps movies at Package\Movie.swf, so the URL's
        // folders and name give the imported movie's path in the package ("gfxGamepadIcons.GamepadIcons").

        /// <summary>The import URLs in the movie, in file order (empty if none, or if it can't be parsed).</summary>
        public static List<string> GetImportUrls(byte[] data) {
            List<string> urls = new List<string>();
            ForEachTag(data, delegate(byte[] body, int code, int pos, int length) {
                if (code != 57 && code != 71) return; // ImportAssets, ImportAssets2: the URL comes first
                int end = Array.IndexOf(body, (byte)0, pos, length);
                if (end > pos) urls.Add(System.Text.Encoding.UTF8.GetString(body, pos, end - pos));
            });
            return urls;
        }

        // == Images: GFx Keeps Them Outside the Movie =================================================
        // When the editor imported the movie, Scaleform's exporter took its images out and left a
        // DefineExternalImage2 tag (code 1009) for each, naming a file next to the movie ("skull.tga"); the image
        // itself became a Texture2D beside the movie in its package (gfxAlerts.skull). A viewer such as JPEXS
        // looks for that file relative to the movie's own file. (DefineExternalImage, 1001, is the older form.)

        /// <summary>The image files the movie names, in file order, each once ("skull.tga").</summary>
        public static List<string> GetExternalImages(byte[] data) {
            List<string> files = new List<string>();
            ForEachTag(data, delegate(byte[] body, int code, int pos, int length) {
                int at = code == 1009 ? pos + 10 : code == 1001 ? pos + 8 : -1; // past the IDs, format and size
                if (at < 0) return;
                int    end        = pos + length;
                string exportName = ReadShortString(body, ref at, end);
                string file       = at < end ? ReadShortString(body, ref at, end) : null;
                if (string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(exportName)) file = exportName + ".tga";
                if (!string.IsNullOrEmpty(file) &&
                    !files.Exists(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
                    files.Add(file);
            });
            return files;
        }

        // A length byte, then the text.
        private static string ReadShortString(byte[] body, ref int at, int end) {
            if (at >= end) return null;
            int n = body[at++];
            if (at + n > end) {
                at = end;
                return null;
            }
            string s  = System.Text.Encoding.UTF8.GetString(body, at, n);
            at       += n;
            return s;
        }

        private delegate void TagVisitor(byte[] body, int code, int pos, int length);

        // Walks the movie's tags (after the header and frame rectangle), stopping at End or anything malformed.
        private static void ForEachTag(byte[] data, TagVisitor visit) {
            byte[] body = Body(data);
            if (body == null) return;
            try {
                int pos    = 0;
                int nbits  = body[0] >> 3; // frame RECT: 5-bit size, then 4 values
                pos       += (5 + 4 * nbits + 7) / 8;
                pos       += 4; // frame rate, frame count
                while (pos + 2 <= body.Length) {
                    int header  = body[pos] | (body[pos + 1] << 8);
                    pos        += 2;
                    int code = header >> 6, length = header & 0x3F;
                    if (length == 0x3F) {
                        if (pos + 4 > body.Length) break;
                        length  = BitConverter.ToInt32(body, pos);
                        pos    += 4;
                    }
                    if (length < 0 || pos + length > body.Length) break;
                    if (code == 0) break; // End
                    visit(body, code, pos, length);
                    pos += length;
                }
            } catch (Exception ex) {
                Genie.Log("Couldn't read the movie's tags: " + ex.Message);
            }
        }

        /// <summary>
        /// The package path an import URL points at: "..\gfxGamepadIcons\GamepadIcons.swf" ->
        /// "gfxGamepadIcons.GamepadIcons".
        /// </summary>
        public static string GetImportPath(string url) {
            List<string> parts = new List<string>();
            foreach (string part in url.Replace('/', '\\').Split('\\')) {
                if (part.Length > 0 && part != "." && part != "..") parts.Add(part);
            }
            if (parts.Count == 0) return null;
            parts[parts.Count - 1] = Path.GetFileNameWithoutExtension(parts[parts.Count - 1]);
            return string.Join(".", parts.ToArray());
        }

        // == Staging Folders: Where Each Movie Lives Under the Export Folder ==========================
        // A movie's file sits in its staging folder, where other movies' import URLs point, so JPEXS finds it.
        // For the SDK's movies that's fixed: the table below, built from every movie in the frozen SDK and every
        // import URL in them. Each is its package's name except gfxfontlib, which every movie imports from
        // "common assets". Keyed by movie name, so a copy in another package (gfxXPACK_ChosenInfo55.XPACK_ChosenInfo)
        // shares the original's folder. A movie not in the SDK uses its own package path.
        // clang-format off: aligned as one table
        private static readonly Dictionary<string, string> SdkStaging =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "Alerts",                         "gfxAlerts" },
                { "AnchoredMessageMgr",             "gfxAnchoredMessageMgr" },
                { "Armory",                         "gfxArmory" },
                { "BLACKMARKET",                    "gfxBlackMarket" },
                { "ChallengeHUD",                   "gfxChallengeHUD" },
                { "ChallengePostGame",              "gfxChallengePostGame" },
                { "ChallengeScoreScreen",           "gfxChallengeScoreScreen" },
                { "ChallengeUnlocks",               "gfxChallengeUnlocks" },
                { "Chat",                           "gfxChat" },
                { "ChooseFacility",                 "gfxChooseFacility" },
                { "CombatLose",                     "gfxCombatLose" },
                { "CommLink",                       "gfxCommLink" },
                { "Components",                     "gfxcomponents" },
                { "Console",                        "gfxconsole" },
                { "ControllerMap",                  "gfxControllerMap" },
                { "CouncilScreen",                  "gfxCouncilScreen" },
                { "Credits",                        "gfxCredits" },
                { "DailyChallenge",                 "gfxDailyChallenge" },
                { "DarkEvents",                     "gfxDarkEvents" },
                { "Debrief",                        "gfxDebrief" },
                { "DebugSafearea",                  "gfxDebugSafearea" },
                { "DialogueBox",                    "gfxDialogueBox" },
                { "DifficultyMenu",                 "gfxDifficultyMenu" },
                { "DropshipBriefing",               "gfxDropshipBriefing" },
                { "EndGameStats",                   "gfxEndGameStats" },
                { "EnemyArrows",                    "gfxEnemyArrows" },
                { "FacilityGrid",                   "gfxFacilityGrid" },
                { "FacilityHUD",                    "gfxFacilityHUD" },
                { "FacilitySummary",                "gfxFacilitySummary" },
                { "GamepadIcons",                   "gfxGamepadIcons" },
                { "GamepadImages",                  "gfxgamepadimages" },
                { "gfxfontlib",                     "common assets" },
                { "HackingScreen",                  "gfxHackingScreen" },
                { "HuntersLodge",                   "gfxHuntersLodge" },
                { "InputDialogue",                  "gfxInputDialogue" },
                { "InterfaceMgr",                   "gfxInterfaceMgr" },
                { "Inventory",                      "gfxInventory" },
                { "LoadSave",                       "gfxLoadSave" },
                { "LoadScreenAnimation",            "gfxLoadScreenAnimation" },
                { "LootingUI",                      "gfxLootingUI" },
                { "LootRecovered",                  "gfxLootRecovered" },
                { "ManageEquipmentMenu",            "gfxManageEquipmentMenu" },
                { "MemorialDetail",                 "gfxMemorialDetail" },
                { "MessageMgr",                     "gfxMessageMgr" },
                { "MiniLoadoutEditor",              "gfxMiniLoadoutEditor" },
                { "MissionIntro",                   "gfxMissionIntro" },
                { "MissionSummary",                 "gfxMissionSummary" },
                { "MouseCursor",                    "gfxMouseCursor" },
                { "MouseGuard",                     "gfxMouseGuard" },
                { "Multiplayer",                    "gfxMultiplayer" },
                { "MultiplayerCharacterSelector",   "gfxMultiplayerCharacterSelector" },
                { "MultiplayerCustomMatch",         "gfxMultiplayerCustomMatch" },
                { "MultiplayerHUD",                 "gfxMultiplayerHUD" },
                { "MultiplayerLeaderboards",        "gfxMultiplayerLeaderboards" },
                { "MultiplayerLoadout",             "gfxMultiplayerLoadout" },
                { "MultiplayerLoadoutList",         "gfxMultiplayerLoadoutList" },
                { "MultiplayerPlayerStats",         "gfxMultiplayerPlayerStats" },
                { "MultiplayerPostMatchSummary",    "gfxMultiplayerPostMatchSummary" },
                { "MultiplayerServerBrowser",       "gfxMultiplayerServerBrowser" },
                { "MultiplayerSetup",               "gfxMultiplayerSetup" },
                { "MultiplayerShell",               "gfxMultiplayerShell" },
                { "My2kScreen",                     "gfxMy2kScreen" },
                { "NewRecruit",                     "gfxNewRecruit" },
                { "Notifications",                  "gfxNotifications" },
                { "OptionsPCScreen",                "gfxOptionsPCScreen" },
                { "OptionsScreen",                  "gfxOptionsScreen" },
                { "PauseMenu",                      "gfxPauseMenu" },
                { "PCKeybindingsScreen",            "gfxPCKeybindingsScreen" },
                { "Photobooth",                     "gfxPhotobooth" },
                { "ProgressDialogue",               "gfxProgressDialogue" },
                { "ResearchReport",                 "gfxResearchReport" },
                { "ResearchUnlocked",               "gfxResearchUnlocked" },
                { "RewardsRecap",                   "gfxRewardsRecap" },
                { "SaveLoad",                       "gfxSaveLoad" },
                { "ServerBrowser",                  "gfxServerBrowser" },
                { "ShellComponents",                "gfxShellComponents" },
                { "SoldierList",                    "gfxSoldierList" },
                { "SpecialMissionHUD",              "gfxSpecialMissionHUD" },
                { "SquadList",                      "gfxSquadList" },
                { "StartScreen",                    "gfxStartScreen" },
                { "StrategyComponents",             "gfxStrategyComponents" },
                { "StrategyMap",                    "gfxStrategyMap" },
                { "TacticalCharInfo",               "gfxTacticalCharInfo" },
                { "TacticalHUD",                    "gfxTacticalHUD" },
                { "TerrorInfo",                     "gfxTerrorInfo" },
                { "TLE_ChallengeMenu",              "gfxTLE_ChallengeMenu" },
                { "TLE_HubMenu",                    "gfxTLE_HubMenu" },
                { "TLE_HUD",                        "gfxTLE_HUD" },
                { "TLE_LadderDifficultyMenu",       "gfxTLE_LadderDifficultyMenu" },
                { "TLE_LadderLevelUp",              "gfxTLE_LadderLevelUp" },
                { "TLE_LadderMenu",                 "gfxTLE_LadderMenu" },
                { "TLE_LadderScoreScreen",          "gfxTLE_LadderScoreScreen" },
                { "TLE_SkirmishMenu",               "gfxTLE_SkirmishMenu" },
                { "TLE_SoldierInfo",                "gfxTLE_SoldierInfo" },
                { "TooltipMgr",                     "gfxTooltipMgr" },
                { "TurnOverlay",                    "gfxTurnOverlay" },
                { "TutorialBox",                    "gfxTutorialBox" },
                { "UnitFlag",                       "gfxUnitFlag" },
                { "UnitGermanMode",                 "gfxUnitGermanMode" },
                { "WorldMessageMgr",                "gfxWorldMessageMgr" },
                { "XComIcons",                      "gfxxcomicons" },
                { "XPACK_Alerts",                   "gfxXPACK_Alerts" },
                { "XPACK_ChosenEncountered",        "gfxXPACK_ChosenEncountered" },
                { "XPACK_ChosenFavored",            "gfxXPACK_ChosenFavored" },
                { "XPACK_ChosenInfo",               "gfxXPACK_ChosenInfo" },
                { "XPACK_ChosenPolicy",             "gfxXPACK_ChosenPolicy" },
                { "XPACK_ChosenPostMission",        "gfxXPACK_ChosenPostMission" },
                { "XPACK_ChosenReveal",             "gfxXPACK_ChosenReveal" },
                { "XPACK_CovertOps",                "gfxXPACK_CovertOps" },
                { "XPACK_FactionPopup",             "gfxXPACK_FactionPopup" },
                { "XPACK_FragmentRecovered",        "gfxXPACK_FragmentRecovered" },
                { "XPACK_HeroAbilitySelect",        "gfxXPACK_HeroAbilitySelect" },
                { "XPACK_PhotoboothInterfaceMgr",   "gfxXPACK_PhotoboothInterfaceMgr" },
                { "XPACK_Poster",                   "gfxXPACK_Poster" },
                { "XPACK_ReplayHUD",                "gfxXPACK_ReplayHUD" },
                { "XPACK_SitRep",                   "gfxXPACK_SitRep" },
                { "XPACK_SoldierBondAlert",         "gfxXPACK_SoldierBondAlert" },
                { "XPACK_SoldierBondConfirm",       "gfxXPACK_SoldierBondConfirm" },
                { "XPACK_SoldierBondScreen",        "gfxXPACK_SoldierBondScreen" },
                { "XPACK_SoldierCaptured",          "gfxXPACK_SoldierCaptured" },
                { "XPACK_StrategyPolicy",           "gfxXPACK_StrategyPolicy" },
                { "XPACK_TacticalHUDMessageBanner", "gfxXPACK_TacticalHUDMessageBanner" },
                { "ZelTestUI",                      "ZelTestUI" },
            };
        // clang-format on

        /// <summary>A movie's file under the export folder: "gfxAlerts\Alerts.swf".</summary>
        public static string GetStagingFile(string exportFolder, string pathName) {
            string name = Genie.PathUtils.GetLeafName(pathName);
            string file = Genie.FileUtils.ToSafeFileName(name) + ".swf";
            string folder;
            if (SdkStaging.TryGetValue(name, out folder)) return Path.Combine(Path.Combine(exportFolder, folder), file);

            // Not an SDK movie: its package path as folders, the layout import URLs assume.
            string[] parts = pathName.Split('.');
            for (int i = 0; i < parts.Length; i++) parts[i] = Genie.FileUtils.ToSafeFileName(parts[i]);
            return Path.Combine(exportFolder, string.Join("\\", parts) + ".swf");
        }

        // The tags, after the 8-byte header; inflated for CWS/CFX (zlib). Null for LZMA or anything unknown.
        private static byte[] Body(byte[] data) {
            if (data == null || data.Length < 9) return null;
            string signature = Signature(data);
            if (signature == "FWS" || signature == "GFX") {
                byte[] body = new byte[data.Length - 8];
                Array.Copy(data, 8, body, 0, body.Length);
                return body;
            }
            if (signature != "CWS" && signature != "CFX") return null;

            // DeflateStream wants raw deflate: skip the 8-byte header and the 2-byte zlib header.
            using (MemoryStream compressed = new MemoryStream(data, 10, data.Length - 10)) {
                using (System.IO.Compression.DeflateStream inflate = new System.IO.Compression.DeflateStream(
                           compressed, System.IO.Compression.CompressionMode.Decompress)) {
                    using (MemoryStream body = new MemoryStream()) {
                        inflate.CopyTo(body);
                        return body.ToArray();
                    }
                }
            }
        }

        public static string GetSizeText(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        /// <summary>
        /// How a movie's size changes on import: "37 bytes larger", "same size, contents differ", "605 KB → 612 KB".
        /// </summary>
        public static string GetSizeChangeText(long oldBytes, long newBytes) {
            long diff = newBytes - oldBytes;
            if (diff == 0) return "same size, contents differ";
            string oldText = GetSizeText(oldBytes), newText = GetSizeText(newBytes);
            if (oldText != newText) return oldText + " \u2192 " + newText;
            long n = Math.Abs(diff);
            return Genie.TextUtils.Count(n, "byte") + (diff > 0 ? " larger" : " smaller");
        }
    }

    internal sealed class MovieItem {
        public IntPtr         Movie;    // Zero until loaded
        public string         PathName; // "Pkg.Group.Name"
        public string         Name;     // "Name"
        public string         File;     // where export writes it; null for its staging file
        public ContentControl Save;     // its save icon in the list
    }

    // One import of the selected movie: the URL the movie names, and the movie it resolves to.
    internal sealed class ImportItem {
        public string Url;
        public string PathName; // the resolved movie's path, or null if missing
        public IntPtr Movie;    // Zero if missing
        public byte[] Data;     // null if missing or unreadable
        public string File;     // where export writes it (and import reads it back)
    }

    // One movie to import: what it is now, and what the file holds.
    internal sealed class ImportPlan {
        public IntPtr Movie;
        public string PathName;
        public string File;
        public byte[] OldData, NewData;
        public bool Chosen; // a file picked with Import from..., not the staging file
        public bool Same {
            get { return Genie.FileUtils.IsSameContent(OldData, NewData); }
        }
    }

    internal sealed class PorterPanel : UserControl {
        private const string SelectHint = "Select an swfMovie to see its details.";
        private const string NotLoadedHint =
            "This movie couldn’t be loaded; see the log. Try Load, or load it in the Content Browser.";
        private const string FolderKey    = "ExportFolder";
        private const double DefaultSplit = 0.3; // movie names are short

        private const string NotExportedTip = "Not exported yet.";
        private const string ExportedTip    = "Exported. The SWF on disk matches the UPK.";
        private const string EditedTip      = "SWF edited on disk. Import to bring it in.";
        private const string UnsavedTip     = "UPK edited in memory. Save it to keep its changes.";
        private const string MissingTip     = "Not found in any UPK.";
        private const string NotLoadedTip   = "Not loaded yet. Selecting it loads it from its UPK.";

        private readonly GenieCountPill      _countPill;
        private readonly Button              _exportAll;
        private readonly GenieSplitButton    _importAll;
        private readonly GenieWindowControls _controls;
        private readonly GenieStatusPill     _status = Genie.ControlUtils.CreateStatusPill();
        private readonly GenieBanner         _saveBanner; // UPKs on screen with unsaved changes
        private readonly ContentControl      _detailsError, _emptyError; // the same error, where it can be seen
        private readonly GenieList           _list;
        private readonly StackPanel          _emptyArea;
        private readonly GenieEmptyState     _emptyState;
        private readonly Button              _load;
        private readonly StackPanel          _details;
        private readonly ScrollViewer        _detailsScroll;
        private readonly GenieRowList        _movieBox, _importBox;
        private readonly GenieCountPill      _importCount;
        private readonly TextBox             _folderBox;
        private readonly Border              _footer;

        // Movies being loaded for display, and those that failed to load (the Load button retries).
        private readonly HashSet<string> _loading    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loadFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string _errorLead, _errorText;
        private bool   _busy;                              // an export is running
        private bool[] _wasEnabled = { true, true, true }; // header buttons' state before the export
        private readonly Genie.ContentBrowserUtils.PackageFollower _follower;
        private IDisposable                                        _folderWatch;

        // UPKs whose state the open rows show, other than the followed package (which the follower watches). Saving
        // or unloading one fires its watch, which refreshes the icons.
        private readonly Dictionary<string, Genie.ContentBrowserUtils.PackageWatch> _upkWatches =
            new Dictionary<string, Genie.ContentBrowserUtils.PackageWatch>(StringComparer.OrdinalIgnoreCase);

        internal PorterPanel(GenieWindow window) {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // save banner
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer

            // Header: the package and its movie count; actions, then refresh, pin and the gear, on the right.
            GenieHeader header        = Genie.ControlUtils.CreatePanelHeader(window, "PACKAGE");
            _controls                 = header.Controls;
            _controls.RefreshToolTip  = "Reload the list.";
            _controls.Refresh        += delegate {
                _follower.Update();
                Refresh(false);
            };
            _exportAll = Genie.ControlUtils.CreateButton(
                "Export all",
                tooltip: "Write every movie in this package, and the movies they import, to the export folder.");
            _exportAll.Click += delegate {
                Export(_list.GetTags<MovieItem>());
            };
            header.AddAction(_exportAll);
            _importAll = Genie.ControlUtils.CreateSplitButton(
                "Import all",
                tooltip: "Import every exported file of this package's movies and their imports, edited or not.");
            _importAll.Margin  = new Thickness(0, 0, Genie.Theme.Spacing, 0);
            _importAll.Click  += delegate {
                Import(PlanImportAll(false));
            };
            _importAll.AddMenuItem("Import all exported", "Every exported file, edited or not",
                                   delegate { Import(PlanImportAll(false)); });
            _importAll.AddMenuItem("Import all edited", "Only files that differ from the package",
                                   delegate { Import(PlanImportAll(true)); });
            header.AddAction(_importAll);
            _countPill = Genie.ControlUtils.CreateCountPill("");
            header.AddPill(_countPill);
            Grid.SetRow(header, 0);
            root.Children.Add(header);
            _saveBanner            = Genie.ControlUtils.CreateBanner(Genie.ControlUtils.TagTone.Warning);
            _saveBanner.Visibility = Visibility.Collapsed;
            Grid.SetRow(_saveBanner, 1);
            root.Children.Add(_saveBanner);

            // A selected movie decides the package before other selected assets, so it can be opened.
            _follower                 = Genie.ContentBrowserUtils.FollowPackage(header);
            _follower.PreferredType   = AddonUtils.MovieType;
            _follower.PackageChanged += delegate {
                Refresh(false);
            };
            _follower.SelectionChanged += delegate {
                Refresh(false); // select the movie selected in the Content Browser
            };
            _follower.PackageModified += delegate {
                Refresh(true); // a load or a save; keep the movie shown
            };

            GenieListPane left      = Genie.ControlUtils.CreateListPane("Movies");
            left.Margin             = new Thickness(10, 8, 0, 10);
            _list                   = left.List;
            _list.SelectionChanged += delegate {
                ShowDetails();
            };

            Grid right =
                new Grid { Margin = new Thickness(14, 8, 0, 10) }; // no right margin: the scrollbar sits at the edge
            _emptyArea       = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            _emptyState      = Genie.ControlUtils.CreateEmptyState("How it works", null, SelectHint);
            StackPanel steps = new StackPanel();
            string[][] how   = {
                new[] { "Export", "a movie. It and everything it uses go to the export folder, laid out the way your " +
                                        "SWF editor expects." },
                new[] { "Edit", "the exported files in an SWF editor. An edited file turns its save icon green." },
                new[] { "Import", "brings the edited files back into their movies. Save their UPKs to keep them." },
            };
            for (int n = 0; n < how.Length; n++) {
                DockPanel step = Genie.ControlUtils.CreateStep(n + 1, how[n][0], how[n][1]);
                if (n > 0) step.Margin = new Thickness(0, 8, 0, 0);
                steps.Children.Add(step);
            }
            Border images =
                Genie.ControlUtils.CreateNote(Genie.ControlUtils.TagTone.Info, "Images",
                                              "are exported so your SWF editor can show them. Edited images aren’t " +
                                                  "imported with the movie. Update each image in the Content Browser.");
            images.Margin = new Thickness(0, 12, 0, 0);
            steps.Children.Add(images);
            _emptyState.Content = steps;
            _emptyArea.Children.Add(_emptyState);
            _load = Genie.ControlUtils.CreateButton(
                "Load", size: Genie.ControlUtils.ButtonSize.Small,
                tooltip: "Load it from disk, as the Content Browser does. Nothing is changed or saved.");
            _load.HorizontalAlignment  = HorizontalAlignment.Left;
            _load.Margin               = new Thickness(0, 10, 0, 0);
            _load.Click               += delegate {
                OnLoad();
            };
            _emptyArea.Children.Add(_load);
            _emptyError = new ContentControl {
                Focusable  = false,
                Visibility = Visibility.Collapsed,
                Margin     = new Thickness(0, 12, 0, 0),
            };
            _emptyArea.Children.Add(_emptyError);
            right.Children.Add(_emptyArea);

            // The movie and its imports, each a boxed list. Their columns line up across both boxes.
            _details = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetIsSharedSizeScope(_details, true);
            TextBlock movieTitle = Genie.ControlUtils.CreateCaption("Movie"); // section titles, as Dependency Viewer's
            movieTitle.Margin    = new Thickness(0, 0, 0, 6);
            _details.Children.Add(movieTitle);
            _movieBox = MovieBox();
            _details.Children.Add(_movieBox);
            _detailsError = new ContentControl {
                Focusable  = false,
                Visibility = Visibility.Collapsed,
                Margin     = new Thickness(0, 6, 0, 0),
            };
            _details.Children.Add(_detailsError);
            StackPanel importsHead =
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 4) };
            TextBlock importsTitle = Genie.ControlUtils.CreateCaption("Imports");
            importsTitle.Margin    = new Thickness(0, 0, 8, 0);
            importsHead.Children.Add(importsTitle);
            _importCount                   = Genie.ControlUtils.CreateCountPill("");
            _importCount.VerticalAlignment = VerticalAlignment.Center;
            importsHead.Children.Add(_importCount);
            _details.Children.Add(importsHead);
            _importBox           = MovieBox();
            _importBox.EmptyText = "None.";
            _details.Children.Add(_importBox);
            _detailsScroll = new ScrollViewer {
                Content                       = _details,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Visibility                    = Visibility.Collapsed,
            };
            right.Children.Add(_detailsScroll);
            _status.Margin = new Thickness(0, 0, 14, 0); // the pane's own bottom margin is its gap
            right.Children.Add(_status);                 // last, so it floats over the details

            // Body: movies | splitter | details.
            Grid body = Genie.ControlUtils.CreateSplitPanel(left, right, DefaultSplit, 90, 240);
            Grid.SetRow(body, 2);
            root.Children.Add(body);

            // Footer: Export to [folder] [Browse|Open]. Clear of the version label; same right edge as the header.
            DockPanel folderRow = new DockPanel { Margin = new Thickness(window.AddonInfoWidth + 12, 8, 10, 8) };
            TextBlock folderLabel         = Genie.ControlUtils.CreateFieldLabel("Export to");
            folderLabel.Margin            = new Thickness(0, 0, 8, 0);
            folderLabel.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(folderLabel, Dock.Left);
            folderRow.Children.Add(folderLabel);
            Border browse =
                Genie.ControlUtils.CreateBrowseIcon(ChangeFolder, "Choose the folder exports are written to.");
            Border openFolder =
                Genie.ControlUtils.CreateFindIcon(OpenFolder, "Open the export folder in File Explorer.");
            Border folderActions =
                Genie.ControlUtils.CreateIconGroup(Genie.ControlUtils.ButtonSize.Regular, browse, openFolder);
            folderActions.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(folderActions, Dock.Right);
            folderRow.Children.Add(folderActions);
            // Typed paths take effect on Enter or leaving the box; Escape puts the current folder back.
            _folderBox         = Genie.ControlUtils.CreatePathBox(ExportFolder);
            _folderBox.KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e) {
                if (e.Key == System.Windows.Input.Key.Enter) {
                    e.Handled = true;
                    CommitFolder();
                } else if (e.Key == System.Windows.Input.Key.Escape) {
                    e.Handled       = true;
                    _folderBox.Text = ExportFolder;
                }
            };
            _folderBox.LostKeyboardFocus += delegate {
                CommitFolder();
            };
            folderRow.Children.Add(_folderBox);
            _footer = Genie.ControlUtils.CreateFooter(folderRow, new Thickness(0)); // the row sets its own
            Grid.SetRow(_footer, 3);
            root.Children.Add(_footer);

            Content = root;
            Genie.ControlUtils.WhileLoaded(this, WatchExportFolder, Unwatch);
        }

        // == Save State ===============================================================================
        // One icon for where a movie stands: grey, never exported; blue, its exported file matches; green, the
        // file was edited on disk; orange, its UPK has unsaved changes. Unsaved wins: those changes are the ones
        // that can be lost. It trusts the UPK's own dirty flag, as the Content Browser's "*" does, so every movie in
        // a changed UPK shows it.

        private enum SaveState { NotExported, Exported, EditedOnDisk, Unsaved }

        // file: where export writes the movie. data: the movie's bytes, null if unreadable.
        private static SaveState GetSaveState(IntPtr movie, string file, byte[] data) {
            if (Genie.ObjectUtils.IsPackageDirty(movie)) return SaveState.Unsaved;
            if (file == null || !File.Exists(file)) return SaveState.NotExported;
            return IsEdited(file, data) ? SaveState.EditedOnDisk : SaveState.Exported;
        }

        private static FrameworkElement SaveIcon(SaveState state, double size) {
            Genie.ControlUtils.TagTone tone;
            string                     tip;
            switch (state) {
                case SaveState.Unsaved:
                    tone = Genie.ControlUtils.TagTone.Warning;
                    tip  = UnsavedTip;
                    break;
                case SaveState.EditedOnDisk:
                    tone = Genie.ControlUtils.TagTone.Success;
                    tip  = EditedTip;
                    break;
                case SaveState.Exported:
                    tone = Genie.ControlUtils.TagTone.Info;
                    tip  = ExportedTip;
                    break;
                default:
                    tone = Genie.ControlUtils.TagTone.Neutral;
                    tip  = NotExportedTip;
                    break;
            }
            FrameworkElement icon = Genie.ControlUtils.CreateSaveIcon(tone, size);
            Genie.ControlUtils.SetToolTip(icon, tip);
            return icon;
        }

        // In dialogs: a file that will be overwritten or imported, edited on disk.
        private static GenieTag EditedTag() {
            GenieTag tag = Genie.ControlUtils.CreateTag("Edited", Genie.ControlUtils.TagTone.Success, EditedTip);
            tag.Margin   = new Thickness(0, 0, Genie.Theme.Spacing, 0);
            tag.VerticalAlignment = VerticalAlignment.Center;
            return tag;
        }

        // A movie that isn't loaded has no icon until it loads.
        private static void SetListStatus(MovieItem item) {
            item.Save.Content = item.Movie == IntPtr.Zero
                                    ? null
                                    : SaveIcon(GetSaveState(item.Movie, RootFile(item.PathName),
                                                            AddonUtils.GetMovieData(item.Movie)),
                                               13);
        }

        private void UpdateListTags() {
            foreach (MovieItem item in _list.GetTags<MovieItem>()) SetListStatus(item);
        }

        // == Notifications ============================================================================

        // The follower watches the Content Browser; these watch the rest while the window is open.

        // Saving in an SWF editor changes the Edited tags. Null until the folder exists (the first export).
        private void WatchExportFolder() {
            if (_folderWatch != null) _folderWatch.Dispose();
            _folderWatch = Genie.FileUtils.WatchFolder(Path.GetFullPath(ExportFolder), UpdateStatus);
        }

        // Watches exactly these UPKs, reusing the watches it already has. Each pauses while the window is closed.
        private void WatchUpks(ICollection<string> upks) {
            List<string> stale = new List<string>();
            foreach (string upk in _upkWatches.Keys) {
                if (!upks.Contains(upk)) stale.Add(upk);
            }
            foreach (string upk in stale) {
                _upkWatches[upk].Dispose();
                _upkWatches.Remove(upk);
            }
            foreach (string upk in upks) {
                if (!_upkWatches.ContainsKey(upk))
                    _upkWatches[upk] = Genie.ContentBrowserUtils.WatchPackage(this, upk, UpdateStatus);
            }
        }

        // A watched UPK or the export folder changed: re-read every tag on screen.
        private void UpdateStatus() {
            UpdateListTags();
            ShowDetails();
        }

        private void Unwatch() {
            if (_folderWatch == null) return;
            _folderWatch.Dispose();
            _folderWatch = null;
        }

        // == List =====================================================================================

        // The followed package's movies, loaded or not. Selects the movie selected in the Content Browser, or with
        // keepSelection (and while pinned) the one already selected here.
        private void Refresh(bool keepSelection) {
            MovieItem       current        = Selected();
            HashSet<string> selectedMovies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string          pkg            = _follower.Package;
            if (keepSelection || _controls.IsPinned) {
                if (current != null && current.PathName != null) selectedMovies.Add(current.PathName);
            } else if (pkg != null) {
                // By name, so it works for movies that aren't loaded: the Content Browser lists those too.
                Genie.ContentBrowserUtils.BrowserAsset[] assets = Genie.ContentBrowserUtils.GetSelectedAssets();
                foreach (Genie.ContentBrowserUtils.BrowserAsset asset in assets) {
                    if (string.Equals(asset.Type, AddonUtils.MovieType, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(asset.Package, pkg, StringComparison.OrdinalIgnoreCase))
                        selectedMovies.Add(asset.Path);
                }
            }
            _list.Items.Clear();

            if (pkg == null) { // the header says so
                SetCount(-1);
                ShowEmpty(SelectHint, false);
                return;
            }

            List<MovieItem> items = new List<MovieItem>();
            try {
                List<string> notLoaded = new List<string>();
                foreach (IntPtr movie in AddonUtils.GetMoviesInPackage(pkg, selectedMovies, notLoaded)) {
                    string path = Genie.ObjectUtils.GetPathName(movie);
                    items.Add(
                        new MovieItem { Movie = movie, PathName = path, Name = Genie.PathUtils.GetLeafName(path) });
                }
                foreach (string path in notLoaded)
                    items.Add(new MovieItem { PathName = path, Name = Genie.PathUtils.GetLeafName(path) });
            } catch (Exception ex) {
                SetCount(-1);
                Genie.Log("Couldn't read package " + pkg + ": " + ex);
                ShowEmpty("Couldn’t read the package: " + ex.Message, false);
                return;
            }
            // By name, so a movie keeps its place when it loads.
            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            ListBoxItem select = null;
            foreach (MovieItem item in items) {
                ListBoxItem li = _list.AddItem(Row(item), item);
                if (select == null && item.PathName != null && selectedMovies.Contains(item.PathName)) select = li;
            }
            SetCount(items.Count);
            if (select != null) {
                select.IsSelected = true; // shows its details, loading it first if it isn't loaded
                _list.ScrollIntoView(select);
            } else
                ShowEmpty(items.Count == 0 ? "This package has no movies." : SelectHint, false);
        }

        // name ... [save icon]. A movie that isn't loaded is dimmed; selecting it loads it.
        private static DockPanel Row(MovieItem item) {
            TextBlock name    = Genie.ControlUtils.CreateMonoText(item.Name);
            name.Margin       = new Thickness(0, 1, 0, 1);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            if (item.Movie == IntPtr.Zero) {
                name.Foreground = Genie.Theme.TextSecondary;
                Genie.ControlUtils.SetToolTip(name, NotLoadedTip);
            }
            item.Save = new ContentControl {
                Focusable         = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 0, 0),
            };
            SetListStatus(item);
            DockPanel line = new DockPanel();
            DockPanel.SetDock(item.Save, Dock.Right);
            line.Children.Add(item.Save);
            line.Children.Add(name);
            return line;
        }

        // count: movies listed for the package; -1 for no package, or one that couldn't be read.
        private void SetCount(int count) {
            _countPill.Text       = Genie.TextUtils.Count(count, "movie");
            _countPill.Visibility = count >= 0 ? Visibility.Visible : Visibility.Collapsed;
            _exportAll.IsEnabled  = count > 0;
            _importAll.IsEnabled  = count > 0;
        }

        private void ShowEmpty(string hint, bool offerLoad) {
            _saveBanner.Visibility    = Visibility.Collapsed;
            _detailsScroll.Visibility = Visibility.Collapsed;
            _emptyArea.Visibility     = Visibility.Visible;
            _emptyState.Hint          = hint;
            _load.Visibility          = offerLoad ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnLoad() {
            MovieItem m = Selected();
            if (m == null) return;
            _loadFailed.Remove(m.PathName);
            ShowDetails();
        }

        private MovieItem Selected() {
            return _list.SelectedTag<MovieItem>();
        }

        // == Details ==================================================================================

        // The movie and its imports, found and loaded now, so their state is what export will do.

        private void ShowDetails() {
            MovieItem m = Selected();
            if (m == null) {
                ShowEmpty(_list.Items.Count == 0 ? "This package has no movies." : SelectHint, false);
                WatchUpks(new string[0]);
                return;
            }
            if (m.Movie == IntPtr.Zero) {
                ShowUnloaded(m);
                return;
            }
            _emptyArea.Visibility     = Visibility.Collapsed;
            _detailsScroll.Visibility = Visibility.Visible;

            byte[] data     = AddonUtils.GetMovieData(m.Movie);
            string rootFile = RootFile(m.PathName);
            _movieBox.ClearRows();
            _movieBox.AddRow(MovieRow(m.Movie, m.PathName, null, rootFile, data, true));

            List<ImportItem> imports  = new List<ImportItem>();
            HashSet<string>  seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string url in AddonUtils.GetImportUrls(data)) { // empty for unreadable data
                // A movie may import from one file in several tags, even spelled differently
                // ("../gfxComponents/components.swf" and "..\gfxComponents\components.swf"): one row per movie.
                string path;
                IntPtr obj = ResolveImport(url, out path);
                if (!seenUrls.Add(obj != IntPtr.Zero ? path : url.Replace('/', '\\'))) continue;
                imports.Add(new ImportItem {
                    Url      = url,
                    Movie    = obj,
                    PathName = obj == IntPtr.Zero ? null : path,
                    Data     = obj == IntPtr.Zero ? null : AddonUtils.GetMovieData(obj),
                    File     = obj == IntPtr.Zero ? null : ImportFile(rootFile, url, path),
                });
            }
            _importCount.Text = imports.Count.ToString();
            _importBox.ClearRows();
            foreach (ImportItem i in imports)
                _importBox.AddRow(MovieRow(i.Movie, i.PathName, i.Url, i.File, i.Data, false));

            List<string> upks = new List<string> { Genie.PathUtils.GetPackageName(m.PathName) };
            foreach (ImportItem i in imports) {
                string upk = (i.PathName == null) ? null : Genie.PathUtils.GetPackageName(i.PathName);
                if (upk != null && !upks.Contains(upk)) upks.Add(upk);
            }
            ShowSaveNote(m, imports, upks);
            string shown = _follower.Package; // the follower watches it, and a change there rebuilds the list
            upks.RemoveAll(u => string.Equals(u, shown, StringComparison.OrdinalIgnoreCase));
            WatchUpks(upks);
        }

        // Loads the movie once this pass is over, so the list and title draw first; the refresh after the load shows
        // its details. A failed load isn't retried on every refresh: the Load button tries again.
        private void ShowUnloaded(MovieItem m) {
            WatchUpks(new string[0]);
            if (_loadFailed.Contains(m.PathName)) {
                ShowEmpty(NotLoadedHint, true);
                return;
            }
            ShowEmpty("Loading " + m.Name + "…", false);
            string path = m.PathName;
            if (!_loading.Add(path)) return; // already on its way
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate {
                bool loaded = Genie.ObjectUtils.LoadObject(AddonUtils.MovieClass, path) != IntPtr.Zero;
                _loading.Remove(path);
                if (!loaded) _loadFailed.Add(path); // the engine logs why
                if (IsLoaded) Refresh(true);
            });
        }

        // One banner naming every UPK on screen with unsaved changes; hidden when there are none.
        private void ShowSaveNote(MovieItem m, List<ImportItem> imports, List<string> upks) {
            List<string> dirty = new List<string>();
            if (Genie.ObjectUtils.IsPackageDirty(m.Movie)) dirty.Add(upks[0]);
            foreach (ImportItem i in imports) {
                if (i.Movie == IntPtr.Zero || !Genie.ObjectUtils.IsPackageDirty(i.Movie)) continue;
                string upk = Genie.PathUtils.GetPackageName(i.PathName);
                if (!dirty.Contains(upk)) dirty.Add(upk);
            }
            if (dirty.Count == 0) {
                _saveBanner.Visibility = Visibility.Collapsed;
                return;
            }
            string names = dirty[dirty.Count - 1];
            if (dirty.Count > 1)
                names = string.Join(", ", dirty.GetRange(0, dirty.Count - 1).ToArray()) + " and " + names;

            _saveBanner.Text       = "Save the " + names + (dirty.Count == 1 ? " UPK" : " UPKs") +
                                     " in the Content Browser to keep the edits.";
            _saveBanner.Visibility = Visibility.Visible;
        }

        // A row's cells for the MOVIE or IMPORTS box (MovieBox): name | size | images | save | Find, Open, Export,
        // Import. The name is the movie's own; its package and exported file show on hover. movie: Zero for an
        // import that can't be found, named by its url. file: where export writes it. isSelected: the selected
        // movie's own row, its name larger and bold.
        private UIElement[] MovieRow(IntPtr movie, string pathName, string url, string file, byte[] data,
                                   bool isSelected) {
            bool      found = movie != IntPtr.Zero;
            TextBlock name  = Genie.ControlUtils.CreateMonoText(found ? Genie.PathUtils.GetLeafName(pathName) : url,
                                                                isSelected ? Genie.Theme.FontSizeValue
                                                                           : Genie.Theme.FontSizeLabel + 0.5);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            if (isSelected) name.FontWeight = FontWeights.SemiBold;
            if (!found) {
                name.Foreground = Genie.Theme.Danger;
                Genie.ControlUtils.SetToolTip(name, MissingTip);
            } else {
                Genie.ControlUtils.SetToolTip(name, pathName + "\nExported to: <Exports>\\" + ExportRelative(file));
            }

            UIElement size = null, images = null, save = null;
            if (found && data == null) {
                size = Cell(Genie.ControlUtils.CreateTag("Unreadable", Genie.ControlUtils.TagTone.Danger,
                                                         "Its movie data couldn’t be read; see the log."));
            } else if (found) {
                int    count     = AddonUtils.GetExternalImages(data).Count;
                size   = Cell(Genie.ControlUtils.CreateTag(AddonUtils.GetSizeText(data.Length),
                                                           Genie.ControlUtils.TagTone.Info, "Its size in the UPK."));
                images = Cell(ImageCountTag(count, 0));
                save   = SaveIcon(GetSaveState(movie, file, data), 14);
            }

            // A missing import has nothing to act on: its icons say so (Block).
            Border find = found ? Genie.ControlUtils.CreateFindAssetIcon(movie)
                                : Genie.ControlUtils.CreateFindIcon(null);
            Border open = Genie.ControlUtils.CreateOpenIcon(delegate { Genie.ContentBrowserUtils.OpenEditor(movie); });
            // The selected movie's Export opens a menu: with its imports, or alone. Elsewhere it exports with them.
            Action<bool> exportIt = delegate(bool withImports) {
                Export(new List<MovieItem> {
                    new MovieItem { Movie = movie, PathName = pathName, Name = name.Text, File = file },
                }, withImports);
            };
            GenieMenu exportMenu = null;
            Border    export     = Genie.ControlUtils.CreateExportIcon(
                delegate {
                    if (exportMenu != null)
                        exportMenu.Open();
                    else
                        exportIt(true);
                },
                isSelected ? "Write it to the export folder, with or without the movies it imports."
                           : "Write it, and the movies it imports, to the export folder.");
            if (isSelected) {
                exportMenu = Genie.ControlUtils.CreateMenu(export);
                exportMenu.AddMenuItem("Export with its imports", "Export it and all of its imports",
                                       delegate { exportIt(true); });
                exportMenu.AddMenuItem("Export only this movie", "Export its SWF and images",
                                       delegate { exportIt(false); });
            }

            // Import opens its menu: the exported file, shown by its folder, or any file.
            GenieMenu importMenu = null;
            Border    import     = Genie.ControlUtils.CreateImportIcon(
                delegate { importMenu.Open(); }, "Import into it, from the export folder or any file.");
            importMenu = Genie.ControlUtils.CreateMenu(import);
            GenieMenuItem fromExport = importMenu.AddMenuItem(
                "Import from export folder",
                file == null ? "" : "<Exports>\\" + Path.GetDirectoryName(ExportRelative(file)) + "\\",
                delegate { Import(PlanFor(movie, pathName, file)); }, true);
            fromExport.IsEnabled = file != null && File.Exists(file);
            Genie.ControlUtils.SetToolTip(fromExport, ImportToolTip(file, found && IsEdited(file, data)));
            importMenu.AddMenuItem("Import from…", "Choose any .swf or .gfx file",
                                   delegate { ImportFrom(movie, pathName, file); });

            Border[] icons = { find, open, export, import };
            if (!found) {
                foreach (Border icon in icons) Genie.ControlUtils.Block(icon, MissingTip);
            }
            Border actions = Genie.ControlUtils.CreateIconGroup(Genie.ControlUtils.ButtonSize.Regular, icons);
            return new UIElement[] { name, size, images, save, actions };
        }

        // How many images a movie exports with it; green, saying how many, when any were edited on disk.
        private static GenieTag ImageCountTag(int count, int edited) {
            return Genie.ControlUtils.CreateTag(
                count.ToString(), edited > 0 ? Genie.ControlUtils.TagTone.Success : Genie.ControlUtils.TagTone.Neutral,
                edited > 0 ? edited + " of " + Genie.TextUtils.Count(count, "image") + " edited on disk."
                           : "Count of images to export with it.",
                icon: Genie.ControlUtils.CreateImageIcon(null, 11));
        }

        // A tag in a row's column, right-aligned so tags of different widths end together.
        private static GenieTag Cell(GenieTag tag) {
            tag.Margin              = new Thickness(0);
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            return tag;
        }

        // The MOVIE and IMPORTS boxes: the same columns, lining up with each other (the details' scope). Size,
        // images and save collapse when no row has one, such as for a missing import.
        private static GenieRowList MovieBox() {
            GenieRowList box  = Genie.ControlUtils.CreateRowList();
            box.SharedColumns = false;
            box.RowPaddingY   = 2; // the icon group is the row's height
            box.ColumnGap     = 8;
            box.SetColumns(GenieColumn.Fill, GenieColumn.Auto, GenieColumn.Auto, GenieColumn.Auto, GenieColumn.Auto);
            return box;
        }

        // The movie an import URL names, loaded if it isn't in memory. "..\gfxGamepadIcons\GamepadIcons.swf" is
        // normally the movie GamepadIcons in package gfxGamepadIcons. Some URLs carry no package folder (the
        // engine's font library, "../common assets/gfxfontlib.swf", lives in gfxCommon), so the movie is also
        // looked up by name in the editor's asset database, whatever the Content Browser is showing, preferring
        // one in the package the URL's folder names.
        private static IntPtr ResolveImport(string url, out string path) {
            path       = AddonUtils.GetImportPath(url);
            IntPtr obj = path == null ? IntPtr.Zero : Genie.ObjectUtils.FindObject(path);
            if (obj != IntPtr.Zero && Genie.ObjectUtils.IsClass(obj, AddonUtils.MovieClass)) return obj;

            string name      = Path.GetFileNameWithoutExtension(url.Replace('/', '\\'));
            string dir       = Path.GetFileName(Path.GetDirectoryName(url.Replace('/', '\\')) ?? "");
            string candidate = null;
            foreach (string found in Genie.AssetDatabaseUtils.FindAssets(AddonUtils.MovieType, name)) {
                bool inNamedPackage =
                    string.Equals(Genie.PathUtils.GetPackageName(found), dir, StringComparison.OrdinalIgnoreCase);
                if (candidate == null || inNamedPackage) candidate = found;
            }
            if (candidate != null) path = candidate;
            if (path == null) return IntPtr.Zero;

            obj = Genie.ObjectUtils.LoadObject(AddonUtils.MovieClass, path);
            return Genie.ObjectUtils.IsClass(obj, AddonUtils.MovieClass) ? obj : IntPtr.Zero;
        }

        // == Files ====================================================================================
        // Export writes them, import reads them back.

        // A file's place under the export folder ("common assets\\gfxfontlib.swf"), as shown to the user.
        private static string ExportRelative(string file) {
            return Genie.FileUtils.GetRelativePath(file, ExportFolder) ?? file;
        }

        // What Import would do with the staging file: it needs one to exist; edited or not, it imports it.
        private static string ImportToolTip(string file, bool edited) {
            if (edited) return "Import the edited file on disk.";
            if (file == null || !File.Exists(file)) return "Export it, edit the file, then import.";
            return "The exported file hasn’t been edited.";
        }

        // A chosen movie's file: <staging folder>\Movie.swf under the export folder. Export writes it, the
        // Edited check compares it with the selected package's movie, and import reads it.
        private static string RootFile(string pathName) {
            return AddonUtils.GetStagingFile(Path.GetFullPath(ExportFolder), pathName);
        }

        // An import's file: the URL, relative to the importing movie's file (where JPEXS looks), or its own
        // package path when the URL points outside the export folder. The same rule export uses.
        private static string ImportFile(string importerFile, string url, string importPath) {
            string folder = Path.GetFullPath(ExportFolder);
            string file   = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(importerFile), url.Replace('/', '\\')));
            return Genie.FileUtils.IsUnder(file, folder) ? file : AddonUtils.GetStagingFile(folder, importPath);
        }

        // The exported file exists and differs from the movie in the package.
        private static bool IsEdited(string file, byte[] current) {
            if (current == null || file == null || !File.Exists(file)) return false;
            return !Genie.FileUtils.IsSameContent(file, current);
        }

        // == Import ===================================================================================

        // The staging file of one movie, to import whether it's edited or not.
        private static List<ImportPlan> PlanFor(IntPtr movie, string pathName, string file) {
            List<ImportPlan> plan = new List<ImportPlan>();
            Add(plan, movie, pathName, file, false, false);
            return plan;
        }

        private static void Add(List<ImportPlan> plan, IntPtr movie, string pathName, string file, bool editedOnly,
                                bool chosen) {
            if (movie == IntPtr.Zero || file == null || !File.Exists(file)) return;
            if (plan.Exists(p => p.Movie == movie)) return;
            byte[] current = AddonUtils.GetMovieData(movie);
            byte[] incoming;
            try {
                incoming = File.ReadAllBytes(file);
            } catch (Exception ex) {
                Genie.Log("Couldn't read " + file + ": " + ex.Message);
                return;
            }
            if (current == null) {
                Genie.Log("Couldn't read " + pathName + "'s movie data; not importing into it.");
                return;
            }
            if (editedOnly && Genie.FileUtils.IsSameContent(current, incoming)) return;
            plan.Add(new ImportPlan {
                Movie    = movie,
                PathName = pathName,
                File     = file,
                OldData  = current,
                NewData  = incoming,
                Chosen   = chosen,
            });
        }

        // This package's movies and their imports, from their staging files: every one that exists, or only
        // the edited ones.
        private List<ImportPlan> PlanImportAll(bool editedOnly) {
            List<ImportPlan> plan = new List<ImportPlan>();
            foreach (MovieItem m in _list.GetTags<MovieItem>()) {
                string rootFile = RootFile(m.PathName);
                Add(plan, m.Movie, m.PathName, rootFile, editedOnly, false);
                byte[] data = AddonUtils.GetMovieData(m.Movie);
                if (data == null) continue;
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string url in AddonUtils.GetImportUrls(data)) {
                    if (!seen.Add(url)) continue;
                    string path;
                    IntPtr obj = ResolveImport(url, out path);
                    if (obj != IntPtr.Zero) Add(plan, obj, path, ImportFile(rootFile, url, path), editedOnly, false);
                }
            }
            if (plan.Count == 0)
                _status.ShowInfo(editedOnly ? "Nothing to import: no exported file has been edited."
                                            : "Nothing to import: nothing has been exported for these movies.");
            return plan;
        }

        // Import from...: any .swf or .gfx file the user picks, into this movie.
        private void ImportFrom(IntPtr movie, string pathName, string stagingFile) {
            if (_busy) return;
            ClearError();
            string start = stagingFile != null && Directory.Exists(Path.GetDirectoryName(stagingFile))
                               ? Path.GetDirectoryName(stagingFile)
                               : Path.GetFullPath(ExportFolder);
            string file  = Genie.WindowUtils.PickFile(this, "Import into " + Genie.PathUtils.GetLeafName(pathName),
                                                      "Scaleform movies (*.swf, *.gfx)", "*.swf;*.gfx", start);
            if (file == null) return;
            byte[] head = new byte[3];
            try {
                using (FileStream f = File.OpenRead(file)) f.Read(head, 0, 3);
            } catch (Exception ex) {
                ShowError("Couldn’t read " + Path.GetFileName(file) + ".", ex.Message);
                return;
            }
            if (AddonUtils.GetFormat(head) == "Unknown") { // GetFormat names the format, not the signature
                ShowError("Couldn’t import " + Path.GetFileName(file) + ".", "It isn’t an SWF or GFX movie.");
                return;
            }
            List<ImportPlan> plan = new List<ImportPlan>();
            Add(plan, movie, pathName, file, false, true);
            Import(plan);
        }

        private void Import(List<ImportPlan> plan) {
            if (_busy || plan.Count == 0) return;
            ClearError();

            bool            single  = plan.Count == 1;
            bool            allSame = plan.TrueForAll(p => p.Same);
            List<UIElement> items   = new List<UIElement>();
            foreach (ImportPlan p in plan) {
                List<UIElement> extras = new List<UIElement>();
                if (!p.Same && !p.Chosen) extras.Add(EditedTag());
                extras.Add(Genie.ControlUtils.CreateTag(AddonUtils.GetSizeText(p.NewData.Length),
                                                        Genie.ControlUtils.TagTone.Info));
                string oldFormat = AddonUtils.GetFormat(p.OldData), newFormat = AddonUtils.GetFormat(p.NewData);
                if (oldFormat != newFormat)
                    extras.Add(Genie.ControlUtils.CreateTag(oldFormat + " \u2192 " + newFormat,
                                                            Genie.ControlUtils.TagTone.Warning));
                extras.Add(new TextBlock {
                    Text       = p.Same ? "same as the package"
                                        : AddonUtils.GetSizeChangeText(p.OldData.Length, p.NewData.Length),
                    Foreground = Genie.Theme.TextSecondary,
                    FontSize   = Genie.Theme.FontSizeBody,
                });
                extras.Add(Genie.ControlUtils.CreateHintIcon("Compared with " + Shown(p.File)));
                string name = p.Chosen ? p.File : single ? Genie.PathUtils.GetLeafName(p.PathName) : p.PathName;
                items.Add(Genie.ControlUtils.CreateDialogItem(name, extras.ToArray()));
            }
            string leaf     = Genie.PathUtils.GetLeafName(plan[0].PathName);
            string question = single ? (plan[0].Chosen ? "Import into " + leaf + "?" : "Import " + leaf + "?")
                                     : "Import " + plan.Count + " movies?";
            string note     = single ? plan[0].PathName + " will be modified. Save its UPK to keep the change."
                                     : plan.Count + " movies will be modified. Save their UPKs to keep the changes.";
            if (!Genie.ControlUtils.Confirm(this, question, "Import", note, items,
                                            allSame ? Genie.ControlUtils.TagTone.Info
                                                    : Genie.ControlUtils.TagTone.Warning))
                return;

            // Every file chosen is imported, whether or not it differs: the comparison is only a prediction.
            int done = 0, failed = 0, same = 0;
            foreach (ImportPlan p in plan) {
                if (AddonUtils.SetMovieData(p.Movie, p.NewData)) {
                    done++;
                    if (p.Same) same++;
                    Genie.Log("Imported " + p.File + " into " + p.PathName + " (" +
                              (p.Same ? "same as the package"
                                      : AddonUtils.GetSizeChangeText(p.OldData.Length, p.NewData.Length)
                                            .Replace("\u2192", "->")) +
                              ").");
                } else {
                    failed++;
                    Genie.Log("Couldn't import " + p.File + " into " + p.PathName + ".");
                }
            }
            if (done > 0)
                _status.ShowDone("Imported " + Genie.TextUtils.Count(done, "movie") +
                                 (same > 0 ? " (" + same + " identical to the package)" : ""));
            if (failed > 0) ShowError(Genie.TextUtils.Count(failed, "import") + " failed.", "See the log.");
            UpdateStatus();
        }

        // A file as shown to the user: under the export folder as <Exports>\..., anywhere else in full.
        private static string Shown(string file) {
            string relative = ExportRelative(file);
            return relative == file ? file : "<Exports>\\" + relative;
        }

        // == Export ===================================================================================

        private static string ExportFolder {
            get { return Genie.ConfigUtils.Get(FolderKey, Path.Combine(Genie.GetDataFolder(), "Exports")); }
        }

        private void ChangeFolder() {
            string picked = Genie.WindowUtils.PickFolder(this, "Export movies to", ExportFolder);
            if (picked != null) SetFolder(picked);
        }

        // A typed folder must be a full path ("C:\..." or "\\pc\share\..."). Anything else ("Exports", "\Exports",
        // "C:Exports") would resolve against the editor's current folder or drive.
        private void CommitFolder() {
            string typed = _folderBox.Text.Trim();
            if (string.Equals(typed, ExportFolder, StringComparison.OrdinalIgnoreCase)) return;
            string full = null;
            try {
                if (Path.IsPathRooted(typed) && Path.GetPathRoot(typed).Length > 2) full = Path.GetFullPath(typed);
            } catch (Exception) {} // characters a path can't hold
            if (full == null) {
                _folderBox.Text = ExportFolder;
                ShowError("Couldn’t use that folder.", "Type a full path, such as C:\\Mods\\SWFExports.");
                return;
            }
            SetFolder(full);
        }

        private void SetFolder(string folder) {
            Genie.ConfigUtils.Set(FolderKey, folder);
            _folderBox.Text = folder;
            WatchExportFolder();
            UpdateStatus();
        }

        private void OpenFolder() {
            if (!Genie.FileUtils.OpenFolder(ExportFolder, true)) ShowError("Couldn’t open the folder.", "See the log.");
        }

        // == Errors ===================================================================================
        // Shown under the save note, or under the empty state when no movie is selected, until dismissed or the
        // next import or export starts.

        private void ShowError(string lead, string text) {
            _errorLead = lead;
            _errorText = text;
            RenderError();
        }

        private void ClearError() {
            ShowError(null, null);
        }

        // A note can have only one parent, so each place gets its own.
        private void RenderError() {
            bool any                 = _errorLead != null || _errorText != null;
            _detailsError.Content    = any ? ErrorNote() : null;
            _emptyError.Content      = any ? ErrorNote() : null;
            _detailsError.Visibility = any ? Visibility.Visible : Visibility.Collapsed; // empty hosts keep no margin
            _emptyError.Visibility   = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private UIElement ErrorNote() {
            return Genie.ControlUtils.CreateNote(Genie.ControlUtils.TagTone.Danger, _errorLead, _errorText, ClearError);
        }

        // One file to write: a movie, and where it goes.
        private sealed class ExportEntry {
            public IntPtr Movie;
            public string PathName;
            public string File;
        }

        private sealed class ImageEntry {
            public IntPtr Texture;
            public string PathName; // gfxAlerts.skull
            public string File;     // ...\gfxAlerts\skull.tga, the name the movie gives it
            public string Current;  // for a file that already exists: the package's image, exported to a temp file
            public bool   Edited;   // that file differs from the package's image
            public ExportEntry Owner; // the movie that names it first
        }

        // What SWF Porter last wrote to each image file in an export folder: its size and SHA-1, kept in
        // SWFPorter.checksums there. An image that no longer matches was edited on disk, however small the edit.
        // (Movies need no record: the staging file is compared with the movie in the package directly.)
        private sealed class ExportRecord {
            public const string     FileName = "SWFPorter.checksums";
            private readonly string _folder, _path;
            private readonly Dictionary<string, string> _entries =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public ExportRecord(string folder) {
                _folder = folder;
                _path   = Path.Combine(folder, FileName);
                try {
                    string[] lines = File.Exists(_path) ? File.ReadAllLines(_path) : new string[0];
                    foreach (string line in lines) {
                        string[] fields = line.Split('\t'); // size, sha1, file relative to the folder
                        if (fields.Length == 3) _entries[fields[2]] = fields[0] + ":" + fields[1];
                    }
                } catch (Exception ex) {
                    Genie.Log("Couldn't read " + _path + ": " + ex.Message);
                }
            }

            private string Key(string file) {
                return Genie.FileUtils.GetRelativePath(file, _folder) ?? file;
            }

            /// <summary>True: matches what was written. False: edited on disk. Null: no record of this file.</summary>
            public bool? Matches(string file) {
                string want;
                if (!_entries.TryGetValue(Key(file), out want)) return null;
                long size = new FileInfo(file).Length;
                if (!want.StartsWith(size + ":")) return false; // a different size needs no checksum
                return want == Stamp(file);
            }

            public void Remember(string file) {
                try {
                    _entries[Key(file)] = Stamp(file);
                } catch (Exception ex) {
                    Genie.Log("Couldn't checksum " + file + ": " + ex.Message);
                }
            }

            public void Save() {
                try {
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, string> e in _entries) {
                        int colon = e.Value.IndexOf(':');
                        lines.Add(e.Value.Substring(0, colon) + "\t" + e.Value.Substring(colon + 1) + "\t" + e.Key);
                    }
                    lines.Sort(StringComparer.OrdinalIgnoreCase);
                    Directory.CreateDirectory(_folder);
                    File.WriteAllLines(_path, lines.ToArray());
                } catch (Exception ex) {
                    Genie.Log("Couldn't write " + _path + ": " + ex.Message);
                }
            }

            // "size:sha1"
            private static string Stamp(string file) {
                using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create()) {
                    using (FileStream stream = File.OpenRead(file)) {
                        string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                        return stream.Length + ":" + hash.ToLowerInvariant();
                    }
                }
            }
        }

        // withImports: also the movies they import, and theirs. Without, each movie is written alone, with its images.
        private void Export(List<MovieItem> items, bool withImports = true) {
            if (items.Count == 0 || _busy) return;
            List<MovieItem> unloaded = items.FindAll(m => m.Movie == IntPtr.Zero);
            if (unloaded.Count > 0) {
                LoadThenExport(items, unloaded, withImports);
                return;
            }
            ClearError();
            string folder = Path.GetFullPath(ExportFolder);

            // The chosen movies at Package\Movie.swf, then each movie they import at the URL the import names,
            // relative to the importing file (that's where JPEXS looks), and so on down the chain. Keyed by file.
            List<ExportEntry> entries      = new List<ExportEntry>();
            HashSet<string>   plannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string>      missing      = new List<string>();
            foreach (MovieItem m in items) {
                string file = m.File ?? AddonUtils.GetStagingFile(folder, m.PathName);
                if (plannedFiles.Add(file))
                    entries.Add(new ExportEntry { Movie = m.Movie, PathName = m.PathName, File = file });
            }
            for (int i = 0; withImports && i < entries.Count && entries.Count < 500; i++) {
                byte[] data = AddonUtils.GetMovieData(entries[i].Movie);
                if (data == null) continue;
                foreach (string url in AddonUtils.GetImportUrls(data)) {
                    string file =
                        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entries[i].File), url.Replace('/', '\\')));
                    string path;
                    IntPtr imported = ResolveImport(url, out path);
                    if (imported == IntPtr.Zero) {
                        if (!missing.Contains(url)) missing.Add(url);
                        Genie.Log(entries[i].PathName + " imports " + url +
                                  ", but no movie by that name could be found or loaded.");
                        continue;
                    }
                    if (!Genie.FileUtils.IsUnder(file, folder)) {
                        Genie.Log(entries[i].PathName + " imports " + url +
                                  ", which points outside the export folder; writing " + path +
                                  " at its package path instead.");
                        file = AddonUtils.GetStagingFile(folder, path);
                    }
                    if (plannedFiles.Add(file))
                        entries.Add(new ExportEntry { Movie = imported, PathName = path, File = file });
                }
            }

            // Each movie's images, next to its file under the names the movie gives them (where JPEXS looks).
            List<ImageEntry> images        = new List<ImageEntry>();
            List<string>     missingImages = new List<string>();
            foreach (ExportEntry e in entries) {
                byte[] data = AddonUtils.GetMovieData(e.Movie);
                if (data == null) continue;
                string outer = Genie.PathUtils.GetOuterName(e.PathName) ?? e.PathName; // its package (and group)
                foreach (string name in AddonUtils.GetExternalImages(data)) {
                    string file =
                        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(e.File), name.Replace('/', '\\')));
                    if (!Genie.FileUtils.IsUnder(file, folder)) {
                        Genie.Log(e.PathName + " names image " + name + ", outside the export folder; skipped.");
                        continue;
                    }
                    if (!plannedFiles.Add(file)) continue;
                    string path    = outer + "." + Path.GetFileNameWithoutExtension(name);
                    IntPtr texture = Genie.ObjectUtils.FindObject(path);
                    if (texture == IntPtr.Zero) texture = Genie.ObjectUtils.LoadObject("Engine.Texture2D", path);
                    if (texture == IntPtr.Zero) {
                        missingImages.Add(path);
                        Genie.Log(e.PathName + " uses image " + name + ", but " + path +
                                  " couldn't be found or loaded.");
                        continue;
                    }
                    images.Add(new ImageEntry { Texture = texture, PathName = path, File = file, Owner = e });
                }
            }

            ExportJob job = new ExportJob {
                Folder        = folder,
                Chosen        = items.Count,
                Entries       = entries,
                Images        = images,
                Missing       = missing,
                MissingImages = missingImages,
                Record        = new ExportRecord(folder),
            };
            List<ImageEntry> existingImages = images.FindAll(im => File.Exists(im.File));
            SetBusy(true);
            // Which images already on disk were edited there, so none is overwritten unasked: checked against the
            // record of what was written. A file with no record yet (exported before the record existed) is
            // compared once with the package's image instead, and recorded if it matches.
            Action<int> progress = delegate(int n) {
                _status.ShowBusy("Checking existing files… " + n + " of " + existingImages.Count);
            };
            Genie.WorkUtils.RunInSteps(
                existingImages.Count,
                delegate(int i) {
                    ImageEntry im = existingImages[i];
                    bool? matches = job.Record.Matches(im.File);
                    if (matches.HasValue) {
                        im.Edited = !matches.Value;
                        return;
                    }
                    string temp = Path.Combine(Path.GetTempPath(), "SWFPorter-" + Guid.NewGuid().ToString("N") +
                                                                       Path.GetExtension(im.File));
                    if (Genie.ObjectUtils.ExportToFile(im.Texture, temp)) im.Current = temp;
                    im.Edited = im.Current == null || !Genie.FileUtils.IsSameContent(im.File, im.Current);
                    if (!im.Edited) job.Record.Remember(im.File);
                },
                progress, delegate { ConfirmAndWrite(job); });
        }

        // Export reads movies from memory, so those not loaded are loaded first, a few per slice so the window keeps
        // drawing. Any that fail to load are left out.
        private void LoadThenExport(List<MovieItem> items, List<MovieItem> unloaded, bool withImports) {
            ClearError();
            SetBusy(true);
            Genie.WorkUtils.RunInSteps(
                unloaded.Count,
                delegate(int i) {
                    unloaded[i].Movie = Genie.ObjectUtils.LoadObject(AddonUtils.MovieClass, unloaded[i].PathName);
                },
                delegate(int n) { _status.ShowBusy("Loading movies… " + n + " of " + unloaded.Count); },
                delegate {
                    SetBusy(false);
                    List<MovieItem> loaded = items.FindAll(m => m.Movie != IntPtr.Zero);
                    int             failed = items.Count - loaded.Count;
                    if (loaded.Count == 0) _status.Hide();
                    Export(loaded, withImports);
                    if (failed == 0) return;
                    string what = Genie.TextUtils.Count(failed, "movie") + " couldn’t be loaded";
                    ShowError(what + " and won’t be exported.", "See the log.");
                });
        }

        private sealed class ExportJob {
            public string            Folder;
            public int               Chosen, Done, ImagesDone, Failed;
            public List<ExportEntry> Entries;
            public List<ImageEntry>  Images;
            public List<string>      Missing, MissingImages;
            public ExportRecord      Record;
        }

        // Before replacing files: the export folder as a tree of the SWFs with files already there, each with its image
        // count (green: some of its images were edited on disk) and save icon (green: the SWF was).
        private void ConfirmAndWrite(ExportJob job) {
            int          existing = 0, editedMovies = 0, editedImages = 0;
            GenieTreeBox tree     = Genie.ControlUtils.CreateTreeBox("Export folder");
            tree.CaptionExtra     = Genie.ControlUtils.CreateFindIcon(OpenFolder,
                                                                  "Open in File Explorer\n" + job.Folder);
            tree.SetRoot(Path.GetFileName(job.Folder.TrimEnd('\\')) + "\\", Genie.ControlUtils.CreateFolderIcon());
            foreach (ExportEntry e in job.Entries) {
                List<ImageEntry> images       = job.Images.FindAll(im => im.Owner == e);
                int              imagesThere  = images.FindAll(im => File.Exists(im.File)).Count;
                int              imagesEdited = images.FindAll(im => im.Edited).Count; // checked only for files there
                bool             movieThere   = File.Exists(e.File);
                if (!movieThere && imagesThere == 0) continue;
                bool movieEdited  = movieThere && IsEdited(e.File, AddonUtils.GetMovieData(e.Movie));
                existing         += (movieThere ? 1 : 0) + imagesThere;
                editedImages     += imagesEdited;
                if (movieEdited) editedMovies++;
                SaveState state = !movieThere ? SaveState.NotExported
                                  : movieEdited ? SaveState.EditedOnDisk
                                                : SaveState.Exported;
                string relative = ExportRelative(e.File);
                // image count, then save: the main window's order
                StackPanel after = Genie.ControlUtils.CreateButtonRow(ImageCountTag(images.Count, imagesEdited),
                                                                      SaveIcon(state, 14));
                tree.AddChild(Path.GetFileName(relative), tag: after, lead: Path.GetDirectoryName(relative) + "\\",
                              showIcon: false);
            }
            if (existing > 0) {
                int    edited = editedMovies + editedImages;
                string note   = "All match their UPKs, so replacing loses nothing.";
                if (edited > 0) {
                    string what = editedMovies == 0   ? Genie.TextUtils.Count(editedImages, "image")
                                  : editedImages == 0 ? Genie.TextUtils.Count(editedMovies, "SWF")
                                                      : Genie.TextUtils.Count(editedMovies, "SWF") + " and " +
                                                            Genie.TextUtils.Count(editedImages, "image");
                    note = "The green files were edited on disk: " + what + ". Replacing overwrites " +
                           (edited == 1 ? "that edit" : "those edits") + " in the export folder.";
                }
                _status.Hide();
                if (!Genie.ControlUtils.Confirm(
                        this, "Replace " + Genie.TextUtils.Count(existing, "existing file") + "?", "Replace", note,
                        new UIElement[] { tree },
                        edited > 0 ? Genie.ControlUtils.TagTone.Danger : Genie.ControlUtils.TagTone.Warning)) {
                    Finish(job, false);
                    return;
                }
            }

            int total = job.Entries.Count + job.Images.Count;
            _status.ShowBusy("Exporting… 0 of " + total + " files");
            Genie.WorkUtils.RunInSteps(
                total,
                delegate(int i) {
                    if (i < job.Entries.Count)
                        WriteMovie(job, job.Entries[i]);
                    else
                        WriteImage(job, job.Images[i - job.Entries.Count]);
                },
                delegate(int n) { _status.ShowBusy("Exporting… " + n + " of " + total + " files"); },
                delegate { Finish(job, true); });
        }

        private static void WriteMovie(ExportJob job, ExportEntry e) {
            byte[] data = AddonUtils.GetMovieData(e.Movie); // read again: the package may have changed
            if (data == null) {
                job.Failed++;
                Genie.Log("Couldn't read " + e.PathName + ".");
                return;
            }
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(e.File));
                File.WriteAllBytes(e.File, data);
                job.Done++;
            } catch (Exception ex) {
                job.Failed++;
                Genie.Log("Couldn't write " + e.File + ": " + ex.Message);
            }
        }

        private static void WriteImage(ExportJob job, ImageEntry im) {
            bool ok = false;
            if (im.Current != null) { // already exported once, to compare: use that copy
                try {
                    File.Copy(im.Current, im.File, true);
                    ok = true;
                } catch (Exception ex) {
                    Genie.Log("Couldn't write " + im.File + ": " + ex.Message);
                }
            } else {
                ok = Genie.ObjectUtils.ExportToFile(im.Texture, im.File); // logs its own failure
            }
            if (!ok) {
                job.Failed++;
                return;
            }
            job.ImagesDone++;
            job.Record.Remember(im.File);
        }

        // The end of an export, written or cancelled: temp files go, the window comes back, the result shows.
        private void Finish(ExportJob job, bool written) {
            foreach (ImageEntry im in job.Images) {
                if (im.Current == null) continue;
                try {
                    File.Delete(im.Current);
                } catch {} // a leftover temp file is harmless
            }
            job.Record.Save(); // also after a cancel: files found unedited were recorded
            SetBusy(false);
            if (!written) {
                _status.Hide();
                return;
            }
            string done =
                "Exported " + Genie.TextUtils.Count(job.Done, "movie") +
                (job.ImagesDone > 0 ? " and " + Genie.TextUtils.Count(job.ImagesDone, "image") : "");
            _status.ShowDone(done);
            Genie.Log(done + " to " + job.Folder + ".");
            // Anything that went wrong stays until dismissed: a fading pill is easy to miss.
            List<string> problems = new List<string>();
            if (job.Missing.Count > 0) problems.Add("Couldn't find " + string.Join(", ", job.Missing.ToArray()) + ".");
            if (job.MissingImages.Count > 0)
                problems.Add(Genie.TextUtils.Count(job.MissingImages.Count, "image") + " not found.");
            if (job.Failed > 0) problems.Add(Genie.TextUtils.Count(job.Failed, "file") + " failed.");
            if (problems.Count > 0)
                ShowError("Export incomplete.", string.Join(" ", problems.ToArray()) + " See the log.");
            // The first export creates the folder. Not once closed: nothing would dispose the watch.
            if (_folderWatch == null && IsLoaded) WatchExportFolder();
            ShowDetails();
        }

        // While an export runs, nothing else may start: the header's buttons and the details are disabled.
        private void SetBusy(bool busy) {
            _busy = busy;
            if (busy) _wasEnabled = new[] { _exportAll.IsEnabled, _importAll.IsEnabled, _controls.IsRefreshEnabled };
            _exportAll.IsEnabled       = !busy && _wasEnabled[0];
            _importAll.IsEnabled       = !busy && _wasEnabled[1];
            _controls.IsRefreshEnabled = !busy && _wasEnabled[2];
            _detailsScroll.IsEnabled   = !busy;
            _emptyArea.IsEnabled       = !busy;
            _footer.IsEnabled          = !busy; // the job writes to the folder it started with
        }
    }
}
