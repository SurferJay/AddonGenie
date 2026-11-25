// Redirector Manager: an AddonGenie addon. Place in Binaries\Win64\Addons\
// Lists a package's redirectors, shows where each points, and adds, renames, repairs, retargets or deletes them.
// Two ways in: a window from the Addons menu, and a button in the Content Browser's Packages tray that counts the
// selected package's redirectors.
// Editor access, how redirectors work and styling come from AddonGenie (Genie.*); what this addon allows and tracks
// is its own (AddonUtils).
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AddonGenie;

namespace RedirectorManager {
    public sealed class Addon : IGenieAddon {
        private GenieWindowSlot _window;

        public string Name {
            get { return "Redirector Manager"; }
        }
        public string Tooltip {
            get { return "Manage the selected package's redirectors"; }
        }
        public string Version {
            get { return "1.0"; }
        }
        public string Author {
            get { return "SurferJay"; }
        }

        public void Initialize() {
            _window = new GenieWindowSlot(CreateWindow);
            Shortcut.Create(_window.Show);
        }

        public void Invoke() {
            _window.Show();
        }

        private GenieWindow CreateWindow() {
            // Wide enough for the multi-selection's tables beside the list.
            GenieWindow w = Genie.WindowUtils.CreateWindow("Redirector Manager", 720, 400);
            w.MinWidth    = 380;
            w.MinHeight   = 220;
            w.Content     = new RedirectorPanel(w);
            return w;
        }
    }

    // == Redirector Logic =============================================================================
    // A redirector is a forwarding address left behind at a moved or renamed object's old location. How they work
    // on the host is Genie.RedirectorUtils; what this addon allows, and remembers between windows, is here.

    internal static class AddonUtils {
        /// <summary>Where a redirector points: its object, or what its package recorded. See GetDestination.</summary>
        public struct Destination {
            public string PackageName;      // outermost package ("bin")
            public string ObjectName;       // the object's own name ("AngelHead_MAT")
            public string PathName;         // full path, groups included ("bin.AngelHead_MAT")
            public string ClassName;        // "Material", when known
            public string ClassPackage;     // "Engine", when known
            public bool   IsResolved;       // the names above were determined
            public string UnresolvedReason; // why not, when IsResolved is false
            public bool   IsObjectLoaded;   // Object is valid
            public bool   IsPackageLoaded;  // loaded from its file, not just a placeholder
            public IntPtr Object;           // the live object, or Zero
            public IntPtr Package;          // UPackage*, or Zero; may be a placeholder (see IsPackageLoaded)
        }

        // What this addon did to a redirector since its package was last saved, by path name: changes the editor
        // drops if it closes first, shown as its note (GetUnsavedNote). Here, not in the window, so it outlives it.
        private sealed class Changes {
            public bool   Created, Repaired, Retargeted;
            public string RenamedFrom; // its name at the last save
        }

        private static readonly Dictionary<string, Changes> _unsaved =
            new Dictionary<string, Changes>(StringComparer.OrdinalIgnoreCase);

        // The record for a redirector, made on first use. Null if its name can't be read.
        private static Changes Record(IntPtr redirector) {
            string path = Genie.ObjectUtils.GetPathName(redirector);
            if (path == null) return null;
            Changes c;
            if (!_unsaved.TryGetValue(path, out c)) {
                c              = new Changes();
                _unsaved[path] = c;
            }
            return c;
        }

        /// <summary>
        /// What this addon changed in the redirector since its package was saved, as its note: "Repaired (unsaved).",
        /// "Renamed from Old, retargeted (unsaved).". Null for none. A clean package means it was saved, or loaded
        /// again from its file: either way the changes are settled, and forgotten here.
        /// </summary>
        public static string GetUnsavedNote(IntPtr redirector) {
            string  path = Genie.ObjectUtils.GetPathName(redirector);
            Changes c;
            if (path == null || !_unsaved.TryGetValue(path, out c)) return null;
            if (!Genie.ObjectUtils.IsPackageDirty(redirector)) {
                _unsaved.Remove(path);
                return null;
            }
            List<string> done = new List<string>();
            if (c.Created) done.Add("created");
            if (c.RenamedFrom != null) done.Add("renamed from " + c.RenamedFrom);
            if (c.Retargeted) done.Add(c.Repaired ? "retargeted and repaired" : "retargeted");
            else if (c.Repaired) done.Add("repaired");
            if (done.Count == 0) return null;
            string note = string.Join(", ", done.ToArray()) + " (unsaved).";
            return char.ToUpperInvariant(note[0]) + note.Substring(1);
        }

        /// <summary>
        /// Why a redirector (Zero: one not created yet) can't point at destination, or null. Only what can never
        /// work (Genie.RedirectorUtils): itself, another redirector, and a transient object.
        /// </summary>
        public static string CheckDestination(IntPtr redirector, IntPtr destination) {
            if (destination == IntPtr.Zero) return "no object given";
            if (destination == redirector) return "a redirector can't point at itself";
            string path = Genie.ObjectUtils.GetPathName(destination);
            if (path == null || Genie.ObjectUtils.GetClassPathName(destination) == null)
                return "couldn't read the object";
            if (Genie.RedirectorUtils.IsRedirector(destination)) return path + " is itself a redirector";
            if (Genie.ObjectUtils.IsTransient(destination)) return path + " is temporary and can't be saved";
            return null;
        }

        /// <summary>
        /// Creates a redirector named name in outerPath ("Pkg", or "Pkg.Group"), pointing at destination. Marks the
        /// package dirty. Zero if not, with message saying why.
        /// </summary>
        public static IntPtr Create(string outerPath, string name, IntPtr destination, out string message) {
            message = Genie.ObjectUtils.CheckNewName(outerPath, name) ?? CheckDestination(IntPtr.Zero, destination);
            if (message != null) return IntPtr.Zero;
            IntPtr outer = FindOuter(outerPath, out message);
            if (outer == IntPtr.Zero) return IntPtr.Zero;
            IntPtr r = Genie.RedirectorUtils.Create(outer, name, destination);
            if (r == IntPtr.Zero) {
                message = "the editor didn’t create it. See the log";
                return IntPtr.Zero;
            }
            Genie.Log("Created " + Genie.ObjectUtils.GetPathName(r) + " -> " +
                      Genie.ObjectUtils.GetPathName(destination) + ".");
            Changes c = Record(r);
            if (c != null) c.Created = true;
            return r;
        }

        /// <summary>
        /// Renames a redirector: a new one at newName beside it, pointing where it points, then the old one deleted.
        /// Not UObject::Rename, which would leave a redirector at the old name. Packages using the old address lose it
        /// once saved, as with Delete. A broken one points nowhere afterwards: where it pointed was only on record in
        /// its file. Returns the new one (the same one for no change, or one in case only), or Zero with message
        /// saying why. message is also set if the old one couldn't be deleted.
        /// </summary>
        public static IntPtr Rename(IntPtr redirector, string newName, out string message) {
            message     = null;
            string path = Genie.ObjectUtils.GetPathName(redirector);
            if (path == null) {
                message = "couldn't read the redirector";
                return IntPtr.Zero;
            }
            // Names ignore case, so "foo" over "Foo" would land on itself.
            if (string.Equals(Genie.PathUtils.GetLeafName(path), newName, StringComparison.OrdinalIgnoreCase))
                return redirector;
            string outerPath = Genie.PathUtils.GetOuterName(path);
            message          = Genie.ObjectUtils.CheckNewName(outerPath, newName, redirector);
            if (message != null) return IntPtr.Zero;
            IntPtr outer = FindOuter(outerPath, out message);
            if (outer == IntPtr.Zero) return IntPtr.Zero;

            IntPtr r = Genie.RedirectorUtils.Create(outer, newName, Genie.RedirectorUtils.GetDestination(redirector));
            if (r == IntPtr.Zero) {
                message = "the editor didn’t create the new one. See the log";
                return IntPtr.Zero;
            }
            Genie.Log("Renamed " + path + " to " + Genie.ObjectUtils.GetPathName(r) +
                      " (a new redirector, the old one deleted).");
            // The new one carries the old one's changes. A created one stays "created"; one renamed back is unrenamed.
            Changes c;
            if (!_unsaved.TryGetValue(path, out c)) c = new Changes();
            string newPath = Genie.ObjectUtils.GetPathName(r);
            if (!c.Created) c.RenamedFrom = c.RenamedFrom ?? Genie.PathUtils.GetLeafName(path);
            if (string.Equals(c.RenamedFrom, newName, StringComparison.OrdinalIgnoreCase)) c.RenamedFrom = null;
            if (newPath != null) _unsaved[newPath] = c;
            if (Delete(redirector))
                _unsaved.Remove(path);
            else
                message = "the old one couldn’t be deleted, so both exist";
            return r;
        }

        // The loaded package or group at outerPath, or Zero with message saying why.
        private static IntPtr FindOuter(string outerPath, out string message) {
            message      = null;
            IntPtr outer = outerPath.Contains(".") ? Genie.ObjectUtils.FindObject(outerPath)
                                                   : Genie.ObjectUtils.FindPackage(outerPath);
            if (outer != IntPtr.Zero && Genie.ObjectUtils.IsPackageLoaded(Genie.ObjectUtils.GetOutermost(outer)))
                return outer;
            message = outerPath + " isn’t loaded";
            return IntPtr.Zero;
        }

        /// <summary>
        /// Where a redirector points: a working one's object, or a broken one's as its package recorded it
        /// (Genie.RedirectorUtils.GetRecordedDestination), with what of it is loaded now.
        /// </summary>
        public static Destination GetDestination(IntPtr redirector) {
            Destination d = new Destination();
            if (redirector == IntPtr.Zero) return Unresolved(d, "no redirector");

            IntPtr dest = Genie.RedirectorUtils.GetDestination(redirector);
            if (dest != IntPtr.Zero) {
                d.PathName    = Genie.ObjectUtils.GetPathName(dest);
                d.PackageName = Genie.PathUtils.GetPackageName(d.PathName);
                d.ObjectName  = Genie.PathUtils.GetLeafName(d.PathName);
                SetClass(ref d, Genie.ObjectUtils.GetClassPathName(dest));
                d.Object          = dest;
                d.IsObjectLoaded  = true;
                d.Package         = Genie.ObjectUtils.GetOutermost(dest);
                d.IsPackageLoaded = Genie.ObjectUtils.IsPackageLoaded(d.Package);
                if (d.PathName == null) return Unresolved(d, "couldn't read the destination's name");
                d.IsResolved = true;
                return d;
            }

            return FromRecorded(Genie.RedirectorUtils.GetRecordedDestination(redirector));
        }

