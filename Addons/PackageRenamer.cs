// Package Renamer: an AddonGenie addon. Place in Binaries\Win64\Addons\
// Renames the selected package in memory: the package itself, groups and assets with it, cut loose from its file
// first, and the old name comes off the Content Browser's list. Loaded packages that use it follow; the user saves them
// and the renamed package. Nothing on disk is touched: the old file stays until the user removes it.
// Two ways in: a window from the Addons menu, and a button in the Content Browser's Packages tray.
// Editor access, the package rename and styling come from AddonGenie (Genie.*); the checks and the steps around the
// rename are this addon's own (AddonUtils).
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AddonGenie;

namespace PackageRenamer {
    public sealed class Addon : IGenieAddon {
        private GenieWindowSlot _window;

        public string Name {
            get { return "Package Renamer"; }
        }
        public string Tooltip {
            get { return "Rename the selected package"; }
        }
        public string Version {
            get { return "1.0"; }
        }
        public string Author {
            get { return "SurferJay"; }
        }

        public void Initialize() {
            _window = new GenieWindowSlot(CreateWindow);
            Shortcut.Create();
        }

        public void Invoke() {
            _window.Show();
        }

        private GenieWindow CreateWindow() {
            GenieWindow w        = Genie.WindowUtils.CreateWindow("Package Renamer", 320, 150);
            w.MinWidth           = 300;
            w.FitHeightToContent = true; // collapses to the header while no package is selected
            w.Content            = new RenamePanel(w);
            return w;
        }
    }

    // == Rename Logic =================================================================================

    internal static class AddonUtils {
        public sealed class Result {
            public bool   IsDone;  // the package has the new name
            public string Problem; // why nothing was renamed, or null
        }