        /// <summary>Where a redirector that isn't loaded points, as its package recorded it.</summary>
        public static Destination GetUnloadedDestination(string pathName, int exportIndex) {
            IntPtr linker = Genie.LinkerUtils.GetLinker(Genie.PathUtils.GetPackageName(pathName));
            return FromRecorded(Genie.RedirectorUtils.GetRecordedDestination(linker, exportIndex));
        }

        // A recorded destination, with what of it is loaded now.
        private static Destination FromRecorded(Genie.RedirectorUtils.RecordedDestination recorded) {
            Destination d = new Destination();
            if (recorded.PathName == null) return Unresolved(d, recorded.Problem);
            d.PathName        = recorded.PathName;
            d.PackageName     = recorded.PackageName;
            d.ObjectName      = recorded.ObjectName;
            d.ClassName       = recorded.ClassName;
            d.ClassPackage    = recorded.ClassPackage;
            d.IsResolved      = true;
            d.Object          = Genie.ObjectUtils.FindObject(d.PathName); // never the import's possibly stale pointer
            d.IsObjectLoaded  = d.Object != IntPtr.Zero;
            d.Package         = Genie.ObjectUtils.FindPackage(d.PackageName);
            d.IsPackageLoaded = Genie.ObjectUtils.IsPackageLoaded(d.Package);
            // A destination in its own package records its class name only; the loaded object gives the package.
            if (d.ClassPackage == null && d.IsObjectLoaded)
                SetClass(ref d, Genie.ObjectUtils.GetClassPathName(d.Object));
            return d;
        }

        /// <summary>
        /// Reconnect a broken redirector whose destination has since been loaded. Loading a package doesn't revisit
        /// objects already loaded, so the null reference stays until set here. The loaded object's class must match
        /// the one the package recorded, as the engine checks when resolving an import. Marks the package dirty:
        /// saving it keeps the fix, while saving it broken writes the reference as null.
        /// </summary>
        public static bool Repair(IntPtr redirector, out string message) {
            message = null;
            if (redirector == IntPtr.Zero) return Fail("no redirector", out message);
            if (!Genie.RedirectorUtils.IsBroken(redirector)) {
                message = "it isn't broken";
                return true;
            }

            Destination d = GetDestination(redirector);
            if (!d.IsResolved) return Fail("its destination is unknown (" + d.UnresolvedReason + ")", out message);
            if (!d.IsObjectLoaded) return Fail(d.PathName + " isn't loaded", out message);

            string actual   = Genie.ObjectUtils.GetClassPathName(d.Object);
            string expected = ExpectedClass(d);
            if (expected == null || actual == null ||
                !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
                return Fail("the package expects a " + (expected ?? "?") + ", but the loaded object is a " +
                                (actual ?? "?"),
                            out message);
            }
            if (!WriteDestination(redirector, d.Object, "Repaired", out message)) return false;
            Changes c = Record(redirector);
            if (c != null) c.Repaired = true;
            return true;
        }

        /// <summary>
        /// Point a redirector at another object. Packages that still use its old address get this object when they
        /// load, if its class is the one they expect (see IsClassMismatch). Refuses what can never work
        /// (CheckDestination). Marks the package dirty.
        /// </summary>
        public static bool Retarget(IntPtr redirector, IntPtr newDestination, out string message) {
            message = null;
            if (redirector == IntPtr.Zero) return Fail("no redirector", out message);
            string refused = CheckDestination(redirector, newDestination);
            if (refused != null) return Fail(refused, out message);
            string targetPath = Genie.ObjectUtils.GetPathName(newDestination);

            if (Genie.RedirectorUtils.GetDestination(redirector) == newDestination) {
                message = "it already points to " + targetPath;
                return true;
            }
            bool wasBroken = Genie.RedirectorUtils.IsBroken(redirector);
            if (!WriteDestination(redirector, newDestination, "Retargeted", out message)) return false;
            Changes c = Record(redirector);
            if (c != null) {
                c.Retargeted = true;
                c.Repaired   = c.Repaired || wasBroken; // a broken one pointed somewhere again is repaired too
            }
            return true;
        }

        /// <summary>
        /// True if newDestination's class differs from the class the redirector points at now, as its destination
        /// details show it: expected and actual are their names ("Material"). False while it points nowhere known.
        /// Allowed, but packages using it then can't: they take a destination only of the class they ask for
        /// (Genie.RedirectorUtils), so the reference loads empty.
        /// </summary>
        public static bool IsClassMismatch(IntPtr redirector, IntPtr newDestination, out string expected,
                                           out string actual) {
            string expectedPath = ExpectedClass(GetDestination(redirector));
            string actualPath   = Genie.ObjectUtils.GetClassPathName(newDestination);
            expected            = Genie.PathUtils.GetLeafName(expectedPath);
            actual              = Genie.PathUtils.GetLeafName(actualPath);
            return expectedPath != null && actualPath != null &&
                   !string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool WriteDestination(IntPtr redirector, IntPtr target, string verb, out string message) {
            if (!Genie.RedirectorUtils.SetDestination(redirector, target))
                return Fail("the change didn't take", out message);
            string path = Genie.ObjectUtils.GetPathName(target);
            message     = "now points to " + path;
            Genie.Log(verb + " " + Genie.ObjectUtils.GetPathName(redirector) + " -> " + path + ".");
            return true;
        }

        /// <summary>Deletes a redirector, logging the outcome. False if the editor didn't delete it.</summary>
        public static bool Delete(IntPtr redirector) {
            string path = Genie.ObjectUtils.GetPathName(redirector);
            bool   ok   = Genie.ObjectUtils.ForceDelete(redirector) > 0;
            Genie.Log((ok ? "Deleted " : "Couldn't delete ") + (path ?? "the redirector") + ".");
            if (ok && path != null) _unsaved.Remove(path);
            return ok;
        }

        /// <summary>
        /// Deletes several redirectors in one engine call (Genie.ObjectUtils.ForceDelete takes a list), so the editor
        /// sweeps for references once, not once each. Logs what was deleted, and what wasn't. Returns how many were.
        /// </summary>
        public static int Delete(List<IntPtr> redirectors) {
            List<string> paths = new List<string>();
            foreach (IntPtr r in redirectors) paths.Add(Genie.ObjectUtils.GetPathName(r));
            int          count = Genie.ObjectUtils.ForceDelete(redirectors);
            List<string> gone  = new List<string>(), kept = new List<string>();
            for (int i = 0; i < paths.Count; i++) {
                string path = paths[i] ?? "a redirector";
                if (count > 0 && (count >= redirectors.Count || Genie.ObjectUtils.IsDeleted(paths[i])))
                    gone.Add(path);
                else
                    kept.Add(path);
            }
            foreach (string path in gone) _unsaved.Remove(path);
            if (gone.Count > 0)
                Genie.Log("Deleted " + Genie.TextUtils.Count(gone.Count, "redirector") + ": " +
                          string.Join(", ", gone.ToArray()) + ".");
            if (kept.Count > 0) Genie.Log("Couldn't delete " + string.Join(", ", kept.ToArray()) + ".");
            return Math.Max(count, 0);
        }

        private static Destination Unresolved(Destination d, string reason) {
            d.UnresolvedReason = reason;
            return d;
        }

        private static bool Fail(string reason, out string message) {
            message = reason;
            return false;
        }

        /// <summary>The destination's class as its package recorded it ("Engine.Material"), or null.</summary>
        public static string ExpectedClass(Destination d) {
            return (d.ClassPackage != null && d.ClassName != null) ? d.ClassPackage + "." + d.ClassName : null;
        }

        private static void SetClass(ref Destination d, string classPathName) {
            d.ClassName    = Genie.PathUtils.GetLeafName(classPathName);
            d.ClassPackage = (classPathName == null) ? null : Genie.PathUtils.GetPackageName(classPathName);
        }
    }

    // == Window =======================================================================================

    internal sealed class RedirectorItem {
        public IntPtr    Redirector;      // Zero while pending, or while not loaded
        public ListBoxItem Row;           // its row in the list
        public string    Name;            // its own name; a pending one's as typed so far
        public string    PathName;        // a created one's path: all a redirector not loaded has, with ExportIndex
        public int       ExportIndex;     // in its package's export table, -1 if not in it
        public bool      IsPending;       // added with +, not created yet: it needs a name and a destination
        public string    Package;         // a pending one's package
        public string    DestinationPath; // a pending one's destination, by path: an object can unload meanwhile
        public TextBlock Label;           // its name in the list

        /// <summary>In its package's file but not in memory: only read, until the package is fully loaded.</summary>
        public bool IsUnloaded {
            get { return !IsPending && Redirector == IntPtr.Zero; }
        }
    }

    internal sealed class RedirectorPanel : UserControl {
        private const string Explainer = "A forwarding address left at a moved or renamed object’s old location, " +
                                         "so objects created before the move still find it.";
        private const string SelectHint    = "Select a redirector to see where it points.";
        private const string NoPackageHint = "Select a package in the Content Browser.";
        private const string Placeholder   = "(New Redirector)";
        private const string MismatchTip =
            "The Content Browser’s selected item does not match the destination class.";
        private const string UnloadedNote = "Not loaded. Fully load this UPK to change it.";
        private const string UnloadedTip  = "Not loaded: fully load this UPK to change it.";
        // Why a row's tick box is off: only redirectors that are loaded and created can be acted on together.
        private const string UnloadedCheckTip = "Not loaded: fully load this UPK to select it.";
        private const string PendingCheckTip  = "Not created yet: it needs a name and a destination.";
        private const string RedirectorClass  = "ObjectRedirector"; // as the Content Browser names the class
        private const int    DialogListCap    = 8; // names a delete prompt lists before "and N more"

        private const double DefaultSplit = 0.43; // the list's share of the width

        // Header and list pane. Repair all is gone: tick all, then Repair, does it.
        private readonly GenieCountPill _countPill, _brokenPill, _listCountPill;
        private readonly Border         _add, _trash;
        private readonly GenieBanner    _saveBanner; // the save rule, while the package has broken redirectors
        private readonly Genie.ContentBrowserUtils.PackageFollower _follower;
        private int _brokenCount;

        // Added with + and not created yet, every package's: they stay while the window does.
        private readonly List<RedirectorItem> _pending = new List<RedirectorItem>();

        // List and details
        private readonly GenieList       _list;
        private readonly GenieEmptyState _emptyState;
        private readonly StackPanel       _details;
        private readonly TextBlock        _sourcePackageName, _destPackageName, _destObjectName;
        private readonly TextBox          _sourceName;
        private readonly GenieParagraph   _note;
        private readonly FrameworkElement _destPackageMissing, _destObjectMissing;
        private readonly GenieTag         _destClass;
        private readonly FrameworkElement _classRow;
        private readonly Border           _findSourcePackage, _useName;
        private readonly Border           _findPackage, _findObject, _openObject, _useSelected;
        private readonly Button           _repair, _delete;

        // Several selected: one table per destination package (BuildMulti), notes on what Repair can't fix, and the
        // buttons for them all.
        private readonly DockPanel  _multi;
        private readonly TextBlock  _multiTitle;
        private readonly StackPanel _multiBody, _multiNotes;
        private readonly Button     _multiRepair, _multiDelete;
        private string              _multiMessage; // an action's result, kept until the selection changes

        private AddonUtils.Destination _dest;
        private bool   _noteIsMessage; // an action's result is showing; kept until the selection changes
        private string _message;       // that result, to show again after a rebuild
        private bool   _messageUnsaved;

        // The name box: whose name it shows, and whether it's been typed in since.
        private RedirectorItem _nameItem;
        private bool           _nameDirty, _settingName;

        // Content Browser notifications, no polling:
        //   followed package changes          -> rebuild the list (the follower minds the pin)
        //   followed package modified, saved,
        //   or unloaded                       -> rebuild, keeping the selection
        //   destination package loads/unloads -> update the details (enables Repair)
        private readonly Genie.ContentBrowserUtils.PackageWatch _destinationWatch;

        internal RedirectorPanel(GenieWindow window) {
            // First: the follower can report a package, and so select a redirector, before the constructor ends.
            _destinationWatch = Genie.ContentBrowserUtils.WatchPackage(this, null, OnDestinationChanged);
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header: the package and its pills; Repair all, then refresh, pin and the gear, on the right.
            GenieHeader header              = Genie.ControlUtils.CreatePanelHeader(window, "PACKAGE");
            header.Controls.RefreshToolTip  = "Reload the list.";
            header.Controls.Settings.AddTrayShortcutItem();
            header.Controls.Refresh        += delegate {
                CommitName(false);
                _follower.Update();
                Refresh();
            };
            _countPill = Genie.ControlUtils.CreateCountPill("");
            header.AddPill(_countPill);
            _brokenPill = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Danger, true);
            header.AddPill(_brokenPill);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Any save writes broken redirectors as nothing, so it's said once, here, for every one of them.
            _saveBanner            = Genie.ControlUtils.CreateBanner(Genie.ControlUtils.TagTone.Warning);
            _saveBanner.Visibility = Visibility.Collapsed;
            Grid.SetRow(_saveBanner, 1);
            root.Children.Add(_saveBanner);

            _follower                  = Genie.ContentBrowserUtils.FollowPackage(header);
            _follower.PackageChanged  += delegate {
                CommitName(false);
                Refresh();
            };
            _follower.PackageModified += OnShownPackageChanged;

            // The list, boxed like the tables beside it, with tick boxes, and the trash and + in its header. The
            // selection is what's ticked: a click on a row selects just it.
            GenieListPane left = Genie.ControlUtils.CreateListPane("Redirectors", true);
            left.Margin        = new Thickness(10, 8, 0, 10);
            left.EnableChecks();
            _trash = Genie.ControlUtils.CreateDeleteIcon(delegate {
                OnTrash();
            }, "Delete the selected redirectors");
            left.AddAction(_trash);
            UpdateTrash(new List<RedirectorItem>()); // nothing selected yet
            _add = Genie.ControlUtils.CreateAddIcon(delegate {
                OnAdd();
            }, "Add a redirector, then name it and give it a destination");
            left.AddAction(_add);
            _list                   = left.List;
            _listCountPill          = left.CountPill;
            _list.SelectionChanged += delegate {
                OnSelect();
            };

            Grid right  = new Grid { Margin = Genie.Theme.PaneInset };
            _emptyState = Genie.ControlUtils.CreateEmptyState("What’s a redirector?", Explainer, NoPackageHint);
            _emptyState.AddPoint(Genie.ControlUtils.TagTone.Danger,
                                 "ones lost their destination when their UPK loaded. Saving the UPK deletes them, so " +
                                     "repair or retarget them first.",
                                 "Broken");
            _emptyState.AddPoint(Genie.ControlUtils.TagTone.Neutral,
                                 "Saving a UPK with no changes writes nothing, so broken ones seem to survive it. The " +
                                     "next real save deletes them.");
            right.Children.Add(_emptyState);

            _details = new StackPanel { Visibility = Visibility.Collapsed };

            // Source details: the redirector itself, its package and its own name, the old address it forwards
            // from. The name is typed, or its arrow copies the selected object's; Enter or leaving the box applies it,
            // Escape puts it back. Icons sit at each row's end in Use, Find, Open slots, as in the table below.
            _details.Children.Add(TableCaption("Source details"));
            GenieFieldTable source = Genie.ControlUtils.CreateFieldTable();
            source.Margin          = new Thickness(0, 0, 0, 12);
            _sourcePackageName     = NameText();
            _findSourcePackage     = Genie.ControlUtils.CreateFindIcon(delegate {
                Genie.ContentBrowserUtils.SelectPackage(_sourcePackageName.Text);
            });
            source.AddRow("Package", _sourcePackageName, find: _findSourcePackage);

            _sourceName = Genie.ControlUtils.CreateFieldTextBox(null, "Type a name");
            Genie.ControlUtils.SetToolTip(_sourceName, "Its own name: the old address it forwards from");
            _sourceName.TextChanged += delegate {
                if (!_settingName) OnNameTyped();
            };
            _sourceName.KeyDown += delegate(object s, KeyEventArgs e) {
                if (e.Key == Key.Enter) {
                    e.Handled = true;
                    CommitName(false);
                } else if (e.Key == Key.Escape && _nameItem != null && !_nameItem.IsPending) {
                    e.Handled = true;
                    ShowSourceName(_nameItem);
                    UpdateDetails(_nameItem);
                }
            };
            // Leaving the box can be the start of a click on another row: CommitName waits for it.
            _sourceName.LostKeyboardFocus += delegate {
                CommitName(true);
            };
            _useName = Genie.ControlUtils.CreateUseIcon(delegate {
                OnUseName();
            }, "Use the Content Browser’s selected object’s name");
            source.AddRow("Object", _sourceName, _useName);
            _details.Children.Add(source);

            // Destination details: a table of package, object and class. Names start after a missing slot, kept
            // even when empty so they line up.
            _details.Children.Add(TableCaption("Destination details"));
            GenieFieldTable table = Genie.ControlUtils.CreateFieldTable();

            _destPackageMissing = MissingIcon();
            _destPackageName    = NameText();
            _findPackage        = Genie.ControlUtils.CreateFindIcon(delegate {
                Genie.ContentBrowserUtils.SelectPackage(_dest.PackageName);
            });
            table.AddRow("Package", NameCell(_destPackageMissing, _destPackageName), find: _findPackage);

            _destObjectMissing = MissingIcon();
            _destObjectName    = NameText();
            _useSelected       = Genie.ControlUtils.CreateUseIcon(delegate {
                OnUseSelected();
            }, "Point it at the Content Browser’s selected object", selectionChanged: JudgeSelection);
            // Find and Open work on the asset as the Content Browser lists it, loaded or not; Open loads it first.
            _findObject = Genie.ControlUtils.CreateFindIcon(delegate {
                Genie.ContentBrowserUtils.SelectAsset(DestinationAsset(_dest));
            });
            _openObject = Genie.ControlUtils.CreateOpenIcon(delegate {
                bool wasLoaded = _dest.IsObjectLoaded;
                Genie.ContentBrowserUtils.OpenEditor(DestinationAsset(_dest));
                if (!wasLoaded) OnShownPackageChanged(); // it may be loaded now: Repair, Repair all, the details
            });
            table.AddRow("Object", NameCell(_destObjectMissing, _destObjectName), _useSelected, _findObject,
                         _openObject);

            _destClass                     = Genie.ControlUtils.CreateTag("", Genie.ControlUtils.TagTone.Type);
            _destClass.HorizontalAlignment = HorizontalAlignment.Left;
            _classRow                      = table.AddRow("Class", _destClass);
            _details.Children.Add(table);

            _note            = Genie.ControlUtils.CreateParagraph();
            _note.FontSize   = Genie.Theme.FontSizeLabel;
            _note.Foreground = Genie.Theme.TextSecondary;
            _note.Margin     = new Thickness(0, 10, 0, 0);
            _note.Visibility = Visibility.Collapsed;
            _details.Children.Add(_note);

            Border line = Genie.ControlUtils.CreateSeparator();
            line.Margin = new Thickness(0, 16, 0, 12);
            _details.Children.Add(line);

            _repair = Genie.ControlUtils.CreateButton(
                "Repair", tooltip: "Reconnect this redirector to its destination (the destination must be loaded).");
            _repair.Click += delegate {
                OnRepair();
            };
            _delete        = Genie.ControlUtils.CreateButton("Delete", Genie.ControlUtils.ButtonTone.Danger,
                                                             tooltip: "Delete this redirector.");
            _delete.Click += delegate {
                OnDelete();
            };
            _details.Children.Add(Genie.ControlUtils.CreateButtonRow(_repair, _delete));

            right.Children.Add(_details);

            // Several selected: a title and Clear, the tables (scrolling), then the notes and buttons at the foot.
            _multi       = new DockPanel { Visibility = Visibility.Collapsed };
            DockPanel top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            TextBlock clear = Genie.ControlUtils.CreateLink("Clear selection", delegate {
                _list.CheckAll(false);
            });
            clear.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(clear, Dock.Right);
            top.Children.Add(clear);
            _multiTitle            = Genie.ControlUtils.CreateCaption("", false, true);
            _multiTitle.FontSize   = Genie.Theme.FontSizeBody;
            _multiTitle.Foreground = Genie.Theme.TextPrimary;
            top.Children.Add(_multiTitle);
            DockPanel.SetDock(top, Dock.Top);
            _multi.Children.Add(top);