        /// <summary>Why this package can't be renamed, or null. Read from its file once it's loaded.</summary>
        public static string CheckPackage(string packageName) {
            string file = Genie.LinkerUtils.GetFileName(packageName);
            if (file != null) {
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".umap", StringComparison.OrdinalIgnoreCase))
                    return "Maps can’t be renamed here.";
                if (!string.Equals(extension, ".upk", StringComparison.OrdinalIgnoreCase))
                    return "Only .upk packages can be renamed here.";
                return null;
            }
            // In memory with no file, such as one just renamed: nothing to check until it's saved.
            return null;
        }

        /// <summary>
        /// True for a package the SDK ships: its file under XComGame\Content or Engine\Content, but not
        /// Content\Mods (SDK Mod), as Dependency Viewer decides it. False when its file isn't known.
        /// </summary>
        public static bool IsStock(string packageName) {
            string file = Genie.LinkerUtils.GetFileName(packageName);
            if (file == null) return false;
            string sdk = Genie.GetSdkFolder();
            if (Genie.FileUtils.IsUnder(file, Path.Combine(sdk, @"XComGame\Content\Mods"))) return false;
            return Genie.FileUtils.IsUnder(file, Path.Combine(sdk, @"XComGame\Content")) ||
                   Genie.FileUtils.IsUnder(file, Path.Combine(sdk, @"Engine\Content"));
        }

        /// <summary>Why newName can't be used for this package, or null.</summary>
        public static string CheckName(string packageName, string newName) {
            if (string.IsNullOrEmpty(newName) ||
                string.Equals(newName, packageName, StringComparison.OrdinalIgnoreCase))
                return "Type a new name.";
            string problem = Genie.PackageUtils.CheckName(newName);
            if (problem != null) return problem;
            // In memory, or a file where the editor looks: the tree lists every package it knows.
            if (Genie.ObjectUtils.FindPackage(newName) != IntPtr.Zero ||
                Genie.ContentBrowserUtils.IsPackageListed(newName))
                return "A package named " + newName + " already exists.";
            return null;
        }

        /// <summary>Loaded packages that import objects from this one, by name.</summary>
        public static List<string> FindLoadedDependents(string packageName) {
            List<string> found = new List<string>();
            foreach (string name in Genie.ContentBrowserUtils.GetListedPackages()) {
                if (string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase)) continue;
                // Only a package loaded from its file has import tables; others have no linker.
                IntPtr linker = Genie.LinkerUtils.GetLinker(name);
                if (linker == IntPtr.Zero) continue;
                if (Genie.LinkerUtils.GetImportedPackages(linker)
                        .Exists(p => string.Equals(p, packageName, StringComparison.OrdinalIgnoreCase)))
                    found.Add(name);
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        /// <summary>
        /// Renames a package in memory: fully loads it, so nothing is left in the file when it's cut loose, renames
        /// it, and marks the dependents unsaved so saving them writes the new paths. Nothing is saved or deleted on
        /// disk.
        /// </summary>
        public static Result Rename(string oldName, string newName, List<string> dependents) {
            Result r = new Result();
            if (!Genie.ContentBrowserUtils.FullyLoadPackage(oldName))
                return Fail(r, "Couldn’t fully load " + oldName + ".");
            string problem = CheckPackage(oldName); // its file is known now
            if (problem != null) return Fail(r, problem);
            if (!Genie.PackageUtils.RenamePackage(oldName, newName))
                return Fail(r, "The editor didn’t rename it. See the log.");
            r.IsDone = true;
            Genie.Log("Renamed " + oldName + " to " + newName + " in memory.");
            foreach (string dep in dependents) Genie.ObjectUtils.MarkPackageDirty(Genie.ObjectUtils.FindPackage(dep));
            return r;
        }

        private static Result Fail(Result r, string problem) {
            r.Problem = problem;
            Genie.Log("Rename failed: " + problem);
            return r;
        }

        /// <summary>
        /// Renames the form's package as asked and shows the outcome on the form, which moves to the new package: the
        /// old name comes off the Content Browser's list and the new one is selected. True if all of that worked; false
        /// leaves a failure or warning on the form to read.
        /// </summary>
        public static bool RenameFrom(RenameForm form) {
            string oldName = form.Package, newName = form.NewName;
            Result r       = Rename(oldName, newName, form.Dependents);
            if (!r.IsDone) {
                form.ShowResult(oldName, r.Problem, Genie.ControlUtils.TagTone.Danger);
                return false;
            }
            form.SetPackage(newName);
            // Both now, before the browser's rebuild, whose replacement for a node takes over its selection. A stock
            // package's file is listed again by that rebuild; that's left to the editor.
            bool removed = !Genie.ContentBrowserUtils.IsPackageListed(oldName) ||
                           Genie.ContentBrowserUtils.RemovePackageFromTree(oldName);
            if (!removed) Genie.Log(oldName + " is still listed in the Content Browser.");
            form.ShowResult(newName,
                            removed ? "Renamed. Save the updated packages in the Content Browser."
                                    : "Renamed. " + oldName + " is still listed in the Content Browser.",
                            removed ? Genie.ControlUtils.TagTone.Success : Genie.ControlUtils.TagTone.Warning);
            Genie.ContentBrowserUtils.SelectPackage(newName);
            return removed;
        }
    }

    // == Rename Form ==================================================================================
    // The new name, the dependents check and Rename: the same in the window and the shortcut's popover.

    internal sealed class RenameForm : UserControl {
        private const string TickTip = "Load every package that uses it, then tick the box.";

        private readonly TextBox        _name;
        private readonly Button         _rename;
        private readonly TextBlock      _problem;
        private readonly GenieParagraph _result; // a check (success only), then the outcome
        private readonly CheckBox       _allLoaded;
        private readonly GenieCountPill _loadedPill;

        private string       _package;
        private string       _packageProblem; // why the package can't be renamed, or null
        private string       _resultFor;      // the result shows while the form is on this package
        private List<string> _dependents = new List<string>();

        /// <summary>Raised when Rename is clicked, or Enter pressed, with a name it can take.</summary>
        public event Action RenameRequested;

        internal RenameForm() {
            StackPanel body = new StackPanel();

            DockPanel nameRow = new DockPanel();
            _rename           = Genie.ControlUtils.CreateButton("Rename", Genie.ControlUtils.ButtonTone.Danger);
            _rename.Margin    = new Thickness(Genie.Theme.Spacing, 0, 0, 0);
            _rename.Click    += delegate {
                Request();
            };
            DockPanel.SetDock(_rename, Dock.Right);
            nameRow.Children.Add(_rename);
            _name              = Genie.ControlUtils.CreateTextBox(null, true, "The package’s new name");
            _name.TextChanged += delegate {
                _resultFor = null; // a new attempt
                Update();
            };
            _name.KeyDown += delegate(object s, KeyEventArgs e) {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Request();
            };
            nameRow.Children.Add(_name);
            body.Children.Add(nameRow);

            _problem            = Genie.ControlUtils.CreateNoteText();
            _problem.Foreground = Genie.Theme.Danger;
            _problem.Margin     = new Thickness(0, 4, 0, 0);
            body.Children.Add(_problem);

            // The last rename's outcome, above the box: "Renamed. Save the updated packages in the Content Browser."
            _result          = Genie.ControlUtils.CreateParagraph();
            _result.FontSize = Genie.Theme.FontSizeLabel;
            _result.Margin   = new Thickness(0, 6, 0, 0);
            body.Children.Add(_result);

            // Only loaded packages can be found, so the count is of those, never a total: the user vouches for the
            // rest.
            StackPanel label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = "All packages that use it are loaded" });
            _loadedPill        = Genie.ControlUtils.CreateCountPill("");
            _loadedPill.Margin = new Thickness(7, 0, 0, 0);
            label.Children.Add(_loadedPill);
            _allLoaded            = Genie.ControlUtils.CreateCheckBox(null);
            _allLoaded.Content    = label;
            _allLoaded.Margin     = new Thickness(0, 7, 0, 0);
            _allLoaded.Checked   += delegate {
                Update();
            };
            _allLoaded.Unchecked += delegate {
                Update();
            };
            body.Children.Add(_allLoaded);

            Content = body;
            Update();
        }

        public string Package {
            get { return _package; }
        }

        public string NewName {
            get { return _name.Text.Trim(); }
        }

        /// <summary>The loaded packages that use the package, as last looked up.</summary>
        public List<string> Dependents {
            get { return _dependents; }
        }

        /// <summary>For anchoring the prompt.</summary>
        public Button RenameButton {
            get { return _rename; }
        }

        /// <summary>
        /// Shows this package. Another package starts over: its own name in the box, the box unticked (it vouches for
        /// one package only), and the last result gone unless it was about this package. False if it was already
        /// showing it: nothing is looked up again (Refresh does).
        /// </summary>
        public bool SetPackage(string package) {
            if (string.Equals(package, _package, StringComparison.OrdinalIgnoreCase)) return false;
            _package             = package;
            string keepResult    = _resultFor; // typing the name clears it
            _name.Text           = package ?? "";
            _resultFor           = keepResult;
            _allLoaded.IsChecked = false;
            Refresh();
            return true;
        }

        /// <summary>Looks again at the package and the loaded packages that use it.</summary>
        public void Refresh() {
            _packageProblem  = _package == null ? null : AddonUtils.CheckPackage(_package);
            _dependents      = _package == null ? new List<string>() : AddonUtils.FindLoadedDependents(_package);
            _loadedPill.Text = _dependents.Count + " loaded";
            Genie.ControlUtils.SetToolTip(_allLoaded, _dependents.Count > 0
                                                          ? "Loaded now: " + string.Join(", ", _dependents.ToArray())
                                                          : "No loaded package uses it.");
            Update();
        }

        /// <summary>An outcome under the form, kept while the form shows forPackage.</summary>
        public void ShowResult(string forPackage, string text, Genie.ControlUtils.TagTone tone) {
            bool success       = tone == Genie.ControlUtils.TagTone.Success;
            _resultFor         = forPackage;
            _result.Foreground = success                                     ? Genie.Theme.SuccessTagText
                                 : tone == Genie.ControlUtils.TagTone.Danger ? Genie.Theme.Danger
                                                                             : Genie.Theme.WarningTagText;
            _result.SetParts(success ? Genie.ControlUtils.CreateCheckIcon() : null, text);
            Update();
        }

        /// <summary>Puts the cursor in the name, all of it selected, so typing replaces it.</summary>
        public void FocusName() {
            _name.Focus();
            _name.SelectAll();
        }

        // Rename is always red, as the prompt's: it renames. Disabled, and dimmed, until the box is ticked and the name
        // can be used. The name's problem shows once it differs from the package's own.
        private void Update() {
            bool   ticked      = _allLoaded.IsChecked == true;
            bool   unchanged   = string.Equals(NewName, _package, StringComparison.OrdinalIgnoreCase);
            string nameProblem = _package == null ? null : AddonUtils.CheckName(_package, NewName);
            string shown       = _packageProblem ?? (unchanged ? null : nameProblem);
            _problem.Text        = shown ?? "";
            _problem.Visibility  = shown != null ? Visibility.Visible : Visibility.Collapsed;
            _name.IsEnabled      = _packageProblem == null;
            _allLoaded.IsEnabled = _packageProblem == null;

            _rename.IsEnabled = ticked && _package != null && _packageProblem == null && nameProblem == null;
            string tip        = _package == null ? null
                                : _packageProblem ?? (!ticked ? TickTip
                                                              : nameProblem ?? "Rename " + _package + " to " + NewName);
            Genie.ControlUtils.SetToolTip(_rename, tip);

            bool showResult = _resultFor != null &&
                              string.Equals(_resultFor, _package, StringComparison.OrdinalIgnoreCase);
            _result.Visibility = showResult ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Request() {
            if (_rename.IsEnabled && RenameRequested != null) RenameRequested();
        }
    }

    // == Prompt =======================================================================================
    // What the rename does: old name → new, the dependents map (the package under its new name, then the loaded
    // packages that use it), and what Package Renamer does and doesn't do. Rename is red; Cancel has the focus.

    internal sealed class ConfirmCard : UserControl {
        private readonly Button _cancel;

        public event Action Confirmed;
        public event Action Cancelled;

        internal ConfirmCard(string oldName, string newName, List<string> dependents, bool stock) {
            MinWidth = 400;
            MaxWidth = 520;
            string original = oldName + ".upk", renamed = newName + ".upk";
            StackPanel body = new StackPanel();

            // (i) Rename package?
            StackPanel title = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement icon =
                Genie.ControlUtils.CreateDialogIcon(Genie.ControlUtils.TagTone.Danger,
                                                    Genie.ControlUtils.DialogSymbol.Info, 20);
            icon.Margin            = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(icon);
            title.Children.Add(new TextBlock {
                Text              = "Rename package?",
                FontSize          = Genie.Theme.FontSizeValue + 1.5,
                FontWeight        = FontWeights.SemiBold,
                Foreground        = Genie.Theme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            });
            body.Children.Add(title);

            // Old.upk → New.upk, one line; a long pair is cut at the end and shown whole on hover.
            TextBlock change = Genie.ControlUtils.CreateMonoText("", Genie.Theme.FontSizeValue);
            change.Inlines.Clear();
            change.Inlines.Add(new System.Windows.Documents.Run(original) { Foreground = Genie.Theme.TextSecondary });
            change.Inlines.Add(
                new System.Windows.Documents.Run("  →  ") { Foreground = Genie.Theme.TextSecondary });
            change.Inlines.Add(new System.Windows.Documents.Run(renamed) { FontWeight = FontWeights.Bold });
            change.Margin       = new Thickness(0, 8, 0, 0);
            change.TextTrimming = TextTrimming.CharacterEllipsis;
            Genie.ControlUtils.SetToolTip(change, original + " → " + renamed);
            body.Children.Add(change);

            // DEPENDENTS MAP: the package under its new name, then the loaded packages that use it.
            GenieTreeBox map = Genie.ControlUtils.CreateTreeBox("Dependents map");
            map.Margin       = new Thickness(0, 8, 0, 0);
            map.EmptyText    = "No loaded package uses it.";
            map.SetRoot(renamed, tag: Genie.ControlUtils.CreateTag("gets renamed", Genie.ControlUtils.TagTone.Danger,
                                                                   "Was " + original,
                                                                   size: Genie.ControlUtils.TagSize.Regular));
            foreach (string dependent in dependents)
                map.AddChild(dependent + ".upk",
                             tag: Genie.ControlUtils.CreateTag("references update", Genie.ControlUtils.TagTone.Neutral,
                                                               "Save it to keep the change"));
            body.Children.Add(map);

            // What's true of it, each with more on hover. "original UPK" names the old package on hover.
            StackPanel lines = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            lines.Children.Add(Line(Genie.ControlUtils.CreateCheckIcon(null, 13),
                                    "Any left unloaded keep the old name and break.",
                                    Words("You verified all dependents of the "), Term(original),
                                    Words(" are loaded.")));
            lines.Children.Add(Line(Disk(), "After a rename, you will need to save the renamed package and its " +
                                                "dependents. To undo, relaunch the editor without saving.",
                                    Words("Package Renamer never saves anything to disk.")));
            lines.Children.Add(Line(Disk(), "Delete the original UPK yourself if you want it gone.",
                                    Words("Package Renamer never deletes anything from disk.")));
            if (stock)
                lines.Children.Add(Line(Genie.ControlUtils.CreateInfoIcon(Genie.Theme.TextSecondary, 13),
                                        "Their files stay cached in the editor, so it automatically lists them again.",
                                        Words("Original SDK UPKs will reappear after the next refresh.")));
            body.Children.Add(lines);

            Button rename  = Genie.ControlUtils.CreateButton("Rename", Genie.ControlUtils.ButtonTone.Danger);
            rename.Click  += delegate {
                if (Confirmed != null) Confirmed();
            };
            _cancel        = Genie.ControlUtils.CreateButton("Cancel");
            _cancel.Margin = new Thickness(0);
            _cancel.Click += delegate {
                if (Cancelled != null) Cancelled();
            };
            StackPanel buttons          = Genie.ControlUtils.CreateButtonRow(rename, _cancel);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin              = new Thickness(0, 10, 0, 0);
            body.Children.Add(buttons);

            Content = body;
        }

        /// <summary>Cancel takes the focus, so Enter backs out.</summary>
        public void FocusCancel() {
            _cancel.Focus();
        }

        // Never saves, never deletes: green, as promises.
        private static FrameworkElement Disk() {
            return Genie.ControlUtils.CreateSaveIcon(Genie.Theme.SuccessTagText, 13);
        }

        // [icon] words..., kept on one line; tooltip says more.
        private static StackPanel Line(FrameworkElement icon, string tooltip, params UIElement[] parts) {
            StackPanel line =
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            icon.Margin     = new Thickness(0, 0, 6, 0);
            line.Children.Add(icon);
            foreach (UIElement p in parts) line.Children.Add(p);
            Genie.ControlUtils.SetToolTip(line, tooltip);
            return line;
        }

        private static TextBlock Words(string text) {
            return new TextBlock {
                Text              = text,
                FontSize          = Genie.Theme.FontSizeBody,
                Foreground        = Genie.Theme.NeutralTagText,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static TextBlock Term(string original) {
            TextBlock term  = Genie.ControlUtils.CreateTermText("original UPK", original, Genie.Theme.FontSizeBody);
            term.Foreground = Genie.Theme.NeutralTagText;
            return term;
        }
    }

    // == Window =======================================================================================

    internal sealed class RenamePanel : UserControl {
        private readonly GenieHeader                               _header;
        private readonly Genie.ContentBrowserUtils.PackageFollower _follower;
        private readonly RenameForm                                _form;
        private readonly GeniePopover                              _prompt;

        internal RenamePanel(GenieWindow window) {
            StackPanel root = new StackPanel();

            _header                          = Genie.ControlUtils.CreatePanelHeader(window, "PACKAGE");
            _header.ShowFileExtension        = true;
            _header.Controls.RefreshToolTip  = "Look for the packages that use it again.";
            _header.Controls.Refresh        += delegate {
                _follower.Update();
                if (!_form.SetPackage(_follower.Package)) _form.Refresh();
            };
            _header.Controls.Settings.AddTrayShortcutItem();
            root.Children.Add(_header);

            _form = new RenameForm {
                Margin     = new Thickness(12, 10, 12, 10),
                Visibility = Visibility.Collapsed, // until the follower names a package
            };
            _form.RenameRequested += Ask;
            root.Children.Add(_form);
            // Room for the version label in the corner, which would cover the last line.
            root.Children.Add(new Border { Height = window.AddonInfoHeight });
            Content = root;

            _prompt         = Genie.ControlUtils.CreatePopover();
            _prompt.Opened += delegate {
                ConfirmCard card = _prompt.Content as ConfirmCard;
                if (card != null) card.FocusCancel();
            };

            _follower                 = Genie.ContentBrowserUtils.FollowPackage(_header);
            _follower.PackageChanged += delegate {
                _prompt.Close();
                _form.SetPackage(_follower.Package);
                _form.Visibility = _follower.Package != null ? Visibility.Visible : Visibility.Collapsed;
            };
            _follower.PackageModified += delegate {
                _form.Refresh(); // loaded, unloaded, modified or saved: its dependents may differ
            };
        }

        // The prompt drops from the Rename button.
        private void Ask() {
            ConfirmCard card = new ConfirmCard(_form.Package, _form.NewName, _form.Dependents,
                                               AddonUtils.IsStock(_form.Package));
            card.Confirmed  += delegate {
                _prompt.Close();
                AddonUtils.RenameFrom(_form);
                if (_follower.IsPinned) _header.Package = _form.Package; // a pinned view follows its package
            };
            card.Cancelled += delegate {
                _prompt.Close();
            };
            _prompt.Content = card;
            _prompt.Show(_form.RenameButton);
        }
    }

    // == Shortcut =====================================================================================
    // A Rename button in the Content Browser's Packages tray. It opens the form under itself for the package
    // selected in the tree; Rename turns the popover into the prompt. Shown per its "Show tray shortcut" setting
    // (GenieTray.AddShortcut).

    internal sealed class Shortcut {
        // Rename: a text box with a letter and a caret, in a 16px box.
        private const string Icon = "M 2.2,3 L 13.8,3 A 1.2,1.2 0 0 1 15,4.2 L 15,11.8 A 1.2,1.2 0 0 1 13.8,13 " +
                                    "L 2.2,13 A 1.2,1.2 0 0 1 1,11.8 L 1,4.2 A 1.2,1.2 0 0 1 2.2,3 Z " +
                                    "M 3,10.5 L 5.5,5.5 L 8,10.5 M 4,8.5 L 7,8.5 " +
                                    "M 11,1.5 L 11,14.5 M 9.5,1.5 L 12.5,1.5 M 9.5,14.5 L 12.5,14.5";
        private const string ButtonTip = "Rename the selected package";
        private const double CardWidth = 300;

        private static Shortcut _instance;

        private readonly GenieTrayButton                           _button;
        private readonly GeniePopover                              _popover;
        private readonly RenameForm                                _form;
        private readonly Genie.ContentBrowserUtils.PackageFollower _follower; // while the button is in the browser

        public static void Create() {
            if (_instance == null) _instance = new Shortcut();
        }

        private Shortcut() {
            _button               = Genie.ContentBrowserUtils.PackagesTray.AddShortcut(Icon, ButtonTip);
            _button.Click        += Toggle;
            _button.ShownChanged += delegate {
                if (!_button.IsShown) _popover.Close();
            };
            _form                  = new RenameForm { Width = CardWidth };
            _form.RenameRequested += Ask;
            _popover               = Genie.ControlUtils.CreatePopover();
            _popover.Opened       += delegate {
                ConfirmCard card = _popover.Content as ConfirmCard;
                if (card != null)
                    card.FocusCancel();
                else
                    _form.FocusName();
            };
            _popover.Closed += delegate {
                _button.IsActive = false;
            };
            // The tree's package, not the selected assets': the button renames "the selected package", so in All
            // Assets there's none. With several selected, the first.
            _follower                 = Genie.ContentBrowserUtils.FollowPackage(_button);
            _follower.Source          = Genie.ContentBrowserUtils.PackageSource.Tree;
            _follower.MultiPackage    = Genie.ContentBrowserUtils.MultiPackage.First;
            _follower.PackageChanged += OnPackageChanged;
            Genie.ControlUtils.WhileLoaded(_button, null, delegate {
                _popover.Close();
            });
        }

        // A new package while the popover is open shows the form for it; none closes it.
        private void OnPackageChanged() {
            string pkg        = _follower.Package;
            _button.Available = pkg != null;
            _button.Tooltip   = pkg != null ? "Rename " + pkg : "Select a package to rename it";
            if (!_popover.IsOpen) return;
            if (pkg == null) {
                _popover.Close();
                return;
            }
            _form.SetPackage(pkg);
            _popover.Content = _form;
        }

        private void Toggle() {
            if (_popover.IsOpen) {
                _popover.Close();
                return;
            }
            string pkg = _follower.Package;
            if (pkg == null) return;
            // Looked at again: packages may have loaded or changed since it was last open.
            if (!_form.SetPackage(pkg)) _form.Refresh();
            _popover.Content = _form;
            _popover.Show(_button);
            _button.IsActive = _popover.IsOpen;
        }

        private void Ask() {
            ConfirmCard card = new ConfirmCard(_form.Package, _form.NewName, _form.Dependents,
                                               AddonUtils.IsStock(_form.Package));
            card.Confirmed  += delegate {
                _popover.Content = _form;
                // The browser then selects the new package, and the form follows. Done, it closes; a failure or
                // warning stays on the form to read.
                if (AddonUtils.RenameFrom(_form))
                    _popover.Close();
                else
                    _form.FocusName();
            };
            card.Cancelled += delegate {
                _popover.Content = _form;
                _form.FocusName();
            };
            _popover.Content = card;
            card.FocusCancel();
        }
    }
}