            StackPanel foot = new StackPanel();
            _multiNotes     = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foot.Children.Add(_multiNotes);
            Border footLine = Genie.ControlUtils.CreateSeparator();
            footLine.Margin = new Thickness(0, 12, 0, 12);
            foot.Children.Add(footLine);
            _multiRepair = Genie.ControlUtils.CreateButton(
                "Repair", tooltip: "Reconnect the disconnected ones to their destinations.");
            _multiRepair.Click += delegate {
                OnRepairSelected();
            };
            _multiDelete = Genie.ControlUtils.CreateButton("Delete", Genie.ControlUtils.ButtonTone.Danger,
                                                           tooltip: "Delete these redirectors.");
            _multiDelete.Click += delegate {
                OnDeleteSelected();
            };
            foot.Children.Add(Genie.ControlUtils.CreateButtonRow(_multiRepair, _multiDelete));
            DockPanel.SetDock(foot, Dock.Bottom);
            _multi.Children.Add(foot);

            // The tables share one label column, so labels and values line up down the stack.
            _multiBody = new StackPanel();
            Grid.SetIsSharedSizeScope(_multiBody, true);
            _multi.Children.Add(new ScrollViewer {
                Content                       = _multiBody,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            right.Children.Add(_multi);

            // Body: list | splitter | details.
            Grid body = Genie.ControlUtils.CreateSplitPanel(left, right, DefaultSplit, 130, 170);
            Grid.SetRow(body, 2);
            root.Children.Add(body);
            Content = root;
        }

        // == Notifications ============================================================================

        private void OnShownPackageChanged() {
            RefreshKeepingSelection(true);
        }

        private void OnDestinationChanged() {
            RedirectorItem selected = SelectedItem();
            if (selected != null && _details.Visibility == Visibility.Visible) UpdateDetails(selected);
        }

        // == List =====================================================================================

        private void Refresh() {
            Refresh(null, null, false, null);
        }

        private void Refresh(string keepName, bool keepMessages, RedirectorItem keep) {
            Refresh(keepName, null, keepMessages, keep);
        }

        // Rebuilds the list, reselecting what's selected now: one by its name, as before, or several by path. A
        // renamed one is found by its new path: its item holds the new redirector.
        private void RefreshKeepingSelection(bool keepMessages) {
            List<RedirectorItem> selected = Selected();
            if (selected.Count <= 1) {
                RedirectorItem one = selected.Count == 1 ? selected[0] : null;
                Refresh(one != null ? one.Name : null, keepMessages, one);
                return;
            }
            List<string> paths = new List<string>();
            foreach (RedirectorItem item in selected) {
                string path = item.Redirector != IntPtr.Zero ? Genie.ObjectUtils.GetPathName(item.Redirector)
                                                             : item.PathName;
                if (path != null) paths.Add(path);
            }
            string message = keepMessages ? _multiMessage : null;
            Refresh(null, paths, keepMessages, null);
            if (message != null && Selected().Count > 1) ShowMultiMessage(message);
        }

        // Rebuilds the list for the followed package: its redirectors, then the ones added with + and not created
        // yet. Reselects keepName, or every path in keepPaths, or keep if it's pending. keepMessages: keep an
        // action's result on screen, for refreshes the user didn't ask for.
        private void Refresh(string keepName, List<string> keepPaths, bool keepMessages, RedirectorItem keep) {
            bool   ownHint     = _emptyState.Hint == SelectHint || _emptyState.Hint == NoPackageHint;
            string keptNote    = (keepMessages && _noteIsMessage) ? _message : null;
            bool   keptUnsaved = _messageUnsaved;
            string keptHint    = (keepMessages && !ownHint) ? _emptyState.Hint : null;

            string pkg = _follower.Package;
            _list.Items.Clear();
            ShowEmptyState();
            _emptyState.Hint = pkg == null ? NoPackageHint : SelectHint;
            if (pkg == null)
                Genie.ControlUtils.Block(_add, "Select a package in the Content Browser");
            else if (!Genie.ObjectUtils.IsPackageLoaded(pkg))
                Genie.ControlUtils.Block(_add, "Load " + pkg + " first");
            else
                Genie.ControlUtils.Unblock(_add);
            if (pkg == null) {
                SetCounts(-1, 0);
                return;
            }

            int               count = 0, broken = 0;
            List<ListBoxItem> reselect = new List<ListBoxItem>();
            try {
                foreach (Genie.RedirectorUtils.Found found in Genie.RedirectorUtils.FindInPackage(pkg)) {
                    RedirectorItem item = new RedirectorItem {
                        Redirector  = found.Redirector,
                        PathName    = found.PathName,
                        ExportIndex = found.ExportIndex,
                        Name        = Genie.PathUtils.GetLeafName(found.PathName),
                    };
                    IntPtr      redirector = found.Redirector;
                    bool        isBroken   = Genie.RedirectorUtils.IsBroken(redirector);
                    ListBoxItem li       = AddRow(item, isBroken);
                    if (keepPaths != null) {
                        if (keepPaths.Exists(p => string.Equals(p, item.PathName, StringComparison.OrdinalIgnoreCase)))
                            reselect.Add(li);
                    } else if (keep == null || !keep.IsPending) {
                        if (keepName != null && reselect.Count == 0 &&
                            string.Equals(item.Name, keepName, StringComparison.OrdinalIgnoreCase))
                            reselect.Add(li);
                    }
                    count++;
                    if (isBroken) broken++;
                }
            } catch (Exception ex) {
                SetCounts(-1, 0);
                _emptyState.Hint = "Couldn’t read the package: " + ex.Message;
                Genie.Log("Couldn't read package " + pkg + ": " + ex);
                return;
            }
            foreach (RedirectorItem item in _pending) {
                if (!string.Equals(item.Package, pkg, StringComparison.OrdinalIgnoreCase)) continue;
                ListBoxItem li = AddRow(item, false);
                if (item == keep) reselect.Add(li);
            }

            SetCounts(count, broken);
            _list.SelectRows(reselect); // one selection change, however many
            if (keptNote != null && reselect.Count == 1) ShowMessage(keptNote, keptUnsaved);
            if (keptHint != null && reselect.Count == 0) _emptyState.Hint = keptHint;
            if (reselect.Count == 0) UpdateTrash(new List<RedirectorItem>());
        }

        // A row: its tick box (off while it isn't loaded or isn't created yet), a warning slot, kept even when empty
        // so names line up, then the name. Pending: muted, in italics. Not loaded: muted.
        private ListBoxItem AddRow(RedirectorItem item, bool isBroken) {
            DockPanel        line = new DockPanel();
            FrameworkElement warn = Genie.ControlUtils.CreateWarningIcon();
            warn.Margin           = new Thickness(0, 0, 5, 0);
            warn.Visibility       = isBroken ? Visibility.Visible : Visibility.Hidden;
            line.Children.Add(warn);
            item.Label              = Genie.ControlUtils.CreateMonoText();
            item.Label.Margin       = new Thickness(0);
            item.Label.TextTrimming = TextTrimming.CharacterEllipsis;
            if (isBroken) item.Label.Foreground = Genie.Theme.Danger;
            if (item.IsPending) {
                item.Label.Foreground = Genie.Theme.TextSecondary;
                item.Label.FontStyle  = FontStyles.Italic;
                Genie.ControlUtils.SetToolTip(line, "Not created yet: it needs a name and a destination.");
            } else if (item.IsUnloaded) {
                item.Label.Foreground = Genie.Theme.TextSecondary;
                Genie.ControlUtils.SetToolTip(line, UnloadedTip);
            }
            ShowLabel(item);
            line.Children.Add(item.Label);
            ListBoxItem row = _list.AddItem(line, item);
            item.Row        = row;
            if (item.IsPending)
                _list.SetCheckable(row, false, PendingCheckTip);
            else if (item.IsUnloaded)
                _list.SetCheckable(row, false, UnloadedCheckTip);
            return row;
        }

        private static void ShowLabel(RedirectorItem item) {
            if (item.Label != null) item.Label.Text = string.IsNullOrEmpty(item.Name) ? Placeholder : item.Name;
        }

        // count < 0: unknown, the package couldn't be read. Pending rows aren't redirectors yet: not counted.
        private void SetCounts(int count, int broken) {
            _countPill.Text           = Genie.TextUtils.Count(count, "redirector");
            _countPill.Visibility     = count >= 0 ? Visibility.Visible : Visibility.Collapsed;
            _listCountPill.Text       = count.ToString();
            _listCountPill.Visibility = count >= 0 ? Visibility.Visible : Visibility.Collapsed;
            _brokenPill.Text          = broken + " broken";
            _brokenPill.Visibility    = broken > 0 ? Visibility.Visible : Visibility.Collapsed;
            _brokenCount              = Math.Max(broken, 0);
            UpdateBanner();
        }

        // "other" while the selected redirector isn't one of them, so it doesn't read as counted.
        private void UpdateBanner() {
            RedirectorItem selected = SelectedItem();
            bool other =
                selected != null && (selected.IsPending || !Genie.RedirectorUtils.IsBroken(selected.Redirector));
            int  n     = _brokenCount;
            _saveBanner.Text       = "Saving this UPK deletes its " + (other ? "other " : "") +
                               Genie.TextUtils.Count(n, "broken redirector") + ".";
            _saveBanner.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // == Details ==================================================================================

        // None selected: the explainer. One: its details. Several: BuildMulti.
        private void OnSelect() {
            _noteIsMessage = false;
            _multiMessage  = null;
            UpdateBanner();
            List<RedirectorItem> selected = Selected();
            UpdateTrash(selected);
            if (selected.Count == 0) {
                _destinationWatch.ChangePackage(null);
                ShowEmptyState();
                return;
            }
            if (selected.Count > 1) {
                // Ticking another row leaves the name box focused (tick boxes take no focus): a name typed there is
                // applied once this click is done, and the selection kept (Rebuild).
                if (_nameDirty) CommitName(true);
                _destinationWatch.ChangePackage(null);
                BuildMulti(selected);
                return;
            }
            UpdateDetails(selected[0]);
        }

        // The trash deletes what's selected, but never one that isn't loaded; its badge counts them.
        private void UpdateTrash(List<RedirectorItem> selected) {
            int deletable = selected.FindAll(i => !i.IsUnloaded).Count;
            Genie.ControlUtils.SetBadgeCount(_trash, deletable);
            if (selected.Count == 0)
                Genie.ControlUtils.Block(_trash, "Select redirectors to delete them");
            else if (deletable == 0)
                Genie.ControlUtils.Block(_trash, UnloadedTip);
            else
                Genie.ControlUtils.Unblock(_trash);
        }

        // Also runs when the destination package loads or unloads, and as the name is typed.
        private void UpdateDetails(RedirectorItem item) {
            bool broken = !item.IsPending && Genie.RedirectorUtils.IsBroken(item.Redirector);
            bool isNew  = !item.IsPending && Genie.ObjectUtils.IsNew(item.Redirector);
            AddonUtils.Destination d;
            try {
                d = item.IsPending    ? PendingDestination(item)
                    : item.IsUnloaded ? AddonUtils.GetUnloadedDestination(item.PathName, item.ExportIndex)
                                      : AddonUtils.GetDestination(item.Redirector);
            } catch (Exception ex) {
                Genie.Log("Couldn't resolve " + item.Name + ": " + ex);
                d = new AddonUtils.Destination();
            }
            _dest = d;
            Genie.ControlUtils.RecheckUseIcon(_useSelected); // it judges the selection against this destination
            _destinationWatch.ChangePackage(d.IsResolved ? d.PackageName : null);

            // Source: typing survives rebuilds of the list, which make new items for the same redirector.
            string sourcePackage    = item.IsPending ? item.Package : _follower.Package;
            _sourcePackageName.Text = sourcePackage ?? "";
            Genie.ControlUtils.SetFindable(_findSourcePackage,
                                           sourcePackage != null &&
                                               Genie.ContentBrowserUtils.IsPackageListed(sourcePackage));
            if (!_nameDirty || !SameItem(_nameItem, item)) ShowSourceName(item);
            _nameItem              = item;
            // Its full path, as the destination object's tooltip; a new one has none yet.
            string sourcePath = item.IsPending ? null : SourcePath(item);
            Genie.ControlUtils.SetToolTip(_sourceName, sourcePath ?? "Its own name: the old address it forwards from");
            string nameProblem     = NameProblem(item);
            _sourceName.Foreground = nameProblem != null ? Genie.Theme.Danger : Genie.Theme.TextPrimary;
            // Read only while not loaded: the name, its arrow, the destination's arrow and Delete.
            _sourceName.IsEnabled = !item.IsUnloaded;
            _delete.IsEnabled     = !item.IsUnloaded;
            foreach (FrameworkElement use in new[] { _useName, _useSelected }) {
                if (item.IsUnloaded)
                    Genie.ControlUtils.Block(use, UnloadedTip);
                else
                    Genie.ControlUtils.Unblock(use);
            }

            // The missing slot is kept on every row while either name is missing, so the names line up; with
            // both found it goes, and the names and class start at the label column's edge.
            bool packageMissing = d.IsResolved && !d.IsPackageLoaded;
            bool objectMissing  = d.IsResolved && !d.IsObjectLoaded;
            bool keepSlot       = packageMissing || objectMissing;
            ShowName(_destPackageName, _destPackageMissing, d.PackageName, "Set by the object", packageMissing,
                     keepSlot);
            Genie.ControlUtils.SetToolTip(_destPackageName, d.PackageName);
            // The tree lists packages that aren't loaded too.
            Genie.ControlUtils.SetFindable(_findPackage, d.PackageName != null &&
                                                             Genie.ContentBrowserUtils.IsPackageListed(d.PackageName));

            ShowName(_destObjectName, _destObjectMissing, d.ObjectName, "Select an object", objectMissing, keepSlot);
            Genie.ControlUtils.SetToolTip(_destObjectName, string.IsNullOrEmpty(d.PathName) ? null : d.PathName);
            string asset  = DestinationAsset(d);
            bool   listed = asset != null && Genie.ContentBrowserUtils.IsAssetListed(asset);
            Genie.ControlUtils.SetFindable(_findObject, listed);
            Genie.ControlUtils.SetOpenable(_openObject, d.IsObjectLoaded || listed);

            _destClass.Text      = d.ClassName ?? "";
            _destClass.Margin    = new Thickness(keepSlot ? MissingSlot : 0, 0, 0, 0);
            _classRow.Visibility = string.IsNullOrEmpty(d.ClassName) ? Visibility.Collapsed : Visibility.Visible;

            bool repairable   = broken && !isNew && d.IsResolved && d.IsObjectLoaded;
            _repair.IsEnabled = repairable;
            Genie.ControlUtils.SetToolTip(_delete, item.IsPending    ? "Remove it: it isn’t created yet."
                                                   : item.IsUnloaded ? UnloadedTip
                                                                     : "Delete this redirector.");

            // Short: the banner says what saving does, so notes only say what's wrong or what changed.
            if (!_noteIsMessage) {
                string unsaved = item.IsPending || item.IsUnloaded ? null : AddonUtils.GetUnsavedNote(item.Redirector);
                bool   warn = false, showsUnsaved = false; // showsUnsaved: the unsaved note, with its orange mark
                string note = "";
                if (item.IsUnloaded) { // first: nothing else can be done until it loads
                    note = UnloadedNote;
                } else if (nameProblem != null) {
                    note = nameProblem;
                    warn = true;
                } else if (item.IsPending) {
                    note = PendingNote(item, d);
                } else if (isNew && broken) { // before the unsaved note: a renamed broken one needs a destination
                    note = "It points nowhere: select an object in the Content Browser, then click the arrow " +
                           "beside Object.";
                    warn = true;
                } else if (unsaved != null) {
                    note         = unsaved;
                    showsUnsaved = true;
                } else if (!d.IsResolved) {
                    note = "Destination unknown: " + (d.UnresolvedReason ?? "it couldn’t be resolved") + ".";
                } else if (repairable) {
                    note = "Disconnected. Repair to reconnect it.";
                    warn = true;
                } else if (broken && !d.IsPackageLoaded) {
                    note = "Load the destination package, then Repair.";
                } else if (broken) {
                    note = "No such object in the destination package. Select another in the Content Browser, " +
                           "then click the arrow beside Object.";
                }
                ShowNote(note, warn, showsUnsaved);
            }

            _emptyState.Visibility = Visibility.Collapsed;
            _multi.Visibility      = Visibility.Collapsed;
            _details.Visibility    = Visibility.Visible;
        }

        // A redirector's own address ("Pkg.Group.Name"): its live path while loaded, else as its package lists it.
        private static string SourcePath(RedirectorItem item) {
            string path = item.Redirector != IntPtr.Zero ? Genie.ObjectUtils.GetPathName(item.Redirector) : null;
            return path ?? item.PathName;
        }

        // The destination as the Content Browser names it ("Class Pkg.Group.Obj"), loaded or not, or null while its
        // class is unknown.
        private static string DestinationAsset(AddonUtils.Destination d) {
            if (d.IsObjectLoaded) return Genie.ObjectUtils.GetFullName(d.Object);
            return d.IsResolved && d.ClassName != null ? d.ClassName + " " + d.PathName : null;
        }

        // A pending one's destination, as GetDestination reads a redirector's: by path, so an object that unloaded
        // since shows as missing.
        private static AddonUtils.Destination PendingDestination(RedirectorItem item) {
            AddonUtils.Destination d = new AddonUtils.Destination();
            if (item.DestinationPath == null) return d;
            d.PathName        = item.DestinationPath;
            d.PackageName     = Genie.PathUtils.GetPackageName(d.PathName);
            d.ObjectName      = Genie.PathUtils.GetLeafName(d.PathName);
            d.IsResolved      = true;
            d.Object          = Genie.ObjectUtils.FindObject(d.PathName);
            d.IsObjectLoaded  = d.Object != IntPtr.Zero;
            d.Package         = Genie.ObjectUtils.FindPackage(d.PackageName);
            d.IsPackageLoaded = Genie.ObjectUtils.IsPackageLoaded(d.Package);
            string cls        = d.IsObjectLoaded ? Genie.ObjectUtils.GetClassPathName(d.Object) : null;
            d.ClassName       = Genie.PathUtils.GetLeafName(cls);
            d.ClassPackage    = cls == null ? null : Genie.PathUtils.GetPackageName(cls);
            return d;
        }

        // "Not created yet. It needs a name and a destination."
        private static string PendingNote(RedirectorItem item, AddonUtils.Destination d) {
            if (d.IsResolved && !d.IsObjectLoaded)
                return d.PathName + " isn’t loaded any more. Select another object.";
            List<string> needs = new List<string>();
            if (string.IsNullOrEmpty(item.Name)) needs.Add("a name");
            if (!d.IsResolved) needs.Add("a destination");
            return needs.Count == 0 ? "" : "Not created yet. It needs " + string.Join(" and ", needs.ToArray()) + ".";
        }

        // Why the name can't be used: a pending one's whenever it's typed, an existing one's while it's being edited.
        private string NameProblem(RedirectorItem item) {
            string name = (_nameDirty && SameItem(_nameItem, item)) ? _sourceName.Text.Trim()
                          : item.IsPending                          ? item.Name
                                                                    : null;
            if (string.IsNullOrEmpty(name)) return null;
            if (!item.IsPending &&
                string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase))
                return null;
            string outerPath = item.IsPending
                                   ? item.Package
                                   : Genie.PathUtils.GetOuterName(Genie.ObjectUtils.GetPathName(item.Redirector));
            return outerPath == null ? null : Genie.ObjectUtils.CheckNewName(outerPath, name, item.Redirector);
        }

        // The same entry: the same pending one, or the same redirector (rebuilds make new items for it), by path while
        // it isn't loaded.
        private static bool SameItem(RedirectorItem a, RedirectorItem b) {
            if (a == null || b == null) return false;
            if (a.IsPending || b.IsPending) return a == b;
            if (a.IsUnloaded || b.IsUnloaded)
                return a.IsUnloaded && b.IsUnloaded &&
                       string.Equals(a.PathName, b.PathName, StringComparison.OrdinalIgnoreCase);
            return a.Redirector == b.Redirector;
        }

        private void ShowSourceName(RedirectorItem item) {
            _settingName     = true;
            _sourceName.Text = item == null ? "" : item.Name ?? "";
            _settingName     = false;
            _nameItem        = item;
            _nameDirty       = false;
        }

        private void ShowEmptyState() {
            _dest                  = new AddonUtils.Destination();
            _details.Visibility    = Visibility.Collapsed;
            _multi.Visibility      = Visibility.Collapsed;
            _emptyState.Visibility = Visibility.Visible;
        }

        // An action's result. unsaved: it changed the package, so it gets the orange mark as unsaved notes do.
        private void ShowMessage(string text, bool unsaved = false) {
            ShowNote(text, false, unsaved);
            _noteIsMessage  = true;
            _message        = text;
            _messageUnsaved = unsaved;
        }

        // The note under the details, led by a triangle: red for something to fix (warn), orange for a change the
        // editor drops if closed unsaved. Hidden while empty.
        private void ShowNote(string text, bool warn, bool unsaved) {
            FrameworkElement mark = warn      ? Genie.ControlUtils.CreateWarningIcon()
                                    : unsaved ? Genie.ControlUtils.CreateWarningIcon(Genie.Theme.WarningTagText)
                                              : null;
            _note.SetParts(mark, text);
            _note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Use turns orange whenever the one selected asset's class differs from the class the destination details
        // show. By the browser's class name, so it works before the asset is loaded. Nothing to compare with while no
        // destination is set. Still allowed: the click asks first.
        private void JudgeSelection(Genie.ContentBrowserUtils.BrowserAsset[] selected) {
            string shown = _details.Visibility == Visibility.Visible ? _dest.ClassName : null;
            if (!string.IsNullOrEmpty(shown) && selected.Length == 1 &&
                !string.Equals(selected[0].Type, shown, StringComparison.OrdinalIgnoreCase))
                Genie.ControlUtils.SetTone(_useSelected, Genie.ControlUtils.TagTone.Warning, MismatchTip);
            else
                Genie.ControlUtils.ClearTone(_useSelected);
        }

        // The one selected, or null while none or several are.
        private RedirectorItem SelectedItem() {
            List<RedirectorItem> selected = Selected();
            return selected.Count == 1 ? selected[0] : null;
        }

        // Every selected (ticked) one, in list order.
        private List<RedirectorItem> Selected() {
            return _list.SelectedTags<RedirectorItem>();
        }

        // == Several selected =========================================================================
        // A table for the redirectors' own package, then one per destination package, each redirector in it as its
        // Src object, Dst object and Dst class, set apart by a heavier line; unknown destinations last. The tables
        // share one label column. Each row's icons act on its own redirector. Under them: why Repair can't fix the
        // broken ones it can't, and an action's result.

        private sealed class Entry {
            public RedirectorItem         Item;
            public AddonUtils.Destination Dest;
        }

        private readonly List<string> _multiWarnings = new List<string>();

        private void BuildMulti(List<RedirectorItem> selected) {
            _multiBody.Children.Clear();
            _multiTitle.Text = selected.Count + " redirectors selected";

            string          sourcePackage = _follower.Package;
            GenieFieldTable source        = Genie.ControlUtils.CreateFieldTable();
            source.SharesLabelWidth       = true;
            TextBlock sourceName          = NameText();
            sourceName.Text               = sourcePackage ?? "";
            Genie.ControlUtils.SetToolTip(sourceName, sourcePackage);
            Border findSourcePackage = Genie.ControlUtils.CreateFindIcon(delegate {
                Genie.ContentBrowserUtils.SelectPackage(sourcePackage);
            });
            Genie.ControlUtils.SetFindable(
                findSourcePackage, sourcePackage != null && Genie.ContentBrowserUtils.IsPackageListed(sourcePackage));
            source.AddRow("Src package", sourceName, find: findSourcePackage);
            _multiBody.Children.Add(source);

            // By destination package, in list order; unknown destinations last. Counted on the way: what Repair can
            // fix, and why it can't fix the rest.
            List<string>                    order = new List<string>();
            Dictionary<string, List<Entry>> groups =
                new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
            List<Entry> unknown = new List<Entry>();
            int repairable = 0, waitPackage = 0, noObject = 0, nowhere = 0, unresolved = 0;
            foreach (RedirectorItem item in selected) {
                AddonUtils.Destination d;
                try {
                    d = AddonUtils.GetDestination(item.Redirector);
                } catch (Exception ex) {
                    Genie.Log("Couldn't resolve " + item.Name + ": " + ex);
                    d = new AddonUtils.Destination();
                }
                Entry entry = new Entry { Item = item, Dest = d };
                if (!d.IsResolved || string.IsNullOrEmpty(d.PackageName)) {
                    unknown.Add(entry);
                } else {
                    List<Entry> group;
                    if (!groups.TryGetValue(d.PackageName, out group)) {
                        group                 = new List<Entry>();
                        groups[d.PackageName] = group;
                        order.Add(d.PackageName);
                    }
                    group.Add(entry);
                }
                if (!Genie.RedirectorUtils.IsBroken(item.Redirector)) continue;
                if (Genie.ObjectUtils.IsNew(item.Redirector))
                    nowhere++; // a renamed broken one: where it pointed was only on record in its file
                else if (!d.IsResolved)
                    unresolved++;
                else if (d.IsObjectLoaded)
                    repairable++;
                else if (!d.IsPackageLoaded)
                    waitPackage++;
                else
                    noObject++;
            }
            foreach (string package in order) _multiBody.Children.Add(DestinationTable(groups[package]));
            if (unknown.Count > 0) _multiBody.Children.Add(DestinationTable(unknown));

            _multiWarnings.Clear();
            if (repairable > 0)
                _multiWarnings.Add(repairable + " disconnected: Repair reconnects " +
                                   (repairable == 1 ? "it." : "them."));
            if (waitPackage > 0)
                _multiWarnings.Add(waitPackage + (waitPackage == 1 ? " needs its" : " need their") +
                                   " destination package loaded before Repair.");
            if (noObject > 0)
                _multiWarnings.Add(noObject + (noObject == 1 ? " points at an object its package doesn’t have"
                                                             : " point at objects their packages don’t have") +
                                   ": retarget " + (noObject == 1 ? "it" : "each") +
                                   " with the arrow beside Dst object.");
            if (nowhere > 0)
                _multiWarnings.Add(nowhere + (nowhere == 1 ? " points" : " point") +
                                   " nowhere: select an object in the Content Browser, then click the arrow beside " +
                                   (nowhere == 1 ? "its" : "each one’s") + " Dst object.");
            if (unresolved > 0)
                _multiWarnings.Add(unresolved + (unresolved == 1 ? " has an unknown destination."
                                                                 : " have unknown destinations."));
            RenderMultiNotes();

            _multiRepair.Content = repairable > 1 ? "Repair " + repairable : "Repair";
            if (repairable > 0)
                Genie.ControlUtils.Unblock(_multiRepair);
            else
                Genie.ControlUtils.Block(_multiRepair, "None of these can be repaired: a broken one's destination " +
                                                           "must be loaded first.");
            _multiDelete.Content = "Delete " + selected.Count;

            _emptyState.Visibility = Visibility.Collapsed;
            _details.Visibility    = Visibility.Collapsed;
            _multi.Visibility      = Visibility.Visible;
        }

        // One destination package's table: the package, then each redirector pointing into it.
        private GenieFieldTable DestinationTable(List<Entry> entries) {
            AddonUtils.Destination first = entries[0].Dest;
            bool                   known = first.IsResolved && !string.IsNullOrEmpty(first.PackageName);
            GenieFieldTable        table = Genie.ControlUtils.CreateFieldTable();
            table.SharesLabelWidth       = true;
            table.Margin                 = new Thickness(0, 10, 0, 0);

            FrameworkElement packageMissing = MissingIcon();
            TextBlock        packageName    = NameText();
            packageName.FontWeight          = FontWeights.SemiBold;
            ShowName(packageName, packageMissing, known ? first.PackageName : null, "Unknown",
                     known && !first.IsPackageLoaded, false);
            Genie.ControlUtils.SetToolTip(packageName, known ? first.PackageName : null);
            string package     = first.PackageName;
            Border findPackage = Genie.ControlUtils.CreateFindIcon(delegate {
                Genie.ContentBrowserUtils.SelectPackage(package);
            });
            Genie.ControlUtils.SetFindable(findPackage, known && Genie.ContentBrowserUtils.IsPackageListed(package));
            table.AddRow("Dst package", NameCell(packageMissing, packageName), find: findPackage);

            foreach (Entry entry in entries) {
                table.AddBreak();
                RedirectorItem         item   = entry.Item;
                AddonUtils.Destination d      = entry.Dest;
                bool                   broken = Genie.RedirectorUtils.IsBroken(item.Redirector);

                // Src object: broken, red with the list's triangle; changes not yet saved, the orange triangle after
                // it, its note as the tooltip.
                FrameworkElement brokenIcon = MissingIcon();
                brokenIcon.Visibility       = broken ? Visibility.Visible : Visibility.Collapsed;
                Genie.ControlUtils.SetToolTip(brokenIcon, "Broken");
                TextBlock sourceName  = NameText();
                sourceName.Text       = item.Name;
                sourceName.Foreground = broken ? Genie.Theme.Danger : Genie.Theme.TextPrimary;
                // Its full path, as Dst object's tooltip shows the destination's: the two can differ by a group.
                Genie.ControlUtils.SetToolTip(sourceName, SourcePath(item));
                DockPanel sourceCell = NameCell(brokenIcon, sourceName);
                string    unsaved    = AddonUtils.GetUnsavedNote(item.Redirector);
                if (unsaved != null) {
                    FrameworkElement mark =
                        Genie.ControlUtils.CreateWarningIcon(Genie.Theme.WarningTagText, MissingSize);
                    mark.Margin           = new Thickness(5, 0, 0, 0);
                    Genie.ControlUtils.SetToolTip(mark, unsaved);
                    DockPanel.SetDock(mark, Dock.Right);
                    sourceCell.Children.Insert(0, mark);
                }
                Border useName = Genie.ControlUtils.CreateUseIcon(delegate {
                    OnUseNameFor(item);
                }, "Rename it to the Content Browser’s selected object’s name");
                table.AddRow("Src object", sourceCell, useName);

                // Dst object, as the details show it.
                FrameworkElement objectMissing = MissingIcon();
                TextBlock        objectName    = NameText();
                ShowName(objectName, objectMissing, d.IsResolved ? d.ObjectName : null, "Destination unknown",
                         d.IsResolved && !d.IsObjectLoaded, false);
                Genie.ControlUtils.SetToolTip(objectName, d.IsResolved ? d.PathName : d.UnresolvedReason);
                AddonUtils.Destination shown = d;
                Border                 use   = null;
                use = Genie.ControlUtils.CreateUseIcon(delegate {
                    OnUseSelectedFor(item, shown);
                }, "Point it at the Content Browser’s selected object", selectionChanged: delegate(
                    Genie.ContentBrowserUtils.BrowserAsset[] assets) {
                    Judge(use, shown.ClassName, assets);
                });
                string asset  = DestinationAsset(d);
                bool   listed = asset != null && Genie.ContentBrowserUtils.IsAssetListed(asset);
                Border find   = Genie.ControlUtils.CreateFindIcon(delegate {
                    Genie.ContentBrowserUtils.SelectAsset(asset);
                });
                Genie.ControlUtils.SetFindable(find, listed);
                Border open = Genie.ControlUtils.CreateOpenIcon(delegate {
                    Genie.ContentBrowserUtils.OpenEditor(asset);
                    if (!shown.IsObjectLoaded) OnShownPackageChanged(); // it may be loaded now: Repair, the notes
                });
                Genie.ControlUtils.SetOpenable(open, d.IsObjectLoaded || listed);
                table.AddRow("Dst object", NameCell(objectMissing, objectName), use, find, open);

                if (!string.IsNullOrEmpty(d.ClassName)) {
                    GenieTag kind = Genie.ControlUtils.CreateTag(d.ClassName, Genie.ControlUtils.TagTone.Type);
                    kind.HorizontalAlignment = HorizontalAlignment.Left;
                    table.AddRow("Dst class", kind);
                }
            }
            return table;
        }

        // Use turns orange while the one selected asset's class differs from the destination's, as the details'.
        private static void Judge(FrameworkElement use, string shownClass,
                                  Genie.ContentBrowserUtils.BrowserAsset[] selected) {
            if (!string.IsNullOrEmpty(shownClass) && selected.Length == 1 &&
                !string.Equals(selected[0].Type, shownClass, StringComparison.OrdinalIgnoreCase))
                Genie.ControlUtils.SetTone(use, Genie.ControlUtils.TagTone.Warning, MismatchTip);
            else
                Genie.ControlUtils.ClearTone(use);
        }

        // An action's result over the multi-selection's notes, until the selection changes.
        private void ShowMultiMessage(string text) {
            _multiMessage = text;
            RenderMultiNotes();
        }

        private void RenderMultiNotes() {
            _multiNotes.Children.Clear();
            if (_multiMessage != null) _multiNotes.Children.Add(NoteLine(null, _multiMessage));
            foreach (string warning in _multiWarnings)
                _multiNotes.Children.Add(NoteLine(Genie.ControlUtils.CreateWarningIcon(), warning));
            _multiNotes.Visibility = _multiNotes.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // A line of the notes, in the details' note style, led by mark if any.
        private static GenieParagraph NoteLine(FrameworkElement mark, string text) {
            GenieParagraph line = Genie.ControlUtils.CreateParagraph(mark, text);
            line.FontSize       = Genie.Theme.FontSizeLabel;
            line.Foreground     = Genie.Theme.TextSecondary;
            line.Margin         = new Thickness(0, 0, 0, 4);
            return line;
        }

        // == Actions ==================================================================================
        // The UI shows each result; the log records changes made and repairs that failed.

        private void OnAdd() {
            CommitName(false);
            string pkg = _follower.Package;
            if (pkg == null) return;
            RedirectorItem item = new RedirectorItem { IsPending = true, Package = pkg, Name = "" };
            _pending.Add(item);
            Refresh(null, false, item);
            _sourceName.Focus();
        }

        private void OnNameTyped() {
            RedirectorItem item = _nameItem;
            if (item == null) return;
            _nameDirty     = true;
            _noteIsMessage = false;
            if (item.IsPending) {
                item.Name = _sourceName.Text.Trim();
                ShowLabel(item);
            }
            UpdateDetails(item);
        }

        // Applies the typed name: a pending one's is kept, and creates it once it has a destination; an existing
        // one is renamed. later: leaving the box, which can be the start of a click on another row, so the list is
        // rebuilt after that click, not under it.
        private void CommitName(bool later) {
            RedirectorItem item = _nameItem;
            if (item == null || !_nameDirty) return;
            string name = _sourceName.Text.Trim();
            if (item.IsPending) {
                _nameDirty = false;
                item.Name  = name;
                TryCreate(item, later);
                return;
            }
            if (string.IsNullOrEmpty(name) || NameProblem(item) != null) return; // the note says why; still typing
            _nameDirty = false;
            if (string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase)) return;

            string old = item.Name, message;
            IntPtr r   = AddonUtils.Rename(item.Redirector, name, out message);
            if (r == IntPtr.Zero) {
                Say(item, "Couldn’t rename it: " + message + ".", false);
                return;
            }
            item.Redirector = r; // so a Use that commits first acts on the new one
            item.Name       = name;
            // Its note says it was renamed, or that it points nowhere now; only a failure needs saying here.
            Rebuild(item, name, message != null ? "Renamed from " + old + ", but " + message + "." : null, later);
        }

        private void OnUseName() {
            RedirectorItem item = SelectedItem();
            IntPtr         obj  = SingleSelection(item);
            if (obj == IntPtr.Zero) return;
            _sourceName.Text = Genie.PathUtils.GetLeafName(Genie.ObjectUtils.GetPathName(obj)); // types it
            CommitName(false);
        }

        // A pending one is created once it has a name and a destination, whichever comes second.
        private void TryCreate(RedirectorItem item, bool later) {
            if (string.IsNullOrEmpty(item.Name) || item.DestinationPath == null || NameProblem(item) != null) {
                if (SelectedItem() == item) UpdateDetails(item);
                return;
            }
            IntPtr dest = Genie.ObjectUtils.FindObject(item.DestinationPath);
            if (dest == IntPtr.Zero) {
                if (SelectedItem() == item) UpdateDetails(item); // says it isn't loaded any more
                return;
            }
            string message;
            IntPtr r = AddonUtils.Create(item.Package, item.Name, dest, out message);
            if (r == IntPtr.Zero) {
                Say(item, "Couldn’t create it: " + message + ".", false);
                return;
            }
            _pending.Remove(item);
            Rebuild(item, item.Name, null, later); // its note says it was created
        }

        // Rebuilds the list after a change to item, now or after the click in progress, keeping it selected, with
        // message if any, if the user is still on it, else keeping what they chose.
        private void Rebuild(RedirectorItem item, string name, string message, bool later) {
            Action rebuild = delegate {
                // Several ticked since (the name was applied as another row was ticked): keep them all.
                if (Selected().Count > 1) {
                    RefreshKeepingSelection(true);
                    if (message != null) ShowMultiMessage(message);
                    return;
                }
                RedirectorItem now    = SelectedItem();
                bool           stayed = now == null || SameItem(now, item) || now == item;
                if (stayed) {
                    Refresh(name, false, null);
                    if (message != null) ShowMessage(message, true);
                } else {
                    Refresh(now.Name, true, now);
                }
            };
            if (later)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, rebuild);
            else
                rebuild();
        }

        // A result for item, if it's still the one shown.
        private void Say(RedirectorItem item, string text, bool unsaved) {
            if (SameItem(SelectedItem(), item)) ShowMessage(text, unsaved);
        }

        // The one object selected in the Content Browser, or Zero with the reason shown.
        private IntPtr SingleSelection(RedirectorItem item) {
            return SingleSelection(item, false);
        }

        // multi: the reason goes in the multi-selection's notes.
        private IntPtr SingleSelection(RedirectorItem item, bool multi) {
            if (item == null || item.IsUnloaded) return IntPtr.Zero;
            IntPtr[] selected = Genie.ContentBrowserUtils.GetSelectedObjects();
            if (selected.Length == 1) return selected[0];
            string why = selected.Length == 0 ? "Select an object in the Content Browser first."
                                              : "Select just one object in the Content Browser.";
            if (multi)
                ShowMultiMessage(why);
            else
                ShowMessage(why);
            return IntPtr.Zero;
        }

        private void OnUseSelected() {
            CommitName(false); // a name typed first counts
            RedirectorItem item = SelectedItem();
            IntPtr         obj  = SingleSelection(item);
            if (obj == IntPtr.Zero) return;

            if (item.IsPending) {
                string refused = AddonUtils.CheckDestination(IntPtr.Zero, obj);
                if (refused != null) {
                    ShowMessage("Couldn’t use the selection: " + refused + ".");
                    return;
                }
                item.DestinationPath = Genie.ObjectUtils.GetPathName(obj);
                _noteIsMessage       = false;
                TryCreate(item, false);
                return;
            }
            Retarget(item, _dest, obj, false);
        }

        // Points item at obj. No prompt, as the host's Use buttons, unless the class differs from the one shown
        // (shown: its destination as the details show it): the details show the new destination and the unsaved
        // note, so only a failure needs saying. multi: from the multi-selection, which is kept.
        private void Retarget(RedirectorItem item, AddonUtils.Destination shown, IntPtr obj, bool multi) {
            string expected, actual;
            if (AddonUtils.IsClassMismatch(item.Redirector, obj, out expected, out actual)) {
                // Old in grey, new in orange, in the box and among the words alike.
                Genie.ControlUtils.TagTone was = Genie.ControlUtils.TagTone.Neutral;
                Genie.ControlUtils.TagTone now = Genie.ControlUtils.TagTone.Warning;
                string         newPath = Genie.ObjectUtils.GetPathName(obj);
                GenieChangeBox change  = Genie.ControlUtils.CreateChangeBox(
                    Genie.PathUtils.GetLeafName(shown.PathName), Genie.PathUtils.GetLeafName(newPath),
                    Genie.ControlUtils.CreateTag(expected, was), Genie.ControlUtils.CreateTag(actual, now));
                Genie.ControlUtils.SetToolTip(change.FromText, shown.PathName);
                Genie.ControlUtils.SetToolTip(change.ToText, newPath);
                GenieParagraph note = Genie.ControlUtils.CreateParagraph(
                    "Packages using this redirector resolve its destination only if it’s the class they expect. " +
                        "After this change, ones expecting a ",
                    Genie.ControlUtils.CreateTag(actual, now), " will; ones expecting a ",
                    Genie.ControlUtils.CreateTag(expected, was), " will get an empty reference.");
                if (!Genie.ControlUtils.Confirm(this, "Change the destination object?", "Change", note,
                                                new[] { change }, Genie.ControlUtils.TagTone.Warning,
                                                detail: "The new object is a different class."))
                    return;
            }
            string message;
            bool   ok = AddonUtils.Retarget(item.Redirector, obj, out message);
            if (multi) {
                RefreshKeepingSelection(false);
                if (!ok) ShowMultiMessage("Couldn’t point " + item.Name + " at the selection: " + message + ".");
                return;
            }
            Refresh(item.Name, false, null);
            if (!ok) ShowMessage("Couldn’t use the selection: " + message + ".");
        }

        // A row's Use beside Dst object, in the multi-selection: as the details' arrow, for that one redirector.
        private void OnUseSelectedFor(RedirectorItem item, AddonUtils.Destination shown) {
            IntPtr obj = SingleSelection(item, true);
            if (obj == IntPtr.Zero) return;
            Retarget(item, shown, obj, true);
        }

        // A row's Use beside Src object, in the multi-selection: renames that redirector to the selected object's
        // name, as the details' arrow types it into the name box.
        private void OnUseNameFor(RedirectorItem item) {
            IntPtr obj = SingleSelection(item, true);
            if (obj == IntPtr.Zero) return;
            string name = Genie.PathUtils.GetLeafName(Genie.ObjectUtils.GetPathName(obj));
            if (string.IsNullOrEmpty(name)) return;
            string old = item.Name, message;
            IntPtr r   = AddonUtils.Rename(item.Redirector, name, out message);
            if (r == IntPtr.Zero) {
                ShowMultiMessage("Couldn’t rename " + old + ": " + message + ".");
                return;
            }
            item.Redirector = r; // its new path keeps it selected
            item.Name       = name;
            RefreshKeepingSelection(false);
            if (message != null) ShowMultiMessage("Renamed " + old + " to " + name + ", but " + message + ".");
        }

        private void OnRepair() {
            RedirectorItem item = SelectedItem();
            if (item == null || item.IsPending) return;

            string message;
            bool   ok = AddonUtils.Repair(item.Redirector, out message);
            Refresh(item.Name, false, null); // a repair shows as its note: "Repaired (unsaved)."
            if (!ok) ShowMessage("Couldn’t repair: " + message + ".");
        }

        // Repair, for several: the ones it can fix, as Repair all did, keeping the selection. "3 repaired (unsaved), 1
        // still broken." Why each failed is in the log.
        private void OnRepairSelected() {
            CommitName(false);
            int repaired = 0, failed = 0;
            foreach (RedirectorItem item in Selected()) {
                if (!IsRepairable(item)) continue;
                string message;
                if (AddonUtils.Repair(item.Redirector, out message)) {
                    repaired++;
                } else {
                    failed++;
                    Genie.Log("Couldn't repair " + item.Name + ": " + message + ".");
                }
            }
            RefreshKeepingSelection(false);
            string summary = repaired + " repaired" + (repaired > 0 ? " (unsaved)" : "") +
                             (failed > 0 ? ", " + failed + " still broken" : "") + ".";
            ShowMultiMessage(summary);
        }

        // Broken, not created this session (a new one has nothing to go back to), and its destination loaded again.
        private static bool IsRepairable(RedirectorItem item) {
            if (item.IsPending || item.IsUnloaded || !Genie.RedirectorUtils.IsBroken(item.Redirector) ||
                Genie.ObjectUtils.IsNew(item.Redirector))
                return false;
            AddonUtils.Destination d = AddonUtils.GetDestination(item.Redirector);
            return d.IsResolved && d.IsObjectLoaded;
        }

        // The trash: the selection, one or several.
        private void OnTrash() {
            if (Selected().Count > 1)
                OnDeleteSelected();
            else
                OnDelete();
        }

        // Delete, for several: asks once, listing them, then deletes them all. The list shows what's left, with the
        // outcome as its hint: "4 deleted."
        private void OnDeleteSelected() {
            CommitName(false);
            List<RedirectorItem> doomed = Selected().FindAll(i => !i.IsUnloaded && !i.IsPending);
            if (doomed.Count == 0) return;
            if (!ConfirmDelete(doomed)) return;
            // One call for them all: the editor sweeps for references once.
            List<IntPtr> redirectors = new List<IntPtr>();
            foreach (RedirectorItem item in doomed) redirectors.Add(item.Redirector);
            int deleted = Math.Min(AddonUtils.Delete(redirectors), doomed.Count);
            int failed  = doomed.Count - deleted;
            Refresh();
            _emptyState.Hint = deleted + " deleted" +
                               (failed > 0 ? ", " + failed + " couldn’t be. See the log" : "") + ".";
        }

        // The delete prompt, for one or several: titled for what they are, not by name (a mesh's name over "Delete"
        // reads as deleting the mesh), each listed with its class at the right, past DialogListCap as "and N more".
        private bool ConfirmDelete(List<RedirectorItem> items) {
            bool one = items.Count == 1;
            List<UIElement> lines = new List<UIElement>();
            for (int i = 0; i < items.Count && i < DialogListCap; i++) {
                GenieTag kind = Genie.ControlUtils.CreateTag(RedirectorClass, Genie.ControlUtils.TagTone.Type);
                lines.Add(Genie.ControlUtils.CreateDialogItemTagged(items[i].Name, kind));
            }
            if (items.Count > DialogListCap) {
                TextBlock more  = Genie.ControlUtils.CreateFieldLabel("and " + (items.Count - DialogListCap) + " more");
                more.Margin     = new Thickness(0, 2, 0, 0);
                lines.Add(more);
            }
            string note = one ? "Objects that truly rely on this redirector would lose their reference once you " +
                                    "save this UPK. If it’s just left over from an accidental move, deleting it is " +
                                    "likely safe."
                              : "Objects that truly rely on these redirectors would lose their references once " +
                                    "you save this UPK. If they’re just left over from an accidental move, deleting " +
                                    "them is likely safe.";
            string question = one ? "Delete redirector?" : "Delete " + items.Count + " redirectors?";
            return Genie.ControlUtils.Confirm(this, question, "Delete", note, lines, Genie.ControlUtils.TagTone.Danger);
        }

        private void OnDelete() {
            RedirectorItem item = SelectedItem();
            if (item == null || item.IsUnloaded) return;
            if (item.IsPending) { // nothing in the editor yet: no prompt
                _pending.Remove(item);
                _nameDirty = false;
                Refresh();
                return;
            }
            CommitName(false);
            item = SelectedItem();
            if (item == null) return;
            if (!ConfirmDelete(new List<RedirectorItem> { item })) return;

            AddonUtils.Delete(item.Redirector);
            Refresh();
        }

        // == Layout ===================================================================================

        private const double MissingSize = 12;
        private const double MissingSlot = MissingSize + 5; // the icon and its gap before the name

        private static DockPanel NameCell(FrameworkElement missing, TextBlock name) {
            DockPanel line = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(missing);
            line.Children.Add(name);
            return line;
        }

        private static FrameworkElement MissingIcon() {
            FrameworkElement icon = Genie.ControlUtils.CreateWarningIcon(null, MissingSize);
            icon.Margin           = new Thickness(0, 0, MissingSlot - MissingSize, 0);
            icon.Visibility       = Visibility.Hidden;
            Genie.ControlUtils.SetToolTip(icon, "Not loaded");
            return icon;
        }

        // A table's heading, in the caption style of the window's own "PACKAGE".
        private static TextBlock TableCaption(string text) {
            TextBlock caption = Genie.ControlUtils.CreateCaption(text);
            caption.Margin    = new Thickness(0, 0, 0, 5);
            return caption;
        }

        private static TextBlock NameText() {
            TextBlock name    = Genie.ControlUtils.CreateMonoText();
            name.Margin       = new Thickness(0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            return name;
        }

        // Missing: red, with the triangle in its slot, as the list shows broken redirectors. keepSlot: hold the
        // triangle's room even when this name isn't missing. No name: placeholder, muted and in italics, as a field
        // text box's hint.
        private static void ShowName(TextBlock name, FrameworkElement missingIcon, string text, string placeholder,
                                     bool missing, bool keepSlot) {
            bool empty             = string.IsNullOrEmpty(text);
            name.Text              = empty ? placeholder : text;
            name.FontStyle         = empty ? FontStyles.Italic : FontStyles.Normal;
            name.Foreground        = empty     ? Genie.Theme.TextDisabled
                                     : missing ? Genie.Theme.Danger
                                               : Genie.Theme.TextPrimary;
            missingIcon.Visibility = missing    ? Visibility.Visible
                                     : keepSlot ? Visibility.Hidden
                                                : Visibility.Collapsed;
        }
    }

    // == Shortcut =====================================================================================
    // A button in the Content Browser's Packages tray, for the package the window would follow: the selected assets',
    // else the tree's (Genie.ContentBrowserUtils.FollowPackage), so it works in All Assets too. Its pill counts the
    // package's redirectors: red if any is broken, else green; none, no pill. A click opens the window, or brings it
    // to the front if it's open. Counts what the window lists, redirectors not loaded included. Shown per its "Show
    // tray shortcut" setting (GenieTray.AddShortcut).

    internal sealed class Shortcut {
        // A detour: the old spot at the bottom right, the path leaving it and turning up to the new place. The top
        // right is left clear for the pill.
        private const string Icon = "M 12.5,14 A 1.5,1.5 0 1 1 12.5,11 A 1.5,1.5 0 1 1 12.5,14 Z " +
                                    "M 11,12.5 L 7.5,12.5 A 3.5,3.5 0 0 1 4,9 L 4,2.5 " +
                                    "M 1.5,5 L 4,2.5 L 6.5,5";

        private static Shortcut _instance;

        private readonly GenieTrayButton                           _button;
        private readonly Genie.ContentBrowserUtils.PackageFollower _follower; // while the button is in the browser
        private readonly Action _queueUpdate; // once for a package change and its modification together

        public static void Create(Action open) {
            if (_instance == null) _instance = new Shortcut(open);
        }

        private Shortcut(Action open) {
            _queueUpdate = Genie.WorkUtils.Coalesce(delegate {
                if (_button.IsLoaded) Update();
            });
            _button               = Genie.ContentBrowserUtils.PackagesTray.AddShortcut(Icon, "Open Redirector Manager");
            _button.Click        += open;
            _button.ShownChanged += delegate {
                if (_button.IsShown) _queueUpdate(); // the count went stale while hidden
            };
            _follower                  = Genie.ContentBrowserUtils.FollowPackage(_button);
            _follower.PackageChanged  += _queueUpdate;
            _follower.PackageModified += _queueUpdate;
        }

        private void Update() {
            if (!_button.IsShown) return; // hidden: no counting; ShownChanged catches up
            string pkg   = _follower.Package;
            int    count = 0, broken = 0;
            if (pkg != null) {
                try {
                    foreach (Genie.RedirectorUtils.Found found in Genie.RedirectorUtils.FindInPackage(pkg)) {
                        count++;
                        if (Genie.RedirectorUtils.IsBroken(found.Redirector)) broken++;
                    }
                } catch (Exception ex) {
                    Genie.Log("Couldn't count the redirectors in " + pkg + ": " + ex.Message);
                }
            }
            _button.BadgeCount = count;
            _button.BadgeTone  = broken > 0 ? Genie.ControlUtils.TrayTone.Danger : Genie.ControlUtils.TrayTone.Success;
            _button.Tooltip    = "Open Redirector Manager" + (pkg == null ? "" : ": " + Describe(pkg, count, broken));
        }

        // "3 redirectors in Pkg, 1 broken"
        private static string Describe(string pkg, int count, int broken) {
            if (count == 0) return "no redirectors in " + pkg;
            return Genie.TextUtils.Count(count, "redirector") + " in " + pkg +
                   (broken > 0 ? ", " + broken + " broken" : "");
        }
    }
}
