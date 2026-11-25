// Package Loader: an AddonGenie addon. Place in Binaries\Win64\Addons\
// Gets the editor ready for a kind of work: jobs of steps that load packages and run batch files, by hand or
// unattended at startup. Editor access and styling come from AddonGenie (Genie.*).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AddonGenie;

namespace PackageLoader {
    public sealed class Addon : IGenieAddon {
        public string Name {
            get { return "Package Loader"; }
        }
        public string Tooltip {
            get { return "Load packages and run scripts, by hand or at startup"; }
        }
        public string Version {
            get { return "1.0"; }
        }
        public string Author {
            get { return "SurferJay"; }
        }

        public void Initialize() {
            Genie.ConfigUtils.BackUpSettings(); // before anything here can change the settings
            Store.Load();
            RunLog.Begin();
            Windows.Initialize();
            RunningWindow.Initialize();
            Startup.Initialize();
            Genie.MenuUtils.AddCheck("Enable Startup Jobs", "Run startup jobs when the editor opens",
                                     Store.StartupJobs);
            Genie.MenuUtils.AddCheck("Show the Launcher", "A pill over the editor to run jobs", Store.ShowLauncher);
        }

        public void Invoke() {
            Windows.ShowMain();
        }
    }

    // The addon's windows, one of each, opened from anywhere in it.
    internal static class Windows {
        private static GenieWindowSlot _main, _history, _restore, _help;

        public static void Initialize() {
            _main    = new GenieWindowSlot(delegate {
                GenieWindow w = Genie.WindowUtils.CreateWindow("Package Loader", 1120, 620);
                w.MinWidth    = 860;
                w.MinHeight   = 420;
                w.Content     = new MainPanel(w);
                return w;
            });
            _history = new GenieWindowSlot(delegate {
                GenieWindow w = Genie.WindowUtils.CreateWindow("Package Loader: run history", 900, 560);
                w.MinWidth    = 700;
                w.MinHeight   = 380;
                w.Content     = new HistoryPanel(w);
                return w;
            });
            _restore = new GenieWindowSlot(RestoreWindow.Create);
            _help    = new GenieWindowSlot(HelpWindow.Create);
        }

        public static void ShowHelp() {
            _help.Show();
        }

        public static void ShowRestore() {
            _restore.Show();
        }

        /// <summary>Opens the main window, at a job if given.</summary>
        public static void ShowMain(Job job = null) {
            _main.Show();
            MainPanel panel = _main.Current != null ? _main.Current.Content as MainPanel : null;
            if (panel != null && job != null) panel.Select(job);
        }

        /// <summary>
        /// Opens the run history at a run (null: the latest), with the step holding focus (a step's or substep's
        /// result) open and in view.
        /// </summary>
        public static void ShowHistory(RunResult run, bool errorsOnly = false, object focus = null) {
            _history.Show();
            HistoryPanel panel = _history.Current != null ? _history.Current.Content as HistoryPanel : null;
            if (panel != null) panel.Open(run, errorsOnly, focus);
        }
    }

    // == Model ========================================================================================

    internal enum SubstepKind { Package, Script }

    // Saved by name. What a package substep's load errors do.
    internal enum LoadErrorMode { Fail, IgnoreListed, IgnoreAll }

    internal sealed class Variable {
        public string Name = "", Value = "";
    }

    internal sealed class Substep {
        public string      Id      = Ids.New();
        public bool        Enabled = true;
        public SubstepKind Kind;
        public string      File = "";

        // Scripts only. Text as typed: Timeout and SuccessCodes are read when run, so a bad value stays visible.
        public string         Arguments = "", WorkingFolder = "", Timeout = "", SuccessCodes = "", Input = "";
        public List<Variable> Variables = new List<Variable>();

        // Packages only. The list outlives Fail and IgnoreAll, so trying another mode doesn't lose it. Its entries are
        // LoadErrorIdentity's, or typed in the panel: IsSameLoadError matches either way.
        public LoadErrorMode LoadErrors;
        public List<string>  IgnoredErrors = new List<string>();

        /// <summary>Anything set in the gear's panel, for the gear's dot.</summary>
        public bool HasSettings {
            get {
                if (Kind == SubstepKind.Package) return LoadErrors != LoadErrorMode.Fail;
                return Arguments.Length > 0 || WorkingFolder.Length > 0 || Timeout.Length > 0 ||
                       SuccessCodes.Length > 0 || Input.Length > 0 || Variables.Count > 0;
            }
        }

        public bool Ignores(string loadError) {
            if (LoadErrors == LoadErrorMode.IgnoreAll) return true;
            return LoadErrors == LoadErrorMode.IgnoreListed && IsListed(loadError);
        }

        public void Ignore(IEnumerable<string> loadErrors) {
            foreach (string e in loadErrors) {
                if (!IsListed(e)) IgnoredErrors.Add(Genie.PackageUtils.LoadErrorIdentity(e));
            }
            if (LoadErrors == LoadErrorMode.Fail) LoadErrors = LoadErrorMode.IgnoreListed;
        }

        /// <summary>Takes it off the list; an emptied list goes back to failing.</summary>
        public void StopIgnoring(string loadError) {
            IgnoredErrors.RemoveAll(i => Genie.PackageUtils.IsSameLoadError(i, loadError));
            FailIfNoneListed();
        }

        /// <summary>Drops rows left blank in the package panel.</summary>
        public void TidyIgnored() {
            IgnoredErrors.RemoveAll(i => i.Trim().Length == 0);
            FailIfNoneListed();
        }

        public void FailIfNoneListed() {
            if (IgnoredErrors.Count == 0 && LoadErrors == LoadErrorMode.IgnoreListed) LoadErrors = LoadErrorMode.Fail;
        }

        private bool IsListed(string loadError) {
            return IgnoredErrors.Exists(i => i.Trim().Length > 0 && Genie.PackageUtils.IsSameLoadError(i, loadError));
        }

        public Substep Copy() {
            Substep c   = (Substep)MemberwiseClone();
            c.Id        = Ids.New();
            c.Variables = new List<Variable>();
            foreach (Variable v in Variables) c.Variables.Add(new Variable { Name = v.Name, Value = v.Value });
            c.IgnoredErrors = new List<string>(IgnoredErrors);
            return c;
        }
    }

    internal sealed class Step {
        public string        Id      = Ids.New();
        public string        Name    = "";
        public bool          Enabled = true;
        public string        TemplateId; // null: its own substeps
        public List<Substep> Substeps = new List<Substep>();
    }

    internal sealed class Job {
        public string Id   = Ids.New();
        public string Name = "";
        public bool   RunOnStartup, StopAtFirstFailure;
        public int    Order = 1;
        public DateTime? LastRun, Updated; // Updated: its definition's last change (Store.Save)
        public Template   RunsTemplate;    // set on a run's stand-in for a template (Runner.Start); never saved
        public List<Step> Steps = new List<Step>();

        public int SubstepCount {
            get {
                int n = 0;
                foreach (Step s in Steps) n += Store.SubstepsOf(s).Count;
                return n;
            }
        }
    }

    internal sealed class Template {
        public string        Id       = Ids.New();
        public string        Name     = "";
        public DateTime? LastRun, Updated; // as a job's
        public List<Substep> Substeps = new List<Substep>();
    }

    internal static class Ids {
        public static string New() {
            return Guid.NewGuid().ToString("N").Substring(0, 10);
        }
    }

    // == Saving =======================================================================================
    // One section per job ("Job.<id>") and per template ("Template.<id>"), rewritten whole on every edit. Steps and
    // substeps are a line each, ConfigUtils.JoinFields' fields: "Step 2" = id|on|templateId|name, "Step 2.1" = a
    // substep.

    internal static class Codec {
        public static string Encode(Substep s) {
            List<string> f = new List<string> { s.Id, Bool(s.Enabled), s.Kind.ToString(), s.File };
            if (s.Kind == SubstepKind.Script) {
                f.AddRange(new[] { s.Arguments, s.WorkingFolder, s.Timeout, s.SuccessCodes, s.Input });
                foreach (Variable v in s.Variables) {
                    f.Add(v.Name);
                    f.Add(v.Value);
                }
            } else if (s.LoadErrors != LoadErrorMode.Fail || s.IgnoredErrors.Count > 0) {
                f.Add(s.LoadErrors.ToString());
                f.AddRange(s.IgnoredErrors);
            }
            return Genie.ConfigUtils.JoinFields(f.ToArray());
        }

        public static Substep DecodeSubstep(string line) {
            List<string> f = Genie.ConfigUtils.SplitFields(line);
            if (f.Count < 4) return null;
            Substep s = new Substep { Id = f[0], Enabled = f[1] != "off", File = f[3] };
            s.Kind    = f[2] == "Script" ? SubstepKind.Script : SubstepKind.Package;
            if (s.Kind == SubstepKind.Script && f.Count >= 9) {
                s.Arguments     = f[4];
                s.WorkingFolder = f[5];
                s.Timeout       = f[6];
                s.SuccessCodes  = f[7];
                s.Input         = f[8];
                for (int i = 9; i + 1 < f.Count; i += 2)
                    s.Variables.Add(new Variable { Name = f[i], Value = f[i + 1] });
            } else if (s.Kind == SubstepKind.Package && f.Count >= 5) {
                Enum.TryParse(f[4], out s.LoadErrors);
                s.IgnoredErrors.AddRange(f.GetRange(5, f.Count - 5));
            }
            return s;
        }

        public static List<KeyValuePair<string, string>> Entries(Job j) {
            List<KeyValuePair<string, string>> e = new List<KeyValuePair<string, string>>();
            e.Add(Pair("Name", j.Name));
            e.Add(Pair("RunOnStartup", Bool(j.RunOnStartup)));
            e.Add(Pair("Order", j.Order.ToString(CultureInfo.InvariantCulture)));
            e.Add(Pair("StopAtFirstFailure", Bool(j.StopAtFirstFailure)));
            AddTimes(e, j.LastRun, j.Updated);
            for (int i = 0; i < j.Steps.Count; i++) {
                Step s = j.Steps[i];
                e.Add(Pair("Step " + (i + 1),
                           Genie.ConfigUtils.JoinFields(s.Id, Bool(s.Enabled), s.TemplateId ?? "", s.Name)));
                for (int k = 0; k < s.Substeps.Count; k++)
                    e.Add(Pair("Step " + (i + 1) + "." + (k + 1), Encode(s.Substeps[k])));
            }
            return e;
        }

        public static List<KeyValuePair<string, string>> Entries(Template t) {
            List<KeyValuePair<string, string>> e = new List<KeyValuePair<string, string>>();
            e.Add(Pair("Name", t.Name));
            AddTimes(e, t.LastRun, t.Updated);
            for (int k = 0; k < t.Substeps.Count; k++) e.Add(Pair("Substep " + (k + 1), Encode(t.Substeps[k])));
            return e;
        }

        private static void AddTimes(List<KeyValuePair<string, string>> e, DateTime? lastRun, DateTime? updated) {
            if (lastRun.HasValue) e.Add(Pair("LastRun", lastRun.Value.ToString("s", CultureInfo.InvariantCulture)));
            if (updated.HasValue) e.Add(Pair("Updated", updated.Value.ToString("s", CultureInfo.InvariantCulture)));
        }

        private static DateTime? ReadTime(string text) {
            DateTime t;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t
                                                                                                     : (DateTime?)null;
        }

        /// <summary>All that's saved but the two times: what has to differ for a save to count as an edit.</summary>
        public static string Definition(List<KeyValuePair<string, string>> entries) {
            StringBuilder b = new StringBuilder();
            foreach (KeyValuePair<string, string> e in entries) {
                if (e.Key != "LastRun" && e.Key != "Updated") b.Append(e.Key).Append('=').Append(e.Value).Append('\n');
            }
            return b.ToString();
        }

        // Steps and substeps go by the numbers in their keys, not the lines' order, so a section reordered by hand
        // or by another tool still reads as saved. Unnumbered or unreadable lines are skipped.
        private static readonly Regex StepKey    = new Regex(@"^Step (\d+)(?:\.(\d+))?$");
        private static readonly Regex SubstepKey = new Regex(@"^Substep (\d+)$");

        public static Job DecodeJob(string id, KeyValuePair<string, string>[] entries) {
            Job                                                   j     = new Job { Id = id };
            SortedDictionary<int, Step>                           steps = new SortedDictionary<int, Step>();
            SortedDictionary<int, SortedDictionary<int, Substep>> substeps =
                new SortedDictionary<int, SortedDictionary<int, Substep>>();
            foreach (KeyValuePair<string, string> e in entries) {
                Match m = StepKey.Match(e.Key);
                if (e.Key == "Name") {
                    j.Name = e.Value;
                } else if (e.Key == "RunOnStartup") {
                    j.RunOnStartup = e.Value == "on";
                } else if (e.Key == "Order") {
                    int.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out j.Order);
                } else if (e.Key == "StopAtFirstFailure") {
                    j.StopAtFirstFailure = e.Value == "on";
                } else if (e.Key == "LastRun") {
                    j.LastRun = ReadTime(e.Value);
                } else if (e.Key == "Updated") {
                    j.Updated = ReadTime(e.Value);
                } else if (m.Success && !m.Groups[2].Success) {
                    List<string> f = Genie.ConfigUtils.SplitFields(e.Value);
                    if (f.Count < 4) continue;
                    Step step                  = new Step { Id = f[0], Enabled = f[1] != "off", Name = f[3] };
                    step.TemplateId            = f[2].Length > 0 ? f[2] : null;
                    steps[Number(m.Groups[1])] = step;
                } else if (m.Success) {
                    Substep sub = DecodeSubstep(e.Value);
                    if (sub == null) continue;
                    SortedDictionary<int, Substep> of;
                    if (!substeps.TryGetValue(Number(m.Groups[1]), out of))
                        substeps[Number(m.Groups[1])] = of = new SortedDictionary<int, Substep>();
                    of[Number(m.Groups[2])] = sub;
                }
            }
            // Substeps whose step line is gone get a step of their own, so the next save doesn't drop them.
            foreach (KeyValuePair<int, SortedDictionary<int, Substep>> of in substeps) {
                if (steps.ContainsKey(of.Key)) continue;
                steps[of.Key] = new Step { Name = "Step " + of.Key };
                Genie.Log("Job " + j.Name + ": its Step " + of.Key + " line is missing, so its substeps got a new " +
                          "step of that name.");
            }
            foreach (KeyValuePair<int, Step> step in steps) {
                SortedDictionary<int, Substep> of;
                if (substeps.TryGetValue(step.Key, out of)) step.Value.Substeps.AddRange(of.Values);
                j.Steps.Add(step.Value);
            }
            return j;
        }

        public static Template DecodeTemplate(string id, KeyValuePair<string, string>[] entries) {
            Template                       t    = new Template { Id = id };
            SortedDictionary<int, Substep> subs = new SortedDictionary<int, Substep>();
            foreach (KeyValuePair<string, string> e in entries) {
                Match m = SubstepKey.Match(e.Key);
                if (e.Key == "Name") {
                    t.Name = e.Value;
                } else if (e.Key == "LastRun") {
                    t.LastRun = ReadTime(e.Value);
                } else if (e.Key == "Updated") {
                    t.Updated = ReadTime(e.Value);
                } else if (m.Success) {
                    Substep s = DecodeSubstep(e.Value);
                    if (s != null) subs[Number(m.Groups[1])] = s;
                }
            }
            t.Substeps.AddRange(subs.Values);
            return t;
        }

        private static int Number(Group g) {
            return int.Parse(g.Value, CultureInfo.InvariantCulture);
        }

        // A job's section, counted for the restore window: "3 steps · 7 substeps". A templated step counts its
        // own substeps here, as the section holds no template.
        public static string Describe(GenieSectionChange section) {
            int steps = 0, substeps = 0;
            foreach (GenieSettingChange c in section.Settings) {
                string value = section.Kind == GenieSettingChangeKind.Goes ? c.Now : c.Backup; // as a restore leaves it
                if (value == null || !c.Key.StartsWith("Step ") && !c.Key.StartsWith("Substep ")) continue;
                if (c.Key.StartsWith("Step ") && c.Key.IndexOf('.') < 0)
                    steps++;
                else
                    substeps++;
            }
            string s = Genie.TextUtils.Count(substeps, "substep");
            return section.Name.StartsWith(Store.JobPrefix) ? Genie.TextUtils.Count(steps, "step") + " · " + s : s;
        }

        private static string Bool(bool b) {
            return b ? "on" : "off";
        }

        private static KeyValuePair<string, string> Pair(string key, string value) {
            return new KeyValuePair<string, string>(key, value ?? "");
        }
    }

    // Saved by name, so the order is only the menu's: the run history's choices, plus Own substeps only.
    internal enum StepsExpand { Never, FailedSteps, OwnSubsteps, AllSteps }

    internal static class Store {
        public const string JobPrefix = "Job.", TemplatePrefix = "Template.", Section = "Settings";

        public static readonly List<Job> Jobs           = new List<Job>();
        public static readonly List<Template> Templates = new List<Template>();

        public static GenieToggle StartupJobs, StopAllAtFirstFailure, ShowLauncher;

        /// <summary>Which steps start open when a job is shown (gear: Auto-expand). A click wins.</summary>
        public static StepsExpand AutoExpand {
            get { return Genie.ConfigUtils.GetEnum("AutoExpand", StepsExpand.AllSteps, Section); }
            set { Genie.ConfigUtils.SetEnum("AutoExpand", value, Section); }
        }

        /// <summary>Raised after every save, and after Load.</summary>
        public static event Action Changed;

        /// <summary>
        /// The settings were read again (a restore), before Changed: every job and template is a new object.
        /// </summary>
        public static event Action Reloaded;

        /// <summary>A substep's ignored load errors changed, from its gear or the run history.</summary>
        public static event Action IgnoresChanged;

        /// <summary>Counts edits, not a run's time: an Undo holds only while it's unchanged.</summary>
        public static int Revision { get; private set; }

        // Each job's and template's definition as last saved, by id, so a save that changes nothing isn't an update.
        private static readonly Dictionary<string, string> _definitions = new Dictionary<string, string>();

        // The Updated time to save: now if the definition differs from the last one seen.
        private static DateTime? Stamp(string id, List<KeyValuePair<string, string>> entries, DateTime? updated) {
            string now = Codec.Definition(entries), was;
            if (_definitions.TryGetValue(id, out was) && was == now) return updated;
            _definitions[id] = now;
            return DateTime.Now;
        }

        public static void Load() {
            if (StartupJobs == null) {
                StartupJobs           = Genie.ConfigUtils.CreateToggle("EnableStartupJobs", true, Section);
                StopAllAtFirstFailure = Genie.ConfigUtils.CreateToggle("StopAllAtFirstFailure", false, Section);
                ShowLauncher          = Genie.ConfigUtils.CreateToggle("ShowLauncher", false, Section);
            }
            Jobs.Clear();
            Templates.Clear();
            foreach (string section in Genie.ConfigUtils.GetSections(JobPrefix))
                Jobs.Add(Codec.DecodeJob(section.Substring(JobPrefix.Length), Genie.ConfigUtils.GetSection(section)));
            foreach (string section in Genie.ConfigUtils.GetSections(TemplatePrefix)) {
                Templates.Add(Codec.DecodeTemplate(section.Substring(TemplatePrefix.Length),
                                                   Genie.ConfigUtils.GetSection(section)));
            }
            _definitions.Clear();
            foreach (Job j in Jobs) _definitions[j.Id] = Codec.Definition(Codec.Entries(j));
            foreach (Template t in Templates) _definitions[t.Id] = Codec.Definition(Codec.Entries(t));
            Revision++;
            if (Reloaded != null) Reloaded();
            Raise();
        }

        public static void Save(Job j) {
            Revision++;
            j.Updated = Stamp(j.Id, Codec.Entries(j), j.Updated);
            SaveRunTime(j);
        }

        public static void Save(Template t) {
            Revision++;
            t.Updated = Stamp(t.Id, Codec.Entries(t), t.Updated);
            SaveRunTime(t);
        }

        /// <summary>Saves a job whose last run time changed: not an edit, so an Undo still holds.</summary>
        public static void SaveRunTime(Job j) {
            Genie.ConfigUtils.SetSection(JobPrefix + j.Id, Codec.Entries(j));
            Raise();
        }

        public static void SaveRunTime(Template t) {
            Genie.ConfigUtils.SetSection(TemplatePrefix + t.Id, Codec.Entries(t));
            Raise();
        }

        public static void Add(Job j) {
            Insert(Jobs.Count, j);
        }

        public static void Add(Template t) {
            Insert(Templates.Count, t);
        }

        public static void Insert(int index, Job j) {
            Jobs.Insert(Math.Min(index, Jobs.Count), j);
            Save(j);
        }

        public static void Insert(int index, Template t) {
            Templates.Insert(Math.Min(index, Templates.Count), t);
            Save(t);
        }

        public static void Delete(Job j) {
            Jobs.Remove(j);
            Genie.ConfigUtils.RemoveSection(JobPrefix + j.Id);
            Revision++;
            Raise();
        }

        /// <summary>Steps using it go back to their own substeps. Returns those steps.</summary>
        public static List<Step> Delete(Template t) {
            List<Step> users = new List<Step>();
            Templates.Remove(t);
            Genie.ConfigUtils.RemoveSection(TemplatePrefix + t.Id);
            foreach (Job j in Jobs) {
                bool used = false;
                foreach (Step s in j.Steps) {
                    if (s.TemplateId != t.Id) continue;
                    s.TemplateId = null;
                    users.Add(s);
                    used = true;
                }
                if (!used) continue;
                j.Updated = Stamp(j.Id, Codec.Entries(j), j.Updated);
                Genie.ConfigUtils.SetSection(JobPrefix + j.Id, Codec.Entries(j));
            }
            Revision++;
            Raise();
            return users;
        }

        public static Template FindTemplate(string id) {
            return id == null ? null : Templates.Find(t => t.Id == id);
        }

        public static Template FindTemplateByName(string name) {
            name = (name ?? "").Trim();
            return Templates.Find(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>What a step runs: its template's substeps while it uses one, else its own.</summary>
        public static List<Substep> SubstepsOf(Step s) {
            Template t = FindTemplate(s.TemplateId);
            return t != null ? t.Substeps : s.Substeps;
        }

        /// <summary>Every step using the template, with its job.</summary>
        public static List<KeyValuePair<Job, Step>> UsesOf(Template t) {
            List<KeyValuePair<Job, Step>> uses = new List<KeyValuePair<Job, Step>>();
            foreach (Job j in Jobs) {
                foreach (Step s in j.Steps) {
                    if (s.TemplateId == t.Id) uses.Add(new KeyValuePair<Job, Step>(j, s));
                }
            }
            return uses;
        }

        public static Job JobOf(Step step) {
            return Jobs.Find(j => j.Steps.Contains(step));
        }

        /// <summary>The job or template that holds this substep list (a step's own, or a template's).</summary>
        public static object OwnerOf(List<Substep> substeps) {
            foreach (Template t in Templates) {
                if (t.Substeps == substeps) return t;
            }
            foreach (Job j in Jobs) {
                foreach (Step s in j.Steps) {
                    if (s.Substeps == substeps) return j;
                }
            }
            return null;
        }

        /// <summary>The substep with this id, in a step or a template, and the list holding it; or null.</summary>
        public static Substep FindSubstep(string id, out List<Substep> list) {
            list = null;
            if (id == null) return null;
            List<List<Substep>> lists = new List<List<Substep>>();
            foreach (Template t in Templates) lists.Add(t.Substeps);
            foreach (Job j in Jobs) {
                foreach (Step s in j.Steps) lists.Add(s.Substeps);
            }
            foreach (List<Substep> l in lists) {
                Substep found = l.Find(s => s.Id == id);
                if (found == null) continue;
                list = l;
                return found;
            }
            return null;
        }

        public static void SaveIgnores(List<Substep> list) {
            SaveOwner(OwnerOf(list));
            if (IgnoresChanged != null) IgnoresChanged();
        }

        public static void SaveOwner(object owner) {
            Job j      = owner as Job;
            Template t = owner as Template;
            if (j != null) Save(j);
            if (t != null) Save(t);
        }

        /// <summary>Names are unique, case-insensitive and trimmed: null if this one is free, else why not.</summary>
        public static string CheckJobName(string name, Job self) {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Type a name.";
            if (Jobs.Exists(j => j != self && string.Equals(j.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                return "Another job is named " + name + ".";
            return null;
        }

        public static string CheckTemplateName(string name, Template self) {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Type a name.";
            if (Templates.Exists(t => t != self &&
                                      string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                return "Another template is named " + name + ".";
            return null;
        }

        /// <summary>Startup jobs in run order: lowest Order first, ties by name.</summary>
        public static List<Job> StartupOrder() {
            List<Job> jobs = Jobs.FindAll(j => j.RunOnStartup);
            jobs.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : ByName(a.Name, b.Name));
            return jobs;
        }

        /// <summary>The other jobs, by name.</summary>
        public static List<Job> OnDemandOrder() {
            List<Job> jobs = Jobs.FindAll(j => !j.RunOnStartup);
            jobs.Sort((a, b) => ByName(a.Name, b.Name));
            return jobs;
        }

        public static int ByName(string a, string b) {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void Raise() {
            if (Changed != null) Changed();
        }
    }

    // == Copy and paste ===============================================================================
    // A copy is the ini's own lines, so lines copied out of a settings backup (SettingsBackups.zip) paste as well:
    //   [Job.3fa2c1d9e0] and its lines       a job
    //   [Template.8b1e0f2a44] and its lines  a template
    //   Step 2=...  Step 2.1=...             steps, each with its substep lines after it
    //   Step 2.1=...  or  Substep 3=...      substeps alone
    // Numbers and ids only give the structure: a paste gets new ids and is numbered where it lands. A step's template
    // is by id, so it reads while that template exists, renamed or not. Read and written by the settings file's own
    // rules (ConfigUtils.ParseIniText, ToIniText). Read makes new objects every time.

    internal enum CopyKind { Job, Template, Steps, Substeps }

    internal sealed class CopyText {
        public CopyKind      Kind;
        public Job           Job;
        public Template      Template;
        public List<Step>    Steps    = new List<Step>();
        public List<Substep> Substeps = new List<Substep>();

        private static readonly Regex StepKey    = new Regex(@"^Step (\d+)$");
        private static readonly Regex SubstepKey = new Regex(@"^(?:Step (\d+)\.\d+|Substep \d+)$");

        // == Writing ==================================================================================

        public static string Of(Job j) {
            return Genie.ConfigUtils.ToIniText(Store.JobPrefix + j.Id, Codec.Entries(j));
        }

        public static string Of(Template t) {
            return Genie.ConfigUtils.ToIniText(Store.TemplatePrefix + t.Id, Codec.Entries(t));
        }

        /// <summary>The step's line and its substeps' lines, as its job saves them.</summary>
        public static string Of(Step s) {
            Job job = Store.JobOf(s);
            if (job == null) return null;
            string n = (job.Steps.IndexOf(s) + 1).ToString(CultureInfo.InvariantCulture);
            return Genie.ConfigUtils.ToIniText(
                null, Codec.Entries(job).FindAll(e => e.Key == "Step " + n || e.Key.StartsWith("Step " + n + ".")));
        }

        /// <summary>The substep's line, as its job or template saves it.</summary>
        public static string Of(List<Substep> list, Substep s) {
            object owner                               = Store.OwnerOf(list);
            Job job                                    = owner                    as Job;
            List<KeyValuePair<string, string>> entries = job != null     ? Codec.Entries(job)
                                                         : owner != null ? Codec.Entries((Template)owner)
                                                                         : null;
            if (entries == null) return null;
            List<KeyValuePair<string, string>> line =
                entries.FindAll(e => SubstepKey.IsMatch(e.Key) && Genie.ConfigUtils.SplitFields(e.Value)[0] == s.Id);
            return line.Count > 0 ? Genie.ConfigUtils.ToIniText(null, line.GetRange(0, 1)) : null;
        }

        // == Reading ==================================================================================

        /// <summary>
        /// What the text holds, or null: problem says why it can't be read, or is null too for text that isn't
        /// Package Loader's (it starts with neither a job or template section nor a step or substep line).
        /// </summary>
        public static CopyText Read(string text, out string problem) {
            problem = null;
            if (text == null) return null;
            List<GenieIniLine> all = Genie.ConfigUtils.ParseIniText(text);
            if (all.Count == 0) return null;
            GenieIniLine first = all[0], head = first.IsSection ? first : null;
            if (head != null && !head.Section.StartsWith(Store.JobPrefix) &&
                !head.Section.StartsWith(Store.TemplatePrefix))
                return null;
            if (head == null && !(first.IsSetting && (StepKey.IsMatch(first.Key) || SubstepKey.IsMatch(first.Key))))
                return null;
            List<GenieIniLine> lines = head != null ? all.GetRange(1, all.Count - 1) : all;
            foreach (GenieIniLine l in lines) {
                if (l.IsSetting) continue;
                problem = At(l, l.IsSection ? "copy one job or template at a time."
                                            : "it isn\u2019t a setting line (Key=Value).");
                return null;
            }

            CopyText copy = new CopyText();
            if (head != null && head.Section.StartsWith(Store.TemplatePrefix)) {
                copy.Kind     = CopyKind.Template;
                copy.Template = new Template();
                problem       = ReadTemplate(lines, copy.Template);
            } else if (head != null) {
                copy.Kind = CopyKind.Job;
                copy.Job  = new Job();
                problem   = ReadSteps(lines, copy.Job.Steps, copy.Job);
            } else if (lines.Exists(l => StepKey.IsMatch(l.Key))) {
                copy.Kind = CopyKind.Steps;
                problem   = ReadSteps(lines, copy.Steps, null);
            } else {
                copy.Kind = CopyKind.Substeps;
                problem   = ReadSubsteps(lines, copy.Substeps);
            }
            if (problem == null && head != null && lines.Count == 0) problem = At(head, "it has no lines under it.");
            return problem == null ? copy : null;
        }

        // A job's own lines (Name, Order...) only count in its section; Name and the times are left out, as a pasted
        // job keeps its own.
        private static string ReadSteps(List<GenieIniLine> lines, List<Step> into, Job job) {
            Dictionary<int, Step> byNumber = new Dictionary<int, Step>();
            foreach (GenieIniLine l in lines) {
                Match step = StepKey.Match(l.Key), sub = SubstepKey.Match(l.Key);
                if (step.Success) {
                    List<string> f = Genie.ConfigUtils.SplitFields(l.Value);
                    if (f.Count < 4) return At(l, "a step line has an id, on or off, a template id and a name.");
                    Step s = new Step { Enabled = f[1] != "off", Name = f[3] };
                    if (f[2].Length > 0) {
                        if (Store.FindTemplate(f[2]) == null)
                            return At(l, "it uses a template that isn’t in your settings now.");
                        s.TemplateId = f[2];
                    }
                    byNumber[int.Parse(step.Groups[1].Value, CultureInfo.InvariantCulture)] = s;
                    into.Add(s);
                } else if (sub.Success && sub.Groups[1].Success) {
                    Step s;
                    if (!byNumber.TryGetValue(int.Parse(sub.Groups[1].Value, CultureInfo.InvariantCulture), out s))
                        return At(l, "its step’s line (" + l.Key.Substring(0, l.Key.IndexOf('.')) +
                                         "=...) isn’t above it.");
                    Substep substep;
                    string  why = ReadSubstep(l, out substep);
                    if (why != null) return why;
                    s.Substeps.Add(substep);
                } else if (job != null && ReadJobSetting(l, job)) {
                } else {
                    return At(l, job != null ? "it isn’t a job’s setting."
                                             : "steps paste alone or in a [Job.] section with its lines.");
                }
            }
            return null;
        }

        private static bool ReadJobSetting(GenieIniLine l, Job job) {
            switch (l.Key) {
                case "Name":
                case "LastRun":
                case "Updated":
                    return true;
                case "RunOnStartup":
                    job.RunOnStartup = l.Value == "on";
                    return true;
                case "StopAtFirstFailure":
                    job.StopAtFirstFailure = l.Value == "on";
                    return true;
                case "Order":
                    int order;
                    if (int.TryParse(l.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out order))
                        job.Order = Math.Max(1, Math.Min(999, order));
                    return true;
                default:
                    return false;
            }
        }

        private static string ReadTemplate(List<GenieIniLine> lines, Template into) {
            foreach (GenieIniLine l in lines) {
                if (l.Key == "Name" || l.Key == "LastRun" || l.Key == "Updated") continue; // it keeps its own
                if (!l.Key.StartsWith("Substep ")) return At(l, "it isn’t a template’s setting.");
                Substep s;
                string  why = ReadSubstep(l, out s);
                if (why != null) return why;
                into.Substeps.Add(s);
            }
            return null;
        }

        private static string ReadSubsteps(List<GenieIniLine> lines, List<Substep> into) {
            foreach (GenieIniLine l in lines) {
                if (!SubstepKey.IsMatch(l.Key))
                    return At(l, "it belongs to a whole job or template: copy its [section] line too.");
                Substep s;
                string  why = ReadSubstep(l, out s);
                if (why != null) return why;
                into.Add(s);
            }
            return null;
        }

        private static string ReadSubstep(GenieIniLine l, out Substep s) {
            List<string> f = Genie.ConfigUtils.SplitFields(l.Value);
            s = f.Count >= 4 && (f[2] == "Package" || f[2] == "Script") ? Codec.DecodeSubstep(l.Value) : null;
            if (s == null) return At(l, "a substep line has an id, on or off, Package or Script, and a file.");
            s.Id = Ids.New();
            return null;
        }

        private static string At(GenieIniLine l, string why) {
            string line = l.Text.Length > 60 ? l.Text.Substring(0, 57) + "..." : l.Text;
            return "line " + l.Number + ", “" + line + "”: " + why;
        }
    }

    // == Run order and issues =========================================================================

    /// <summary>A substep in run order, numbered as shown ("2.1").</summary>
    internal sealed class RunItem {
        public Step    Step; // null down a template
        public int     StepNumber;
        public Substep Substep;
        public int     Number; // within its step or template

        public string Label {
            get { return (Step != null ? StepNumber + "." : "") + Number; }
        }
    }

    internal sealed class Issue {
        public bool   IsWrong; // red; else missing (orange)
        public bool   IsInfo;  // blue: fine as set up, but not this session. Not a failure to warn about.
        public string Text;

        public Genie.ControlUtils.TagTone Tone {
            get {
                return IsInfo    ? Genie.ControlUtils.TagTone.Info
                       : IsWrong ? Genie.ControlUtils.TagTone.Danger
                                 : Genie.ControlUtils.TagTone.Warning;
            }
        }
    }

    // What makes the next run fail a substep. Not loaded is no issue: running loads it. Nor is a substep with no
    // file yet, which is still being set up (a run fails it). Info only, as neither fails it: a package in memory
    // from another file (the choice of file is the user's, and it holds once the editor restarts), and a file an
    // earlier row already has, likely added twice by accident.
    internal static class Issues {
        public static List<RunItem> Order(Job j, bool enabledOnly) {
            List<RunItem> items = new List<RunItem>();
            for (int i = 0; i < j.Steps.Count; i++) {
                Step s = j.Steps[i];
                if (enabledOnly && !s.Enabled) continue;
                List<Substep> subs = Store.SubstepsOf(s);
                for (int k = 0; k < subs.Count; k++) {
                    if (enabledOnly && !subs[k].Enabled) continue;
                    items.Add(new RunItem { Step = s, StepNumber = i + 1, Substep = subs[k], Number = k + 1 });
                }
            }
            return items;
        }

        public static List<RunItem> Order(Template t) {
            List<RunItem> items = new List<RunItem>();
            for (int k = 0; k < t.Substeps.Count; k++)
                items.Add(new RunItem { Substep = t.Substeps[k], Number = k + 1 });
            return items;
        }

        /// <summary>
        /// Each substep's issue, by substep and step ("stepId/substepId": a template's substeps can sit under several
        /// steps of one job). Earlier means earlier in run order among enabled ones.
        /// </summary>
        public static Dictionary<string, Issue> Find(List<RunItem> items) {
            Dictionary<string, Issue>  found  = new Dictionary<string, Issue>();
            Dictionary<string, string> loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> rows   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (RunItem item in items) {
                Issue issue = Of(item.Substep);
                if ((issue == null || issue.IsInfo) && item.Substep.Kind == SubstepKind.Package &&
                    item.Substep.File.Trim().Length > 0 && item.Substep.Enabled &&
                    (item.Step == null || item.Step.Enabled)) {
                    string name = Path.GetFileNameWithoutExtension(item.Substep.File), earlier;
                    if (loaded.TryGetValue(name, out earlier) &&
                        !Genie.FileUtils.IsSamePath(earlier, item.Substep.File))
                        issue = Wrong("An earlier substep loads " + name + " from " + earlier + ".");
                    else if (!loaded.ContainsKey(name))
                        loaded[name] = item.Substep.File;
                }
                // Off rows count: they're still listed twice.
                string full = item.Substep.File.Trim().Length > 0 ? Genie.FileUtils.GetFullPath(item.Substep.File)
                                                               : null;
                if (full != null) {
                    string first;
                    if (!rows.TryGetValue(full, out first))
                        rows[full] = item.Label;
                    else if (issue == null || issue.IsInfo)
                        issue = Repeated(issue, first);
                }
                if (issue != null) found[Key(item)] = issue;
            }
            return found;
        }

        public static string Key(RunItem item) {
            return (item.Step != null ? item.Step.Id : "") + "/" + item.Substep.Id;
        }

        /// <summary>A substep's own issue, without the others around it.</summary>
        public static Issue Of(Substep s) {
            if (s.File.Trim().Length == 0) return null;
            string full = Genie.FileUtils.GetFullPath(s.File);
            string ext  = Path.GetExtension(s.File).ToLowerInvariant();
            if (s.Kind == SubstepKind.Package) {
                if (ext != ".upk" && ext != ".umap") return Wrong("Not a package file (.upk or .umap).");
                if (full == null || !File.Exists(full)) return Missing("No file at this path.");
                string why = Genie.PackageUtils.CheckLoadFromFile(full);
                if (why == null) return null;
                return new Issue {
                    IsInfo = true,
                    Text =
                        why + " This file loads in its place once the editor restarts; this session it can\u2019t.",
                };
            }
            if (ext != ".bat" && ext != ".cmd") return Wrong("Not a batch file (.bat or .cmd).");
            if (full == null || !File.Exists(full)) return Missing("No file at this path.");
            string problem;
            Genie.ProcessUtils.ParseExitCodes(s.SuccessCodes, out problem);
            if (problem != null) return Wrong("Success codes: " + problem);
            if (s.Timeout.Trim().Length > 0 && !Seconds(s.Timeout).HasValue)
                return Wrong("Timeout isn\u2019t a number of seconds above 0.");
            string variable = CheckVariables(s.Variables);
            return variable == null ? null : Wrong(variable);
        }

        /// <summary>A timeout as saved ("2.5"), or null for none or unreadable.</summary>
        public static double? Seconds(string text) {
            double seconds;
            return double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                                   out seconds) &&
                           seconds > 0
                       ? (double?)seconds
                       : null;
        }

        /// <summary>Why a variable can't be set, or null: a PL_ name (ours) or one given twice.</summary>
        public static string CheckVariables(List<Variable> variables) {
            List<string> seen = new List<string>();
            foreach (Variable v in variables) {
                if (v.Name.Length == 0) return "A variable has no name.";
                if (v.Name.StartsWith("PL_", StringComparison.OrdinalIgnoreCase))
                    return "Names starting with PL_ are reserved for Package Loader.";
                if (seen.Exists(n => string.Equals(n, v.Name, StringComparison.OrdinalIgnoreCase)))
                    return v.Name + " is set twice.";
                seen.Add(v.Name);
            }
            return null;
        }

        private static Issue Repeated(Issue info, string firstRow) {
            string text = "Substep " + firstRow + " has this file too.";
            return new Issue { IsInfo = true, Text = info != null ? info.Text + " " + text : text };
        }

        private static Issue Wrong(string text) {
            return new Issue { IsWrong = true, Text = text };
        }

        private static Issue Missing(string text) {
            return new Issue { IsWrong = false, Text = text };
        }
    }

    // == Running ======================================================================================

    // Unfinished: read back from a log only, a substep that started and has no end (the editor hung or closed).
    internal enum Outcome { NotRun, Running, Done, Failed, Skipped, Unfinished }

    /// <summary>How a substep ran, as it was set up then: later edits don't touch it.</summary>
    internal sealed class SubstepResult {
        public string      Key, Label, File;
        public SubstepKind Kind;
        public Outcome     Outcome = Outcome.NotRun;
        public string      Message = "";
        public DateTime    Start, End;

        public bool    WillRun = true; // on, with its step on, when the run was planned
        public string  SubstepId;      // for the run history's Ignore; none in logs from before it
        public Substep Source;    // while running only: what it runs as, as a job in the run is locked

        public List<string> Settings = new List<string>(); // a script's, as RunLog.ScriptSettings writes them

        public readonly List<Genie.ProcessUtils.ProcessLine> Lines = new List<Genie.ProcessUtils.ProcessLine>();

        public readonly List<string> Ignored = new List<string>(); // load errors raised and ignored

        /// <summary>What Show errors only keeps, and Auto-expand's Failed steps opens.</summary>
        public bool IsProblem {
            get { return Outcome == Outcome.Failed || Outcome == Outcome.Unfinished; }
        }

        public bool IsOff {
            get { return Message == Runner.OffMessage; }
        }
    }

    internal sealed class StepResult {
        public string StepId, Name, TemplateName;
        public int    Number;

        public readonly List<SubstepResult> Substeps = new List<SubstepResult>();

        /// <summary>Counts only enabled substeps: one turned off isn't a skip of the step.</summary>
        public Outcome Outcome {
            get { return Combine(Substeps.FindAll(s => !s.IsOff)); }
        }

        public static Outcome Combine(List<SubstepResult> results) {
            if (results.Exists(r => r.Outcome == Outcome.Running)) return Outcome.Running;
            if (results.Exists(r => r.Outcome == Outcome.Failed)) return Outcome.Failed;
            if (results.Exists(r => r.Outcome == Outcome.Unfinished)) return Outcome.Unfinished;
            if (results.Exists(r => r.Outcome == Outcome.NotRun)) return Outcome.NotRun;
            if (results.Count > 0 && results.TrueForAll(r => r.Outcome == Outcome.Skipped)) return Outcome.Skipped;
            return results.Count > 0 ? Outcome.Done : Outcome.Skipped;
        }
    }

    internal sealed class JobResult {
        public string   JobId, Name;
        public int      Number; // in its run
        public bool     IsTemplate; // a template run on its own
        public DateTime Start, End;

        public readonly List<StepResult> Steps = new List<StepResult>();

        public List<SubstepResult> All() {
            List<SubstepResult> all = new List<SubstepResult>();
            foreach (StepResult s in Steps) all.AddRange(s.Substeps);
            return all;
        }

        public int Count(Outcome outcome) {
            return All().FindAll(r => r.Outcome == outcome && !r.IsOff).Count;
        }

        public Outcome Outcome {
            get { return StepResult.Combine(All().FindAll(r => !r.IsOff)); }
        }
    }

    internal sealed class RunResult {
        public DateTime Start, End;
        public bool     IsStartup, IsFinished;
        public bool     IsStopped; // by the user; this session only, as the log doesn't say
        public string   LogFile;
        public string   Session; // as addons.log marks it; null from a log older than this

        public readonly List<JobResult> Jobs = new List<JobResult>();

        public string Title {
            get { return IsStartup ? "On startup" : Jobs.Count > 0? Jobs[0].Name : ""; }
        }

        public int Count(Outcome outcome) {
            int n = 0;
            foreach (JobResult j in Jobs) n += j.Count(outcome);
            return n;
        }
    }

    /// <summary>
    /// Runs back to back: one started while another goes waits in its line, and starts as the one before it ends. The
    /// running window shows a line whole. A start with nothing running begins a new one.
    /// </summary>
    internal sealed class RunLine {
        public readonly List<RunResult> Runs = new List<RunResult>(); // started or waiting, in order
        public bool                     IsFinished, IsStopped;
    }

    // One run at a time: the editor's thread does one thing at a time, and a load holds it. A start while a run goes
    // is a run of its own that waits in the line, so each start is its own entry in the run history. Package substeps
    // between two scripts load in one pass (PackageUtils.LoadPackagesFromFiles); scripts run beside the editor and the
    // run carries on when one ends. Everything here is on the UI thread.
    internal static class Runner {
        public const string OffMessage = "Off", NoFileMessage = "No file chosen, so skipped.";

        private const string StoppedMessage       = "Skipped: the run was stopped.",
                             FailedEarlierMessage = "Skipped: an earlier substep failed.";

        private sealed class Plan {
            public RunResult           Run;
            public Job                 Job;
            public List<RunItem>       Items;
            public List<SubstepResult> Results;
            public JobResult           Result;
        }

        private static readonly List<Plan> _plans = new List<Plan>();
        private static int                 _plan, _item;
        private static bool                _stopJob, _stopRun;
        private static GenieProcess        _process;

        // What each run waiting in the line has asked to hear once it starts.
        private static readonly Dictionary<RunResult, Action<RunResult>> _onStarted =
            new Dictionary<RunResult, Action<RunResult>>();

        // A package name the line has loaded, and from where: a later substep can't load it from another file.
        private static readonly Dictionary<string, string> _loaded =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Latest results this session: by job id, step id and "stepId/substepId".
        private static readonly Dictionary<string, JobResult> _jobs         = new Dictionary<string, JobResult>();
        private static readonly Dictionary<string, StepResult> _steps       = new Dictionary<string, StepResult>();
        private static readonly Dictionary<string, SubstepResult> _substeps = new Dictionary<string, SubstepResult>();

        private static Action _changed;

        /// <summary>This session's runs, oldest first.</summary>
        public static readonly List<RunResult> History = new List<RunResult>();

        /// <summary>The run going now, else the last one this session.</summary>
        public static RunResult Current { get; private set; }

        /// <summary>The runs going and waiting, else the last line this session; null before any.</summary>
        public static RunLine Line { get; private set; }

        /// <summary>What the run is doing now: "Loading 3 packages", "Running Sync-Upks.bat".</summary>
        public static string Activity { get; private set; }

        /// <summary>Anything about the run changed: a substep started or ended, a line came in. Coalesced.</summary>
        public static event Action Changed;

        /// <summary>A run ended, with its result.</summary>
        public static event Action<RunResult> Finished;

        /// <summary>
        /// Raised right before a script starts or a batch of packages loads, with what's about to run: a load can
        /// hang or close the editor, so this is the last moment to note it (the startup marker).
        /// </summary>
        public static event Action<JobResult, List<SubstepResult>> Starting;

        /// <summary>A run is going, or waiting: the line isn't finished.</summary>
        public static bool IsRunning {
            get { return Line != null && !Line.IsFinished; }
        }

        /// <summary>In the line, running or waiting: its settings, steps and templates are locked.</summary>
        public static bool IsLocked(Job j) {
            return IsRunning && _plans.FindIndex(_plan, p => p.Job == j) >= 0;
        }

        public static bool IsLocked(Template t) {
            return LockerOf(t) != null;
        }

        /// <summary>The first job in the line using the template, or its own stand-in (RunsTemplate), or
        /// null.</summary>
        public static Job LockerOf(Template t) {
            if (!IsRunning) return null;
            int i = _plans.FindIndex(_plan, p => p.Job.Steps.Exists(s => s.TemplateId == t.Id));
            return i >= 0 ? _plans[i].Job : null;
        }

        /// <summary>The job or template holding this substep list is locked.</summary>
        public static bool IsLocked(List<Substep> list) {
            object   owner    = Store.OwnerOf(list);
            Job      job      = owner as Job;
            Template template = owner as Template;
            return job != null ? IsLocked(job) : template != null && IsLocked(template);
        }

        /// <summary>The job running now, or null.</summary>
        public static Job RunningJob {
            get { return IsRunning && _plan < _plans.Count ? _plans[_plan].Job : null; }
        }

        /// <summary>The running job's result, or null.</summary>
        public static JobResult RunningResult {
            get { return IsRunning && _plan < _plans.Count ? _plans[_plan].Result : null; }
        }

        /// <summary>A script is running now, so Skip can end it. A package load can't be ended.</summary>
        public static bool IsScriptRunning {
            get { return _process != null; }
        }

        public static JobResult LatestOf(Job j) {
            JobResult r;
            return _jobs.TryGetValue(j.Id, out r) ? r : null;
        }

        public static JobResult LatestOf(Template t) {
            JobResult r;
            return _jobs.TryGetValue(t.Id, out r) ? r : null;
        }

        /// <summary>The substep's latest result in a run of the template itself, not of a step using it.</summary>
        public static SubstepResult LatestOf(Template t, Substep s) {
            SubstepResult r;
            return _substeps.TryGetValue(t.Id + "/" + s.Id, out r) ? r : null;
        }

        public static StepResult LatestOf(Step s) {
            StepResult r;
            return _steps.TryGetValue(s.Id, out r) ? r : null;
        }

        public static SubstepResult LatestOf(RunItem item) {
            SubstepResult r;
            return _substeps.TryGetValue(Issues.Key(item), out r) ? r : null;
        }

        /// <summary>The run holding a result from this session (a job's, step's or substep's), or null.</summary>
        public static RunResult RunOf(object result) {
            if (result == null) return null;
            foreach (RunResult run in History) {
                foreach (JobResult j in run.Jobs) {
                    if (j == result) return run;
                    foreach (StepResult s in j.Steps) {
                        if (s == result || s.Substeps.Contains(result as SubstepResult)) return run;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Runs the job, or the substeps of it that only picks (a step, a substep), as a run of its own: now, or once
        /// the runs in the line have. False if it's running or waiting already.
        /// </summary>
        public static bool Start(Job job, Predicate<RunItem> only = null) {
            if (IsLocked(job)) return false;
            Begin(new List<Plan> { MakePlan(job, only) }, false, null);
            return true;
        }

        /// <summary>
        /// Runs the template by itself, or the substeps of it that only picks. It goes through its run as a one-step
        /// job that's never saved, under the template's id, so results and locks work as for a job using it. False if
        /// it's running or waiting already, by itself or through a job.
        /// </summary>
        public static bool Start(Template t, Predicate<RunItem> only = null) {
            if (IsLocked(t)) return false;
            Job job = new Job { Id = t.Id, Name = t.Name, RunsTemplate = t };
            job.Steps.Add(new Step { Id = t.Id, Name = t.Name, TemplateId = t.Id });
            Begin(new List<Plan> { MakePlan(job, only) }, false, null);
            return true;
        }

        /// <summary>
        /// Runs the jobs in order as one run, now or once the runs in the line have; any running or waiting already
        /// are left out. onStarted gets the run as it starts, before anything of it runs. The startup jobs.
        /// </summary>
        public static void Start(List<Job> jobs, bool startup, Action<RunResult> onStarted) {
            List<Plan> plans = new List<Plan>();
            foreach (Job job in jobs) {
                if (!IsLocked(job)) plans.Add(MakePlan(job, null));
            }
            if (plans.Count > 0) Begin(plans, startup, onStarted);
        }

        private static Plan MakePlan(Job job, Predicate<RunItem> only) {
            List<RunItem> items = Issues.Order(job, false);
            if (only != null) items = items.FindAll(only);
            Plan plan   = new Plan { Job = job, Items = items, Results = new List<SubstepResult>() };
            plan.Result = new JobResult { JobId = job.Id, Name = job.Name, IsTemplate = job.RunsTemplate != null };
            foreach (RunItem item in items) {
                StepResult step = plan.Result.Steps.Find(s => s.StepId == item.Step.Id);
                if (step == null) {
                    Template t = Store.FindTemplate(item.Step.TemplateId);
                    step       = new StepResult {
                        StepId       = item.Step.Id,
                        Name         = item.Step.Name,
                        Number       = item.StepNumber,
                        TemplateName = t != null ? t.Name : null,
                    };
                    plan.Result.Steps.Add(step);
                }
                SubstepResult r = new SubstepResult {
                    Key      = Issues.Key(item),
                    Label    = item.Label,
                    File     = item.Substep.File,
                    Kind     = item.Substep.Kind,
                    Settings = RunLog.ScriptSettings(item.Substep),

                    WillRun   = item.Step.Enabled && item.Substep.Enabled,
                    SubstepId = item.Substep.Id,
                    Source    = item.Substep,
                };
                step.Substeps.Add(r);
                plan.Results.Add(r);
            }
            return plan;
        }

        // Every plan joins before the first runs: a job with nothing to run would end the run before the next joined.
        private static void Begin(List<Plan> plans, bool startup, Action<RunResult> onStarted) {
            bool      idle = !IsRunning;
            RunResult run  = new RunResult { IsStartup = startup };
            if (idle) {
                Line = new RunLine();
                _plans.Clear();
                _loaded.Clear();
                _plan = _item = 0;
                Activity      = null;
            }
            Line.Runs.Add(run);
            foreach (Plan plan in plans) {
                plan.Run           = run;
                plan.Result.Number = run.Jobs.Count + 1;
                _plans.Add(plan);
                run.Jobs.Add(plan.Result);
            }
            if (onStarted != null) _onStarted[run] = onStarted;
            if (idle) Next();
            Raise();
        }

        // A run's start is when it starts, not when it joined the line: its time in the history is its own.
        private static void StartRun(RunResult run) {
            Current     = run;
            run.Start   = DateTime.Now;
            run.LogFile = RunLog.SessionFile;
            run.Session = Genie.GetSessionId();
            _stopRun    = false;
            History.Add(run);
            RunLog.Started(run);
            Action<RunResult> onStarted;
            if (!_onStarted.TryGetValue(run, out onStarted)) return;
            _onStarted.Remove(run);
            onStarted(run);
        }

        private static void EndRun(RunResult run) {
            run.End        = DateTime.Now;
            run.IsFinished = true;
            RunLog.Ended(run);
            Raise();
            if (Finished != null) Finished(run);
        }

        /// <summary>Ends the running script as skipped, not failed, and carries on.</summary>
        public static void Skip() {
            if (_process != null) _process.Cancel();
        }

        /// <summary>
        /// Ends the run: the running script is killed, everything after it skipped. The runs waiting behind it never
        /// start, so the run history never gets them.
        /// </summary>
        public static void Stop() {
            if (!IsRunning) return;
            Current.IsStopped = true;
            Line.IsStopped    = true;
            _stopRun          = true;
            int ours          = _plans.FindLastIndex(p => p.Run == Current) + 1;
            foreach (Plan p in _plans.GetRange(ours, _plans.Count - ours)) {
                Line.Runs.Remove(p.Run);
                _onStarted.Remove(p.Run);
            }
            _plans.RemoveRange(ours, _plans.Count - ours);
            if (_process != null) _process.Cancel();
            Raise();
        }

        // Runs from where the line is until something has to wait: a script, or the UI drawing after a load.
        private static void Next() {
            while (_plan < _plans.Count) {
                Plan plan = _plans[_plan];
                if (_item == 0) {
                    if (plan.Run != Current) StartRun(plan.Run);
                    StartJob(plan);
                }
                while (_item < plan.Items.Count) {
                    RunItem       item = plan.Items[_item];
                    SubstepResult r    = plan.Results[_item];
                    if (!item.Step.Enabled || !item.Substep.Enabled) {
                        Finish(r, Outcome.Skipped, OffMessage);
                        _item++;
                        continue;
                    }
                    if (item.Substep.File.Trim().Length == 0) {
                        Finish(r, Outcome.Skipped, NoFileMessage);
                        _item++;
                        continue;
                    }
                    if (_stopRun || _stopJob) {
                        Finish(r, Outcome.Skipped, Current.IsStopped ? StoppedMessage : FailedEarlierMessage);
                        _item++;
                        continue;
                    }
                    if (item.Substep.Kind == SubstepKind.Script) {
                        RunScript(plan, item, r);
                        return;
                    }
                    LoadBatch(plan); // carries on with Next once loaded
                    return;
                }
                plan.Result.End = DateTime.Now;
                _plan++;
                _item = 0;
                if (_plan < _plans.Count && _plans[_plan].Run == plan.Run) continue;
                // The line is done before its last run says so, so a Finished handler that starts a run begins a new
                // line instead of joining this one.
                if (_plan == _plans.Count) {
                    Line.IsFinished = true;
                    _process        = null;
                    EndRun(plan.Run);
                    return;
                }
                EndRun(plan.Run);
            }
        }

        private static void StartJob(Plan plan) {
            _stopJob           = false;
            plan.Result.Start  = DateTime.Now;
            _jobs[plan.Job.Id] = plan.Result;
            RunLog.Job(plan.Result);
            Template template = plan.Job.RunsTemplate;
            if (template != null) {
                template.LastRun = plan.Result.Start;
                Store.SaveRunTime(template);
            } else {
                plan.Job.LastRun = plan.Result.Start;
                Store.SaveRunTime(plan.Job);
            }
        }

        // Every package substep from here to the next script, across steps, in one pass. One that fails its checks
        // is never handed to the load, so the package file cache is left as it was for it.
        private static void LoadBatch(Plan plan) {
            List<SubstepResult>    batch = new List<SubstepResult>();
            List<string>           files = new List<string>();
            HashSet<SubstepResult> again = new HashSet<SubstepResult>(); // its file loaded by an earlier substep
            DateTime               start = DateTime.Now;
            for (; _item < plan.Items.Count && plan.Items[_item].Substep.Kind == SubstepKind.Package; _item++) {
                RunItem       item = plan.Items[_item];
                SubstepResult r    = plan.Results[_item];
                if (!item.Step.Enabled || !item.Substep.Enabled) {
                    Finish(r, Outcome.Skipped, OffMessage);
                    continue;
                }
                if (item.Substep.File.Trim().Length == 0) {
                    Finish(r, Outcome.Skipped, NoFileMessage);
                    continue;
                }
                r.Start = start;
                Note(plan, r);
                Issue  issue = Blocking(item.Substep);
                string name  = Path.GetFileNameWithoutExtension(item.Substep.File), earlier;
                if (issue == null && _loaded.TryGetValue(name, out earlier) &&
                    !Genie.FileUtils.IsSamePath(earlier, item.Substep.File))
                    issue = new Issue { IsWrong = true,
                                        Text    = "An earlier substep loaded " + name + " from " + earlier + "." };
                if (issue != null) {
                    Finish(r, Outcome.Failed, issue.Text);
                    continue;
                }
                if (_loaded.ContainsKey(name)) again.Add(r);
                _loaded[name] = item.Substep.File;
                batch.Add(r);
                files.Add(item.Substep.File);
            }
            if (files.Count == 0) {
                Later(Next);
                return;
            }
            Activity = "Loading " + Genie.TextUtils.Count(files.Count, "package");
            Raise();
            // The load holds the UI thread, as the editor's own Open does: the marks, spinners and lock draw first.
            Genie.WorkUtils.RunAfterRender(delegate {
                if (Current.IsStopped) { // the running window's Stop, clicked while this waited to draw
                    foreach (SubstepResult r in batch) Finish(r, Outcome.Skipped, StoppedMessage);
                } else {
                    Announce(plan, batch);
                    Load(batch, files, again);
                }
                Later(Next);
            });
        }

        private static void Announce(Plan plan, List<SubstepResult> substeps) {
            RunLog.Starting(plan.Result, substeps);
            if (Starting != null) Starting(plan.Result, substeps);
        }

        private static void Later(Action a) {
            Genie.Dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Background);
        }

        // A package already loaded from the same file is done, not a failure: substeps (in shared templates, say) can
        // load the same file. Its load errors stay with the substep that loaded it.
        private static void Load(List<SubstepResult> batch, List<string> files, HashSet<SubstepResult> again) {
            Genie.PackageUtils.LoadResult[] loaded = Genie.PackageUtils.LoadPackagesFromFiles(files);
            for (int i = 0; i < batch.Count; i++) {
                Genie.PackageUtils.LoadResult l = loaded[i];
                SubstepResult                 r = batch[i];
                if (l.Problem == null && l.IsLoaded && again.Contains(r)) {
                    Finish(r, Outcome.Done, "Already loaded by an earlier substep, from this file.");
                    continue;
                }
                if (l.Problem == null && l.WasLoaded) {
                    Finish(r, Outcome.Done, "Already loaded, from this file.");
                    continue;
                }
                List<string> failing = new List<string>();
                foreach (Genie.PackageUtils.LoadError e in l.Errors) {
                    if (r.Source.Ignores(e.Description)) {
                        r.Ignored.Add(e.Description);
                        continue;
                    }
                    failing.Add(e.Description);
                    r.Lines.Add(new Genie.ProcessUtils.ProcessLine {
                        IsError = true,
                        Text    = e.Description,
                        Time    = DateTime.Now,
                    });
                }
                if (l.Problem != null)
                    Finish(r, Outcome.Failed, l.Problem);
                else if (!l.IsLoaded)
                    Finish(r, Outcome.Failed, "It didn\u2019t load.");
                else if (failing.Count > 0)
                    Finish(r, Outcome.Failed,
                           failing.Count == 1 ? failing[0]
                                              : Genie.TextUtils.Count(failing.Count, "load error") + ". " + failing[0]);
                else
                    Finish(r, Outcome.Done, "Loaded.");
            }
        }

        private static void RunScript(Plan plan, RunItem item, SubstepResult r) {
            r.Start  = DateTime.Now;
            Activity = "Running " + Path.GetFileName(item.Substep.File);
            Note(plan, r);
            Issue issue = Blocking(item.Substep);
            if (issue != null) {
                Finish(r, Outcome.Failed, issue.Text);
                _item++;
                Later(Next);
                return;
            }
            Announce(plan, new List<SubstepResult> { r });
            Substep s = item.Substep;
            string  problem;
            double? timeout                  = Issues.Seconds(s.Timeout);
            Genie.ProcessUtils.ScriptRun run = new Genie.ProcessUtils.ScriptRun {
                File            = s.File,
                Arguments       = s.Arguments,
                WorkingFolder   = s.WorkingFolder,
                Input           = s.Input.Length > 0 ? s.Input : null,
                TimeoutSeconds  = timeout ?? 0,
                SuccessCodes    = Genie.ProcessUtils.ParseExitCodes(s.SuccessCodes, out problem),
                ExpandVariables = true,
            };
            Add(run, "PL_SDK", Genie.GetSdkFolder());
            Add(run, "PL_JOB", plan.Job.Name);
            Add(run, "PL_STEP", item.StepNumber.ToString(CultureInfo.InvariantCulture));
            Add(run, "PL_STEPNAME", item.Step.Name);
            Add(run, "PL_SUBSTEP", item.Number.ToString(CultureInfo.InvariantCulture));
            foreach (Variable v in s.Variables) Add(run, v.Name, v.Value);

            _process = Genie.ProcessUtils.RunScript(run,
                                                    delegate(Genie.ProcessUtils.ProcessLine line) {
                                                        r.Lines.Add(line);
                                                        Raise();
                                                    },
                                                    delegate(Genie.ProcessUtils.ProcessResult result) {
                                                        _process = null;
                                                        Finish(r,
                                                               result.Cancelled   ? Outcome.Skipped
                                                               : result.IsSuccess ? Outcome.Done
                                                                                  : Outcome.Failed,
                                                               Describe(result, r));
                                                        _item++;
                                                        Next();
                                                    });
        }

        // What fails a substep before it starts. Info issues don't: a package in memory from another file is refused
        // by the load itself, which says why.
        private static Issue Blocking(Substep s) {
            Issue issue = Issues.Of(s);
            return issue != null && !issue.IsInfo ? issue : null;
        }

        // The failure reason is the script's last error line, if it wrote one.
        private static string Describe(Genie.ProcessUtils.ProcessResult result, SubstepResult r) {
            if (result.StartProblem != null) return result.StartProblem;
            if (result.Cancelled) return Current.IsStopped ? "Stopped." : "Skipped while running.";
            if (result.TimedOut) return "Timed out after " + Seconds(result.Duration) + ".";
            string code = "Exit code " + result.ExitCode;
            if (result.IsSuccess) return code + ".";
            Genie.ProcessUtils.ProcessLine error = r.Lines.FindLast(l => l.IsError);
            return error != null ? error.Text : code + ", not a success code.";
        }

        private static string Seconds(TimeSpan t) {
            return Genie.TextUtils.Count((int)Math.Round(t.TotalSeconds), "second");
        }

        private static void Add(Genie.ProcessUtils.ScriptRun run, string name, string value) {
            run.Variables.Add(new KeyValuePair<string, string>(name, value ?? ""));
        }

        private static void Note(Plan plan, SubstepResult r) {
            r.Outcome = Outcome.Running;
            Index(plan, r);
            Raise();
        }

        private static void Finish(SubstepResult r, Outcome outcome, string message) {
            r.Outcome = outcome;
            r.Message = message;
            if (r.Start == DateTime.MinValue) r.Start = DateTime.Now;
            r.End     = DateTime.Now;
            Plan plan = _plans[_plan];
            Index(plan, r);
            RunLog.Substep(plan.Result.Steps.Find(s => s.Substeps.Contains(r)), r);
            if (outcome == Outcome.Failed) {
                if (plan.Job.StopAtFirstFailure) _stopJob = true;
                if (Store.StopAllAtFirstFailure.Value) _stopRun = true;
            }
            Raise();
        }

        private static void Index(Plan plan, SubstepResult r) {
            _substeps[r.Key] = r;
            foreach (StepResult s in plan.Result.Steps) {
                if (s.Substeps.Contains(r)) _steps[s.StepId] = s;
            }
        }

        private static void Raise() {
            if (_changed == null)
                _changed = Genie.WorkUtils.Coalesce(delegate {
                    if (Changed != null) Changed();
                });
            _changed();
        }
    }

    // == Run log ======================================================================================
    // One file per editor session, Data\PackageLoader\Logs\<yyyy-MM-dd_HH-mm>.log, written as each substep ends so a
    // crash keeps what ran. Readable as text, and read back by the run history:
    //   Run 2026-11-16 23:41:02.104, on startup
    //     Job 1: AdventAngels
    //       Step 3: UI libraries
    //         Template: SDK UI libraries
    //         3.1 Started 23:41:02.104, package C:\Mods\gfxAlerts.upk
    //         3.1 Done 23:41:02.104 to 23:41:02.180, package C:\Mods\gfxAlerts.upk
    //           Message: Loaded.
    //           Arguments: x          a script's settings at the run
    //           out: a line the script wrote
    //           err: an error line, or a load error
    //           ign: a load error the substep ignores
    //           Id: e9b38902ec        the substep, on one with load errors, for the run history's Ignore
    //     Ended 23:41:04.700
    // Started is written before a substep runs, so one the editor died in still shows; its end line replaces it.
    // Ends in a batch aren't in order; the reader sorts them.

    internal static class RunLog {
        public const string Section = "Run History";

        private const string TimeFormat = "HH:mm:ss.fff";

        /// <summary>A run's start as its Run line writes it, to the millisecond.</summary>
        public const string StartFormat = "yyyy-MM-dd " + TimeFormat;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly HashSet<StepResult> _written = new HashSet<StepResult>();
        private static bool                         _failed;

        private static readonly Regex FileName = new Regex(@"^(\d{4}-\d\d-\d\d_\d\d-\d\d)(?: \((\d+)\))?\.log$");
        private static readonly Regex RunLine =
            new Regex(@"^Run (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})(?:, session (S\d+))?(, on startup)?$");
        private static readonly Regex JobLine     = new Regex(@"^(Job|Template) (\d+):\s?(.*)$");
        private static readonly Regex StepLine    = new Regex(@"^Step (\d+):\s?(.*)$");
        private static readonly Regex SubstepLine = new Regex(
            @"^(\d+)\.(\d+) (\w+) (\d\d:\d\d:\d\d\.\d{3}) to (\d\d:\d\d:\d\d\.\d{3}), (package|script) ?(.*)$");
        private static readonly Regex StartedLine =
            new Regex(@"^(\d+)\.(\d+) Started (\d\d:\d\d:\d\d\.\d{3}), (package|script) ?(.*)$");
        private static readonly Regex EndLine = new Regex(@"^Ended (\d\d:\d\d:\d\d\.\d{3})$");

        // By file: its last write and its runs, so reopening the run history doesn't read every file again.
        private static readonly Dictionary<string, KeyValuePair<DateTime, List<RunResult>>> _read =
            new Dictionary<string, KeyValuePair<DateTime, List<RunResult>>>(StringComparer.OrdinalIgnoreCase);

        public static string Folder {
            get { return Path.Combine(Genie.GetDataFolder(), "Logs"); }
        }

        /// <summary>This session's file. Not created until its first run.</summary>
        public static string SessionFile { get; private set; }

        /// <summary>Sessions to keep, this one included.</summary>
        public static int Keep {
            get { return Math.Max(1, Genie.ConfigUtils.GetInt("KeepSessions", 300, Section)); }
            set { Genie.ConfigUtils.SetInt("KeepSessions", value, Section); }
        }

        /// <summary>At launch: older files go, leaving room for this session's.</summary>
        public static void Begin() {
            Genie.FileUtils.KeepNewest(Folder, "*.log", Keep - 1);
            string stem = Genie.GetSessionStart().ToString("yyyy-MM-dd_HH-mm", Inv);
            SessionFile =
                Genie.FileUtils.GetFreePath(Path.Combine(Folder, stem + ".log")); // a second in one minute: " (2)"
        }

        // == Writing ==================================================================================

        /// <summary>A script's settings at the run, a line each ("Arguments: x"), kept with its result.</summary>
        public static List<string> ScriptSettings(Substep s) {
            List<string> lines = new List<string>();
            if (s.Kind != SubstepKind.Script) return lines;
            if (s.Arguments.Length > 0) lines.Add("Arguments: " + s.Arguments);
            if (s.WorkingFolder.Length > 0) lines.Add("Working folder: " + s.WorkingFolder);
            if (s.Timeout.Length > 0) lines.Add("Timeout: " + s.Timeout);
            if (s.SuccessCodes.Length > 0) lines.Add("Success codes: " + s.SuccessCodes);
            if (s.Input.Length > 0) {
                foreach (string answer in s.Input.Split('\n')) lines.Add("Input: " + answer.TrimEnd('\r'));
            }
            foreach (Variable v in s.Variables) lines.Add("Variable: " + v.Name + "=" + v.Value);
            return lines;
        }

        public static void Started(RunResult run) {
            _written.Clear();
            Append(RunText(run));
        }

        public static void Job(JobResult job) {
            Append(JobText(job));
        }

        public static void Substep(StepResult step, SubstepResult r) {
            StringBuilder b = new StringBuilder();
            if (step != null && _written.Add(step)) b.Append(StepText(step));
            b.Append(SubstepText(r));
            Append(b.ToString());
        }

        /// <summary>Before substeps run, as the editor may never reach their end.</summary>
        public static void Starting(JobResult job, List<SubstepResult> substeps) {
            StringBuilder b = new StringBuilder();
            foreach (SubstepResult r in substeps) {
                StepResult step = job.Steps.Find(s => s.Substeps.Contains(r));
                if (step != null && _written.Add(step)) b.Append(StepText(step));
                b.Append(StartedText(r));
            }
            Append(b.ToString());
        }

        public static void Ended(RunResult run) {
            Append(EndText(run));
        }

        /// <summary>A whole run as the log holds it, steps and substeps in order. Copy in the run history.</summary>
        public static string Text(RunResult run) {
            StringBuilder b = new StringBuilder(RunText(run));
            foreach (JobResult j in run.Jobs) {
                b.Append(JobText(j));
                foreach (StepResult s in j.Steps) {
                    if (s.Substeps.TrueForAll(r => r.Outcome == Outcome.NotRun)) continue;
                    b.Append(StepText(s));
                    foreach (SubstepResult r in s.Substeps) {
                        if (r.Outcome == Outcome.Unfinished)
                            b.Append(StartedText(r));
                        else if (r.Outcome != Outcome.NotRun && r.Outcome != Outcome.Running)
                            b.Append(SubstepText(r));
                    }
                }
            }
            if (run.IsFinished) b.Append(EndText(run));
            return b.ToString();
        }

        private static string RunText(RunResult run) {
            return Line(0, "Run " + run.Start.ToString(StartFormat, Inv) +
                               (run.Session != null ? ", session " + run.Session : "") +
                               (run.IsStartup ? ", on startup" : ""));
        }

        private static string JobText(JobResult job) {
            return Line(1, (job.IsTemplate ? "Template " : "Job ") + job.Number + ": " + job.Name);
        }

        private static string StepText(StepResult step) {
            return Line(2, "Step " + step.Number + ": " + step.Name) +
                   (step.TemplateName != null ? Line(3, "Template: " + step.TemplateName) : "");
        }

        private static string SubstepText(SubstepResult r) {
            StringBuilder b = new StringBuilder();
            b.Append(Line(3, r.Label + " " + r.Outcome + " " + r.Start.ToString(TimeFormat, Inv) + " to " +
                                 r.End.ToString(TimeFormat, Inv) + ", " +
                                 (r.Kind == SubstepKind.Script ? "script " : "package ") + r.File));
            if (r.Message.Length > 0) b.Append(Line(4, "Message: " + r.Message));
            foreach (string s in r.Settings) b.Append(Line(4, s));
            foreach (Genie.ProcessUtils.ProcessLine l in r.Lines)
                b.Append(Line(4, (l.IsError ? "err: " : "out: ") + l.Text));
            foreach (string e in r.Ignored) b.Append(Line(4, "ign: " + e));
            if (r.Kind == SubstepKind.Package && r.SubstepId != null && r.Lines.Count + r.Ignored.Count > 0)
                b.Append(Line(4, "Id: " + r.SubstepId));
            return b.ToString();
        }

        private static string StartedText(SubstepResult r) {
            return Line(3, r.Label + " Started " + r.Start.ToString(TimeFormat, Inv) + ", " +
                               (r.Kind == SubstepKind.Script ? "script " : "package ") + r.File);
        }

        private static string EndText(RunResult run) {
            return Line(1, "Ended " + run.End.ToString(TimeFormat, Inv));
        }

        private static string Line(int depth, string text) {
            return new string(' ', depth * 2) + text.Replace("\r", "").Replace('\n', ' ') + "\r\n";
        }

        private static void Append(string text) {
            if (SessionFile == null) return;
            string why = Genie.FileUtils.TryWriteText(SessionFile, text, true);
            if (why == null || _failed) return; // logged once a session
            _failed = true;
            Genie.Log("Couldn't write the run log " + SessionFile + ": " + why +
                      " This session's runs show in the run history until the editor closes.");
        }

        // == Reading ==================================================================================

        /// <summary>Earlier sessions with a log file, newest first. Any thread: it can read hundreds of
        /// files.</summary>
        public static List<SessionLog> PastSessions(string folder) {
            List<SessionLog> found = new List<SessionLog>();
            string[] files;
            try {
                files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.log") : new string[0];
            } catch (Exception ex) {
                Genie.Log("Couldn't list the run logs in " + folder + ": " + ex.Message);
                return found;
            }
            foreach (string file in files) {
                Match    m = FileName.Match(Path.GetFileName(file));
                DateTime start;
                if (!m.Success || Genie.FileUtils.IsSamePath(file, SessionFile) ||
                    !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH-mm", Inv, DateTimeStyles.None, out start))
                    continue;
                List<RunResult> runs = Read(file);
                if (runs.Count == 0) continue;
                found.Add(new SessionLog {
                    File  = file,
                    Start = start,
                    Order = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, Inv) : 1,
                    Runs  = runs,
                });
            }
            found.Sort((a, b) => a.Start != b.Start ? b.Start.CompareTo(a.Start) : b.Order.CompareTo(a.Order));
            return found;
        }

        /// <summary>
        /// A run in a log file by its start, or null if the file or the run is gone. The same object the run history
        /// lists while the file is unchanged, so it can be shown there.
        /// </summary>
        public static RunResult FindRun(string file, DateTime start) {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            return Read(file).Find(r => r.Start == start);
        }

        // A file's runs, oldest first. Unreadable lines are skipped; a run with no Ended line didn't finish.
        // Locked: the run history lists sessions on a background thread while FindRun asks on the editor's.
        private static List<RunResult> Read(string file) {
            lock (_read) return ReadUnlocked(file);
        }

        private static List<RunResult> ReadUnlocked(string file) {
            KeyValuePair<DateTime, List<RunResult>> known;
            DateTime                                written;
            string[] lines;
            try {
                written = File.GetLastWriteTimeUtc(file);
                if (_read.TryGetValue(file, out known) && known.Key == written) return known.Value;
                lines = File.ReadAllLines(file);
            } catch (Exception ex) {
                Genie.Log("Couldn't read the run log " + file + ": " + ex.Message);
                return new List<RunResult>();
            }
            List<RunResult> runs = new List<RunResult>();
            RunResult       run  = null;
            JobResult       job  = null;
            StepResult      step = null;
            SubstepResult   sub  = null;
            DateTime        last = DateTime.MinValue;
            foreach (string raw in lines) {
                int    indent = raw.Length - raw.TrimStart(' ').Length, depth = indent / 2;
                string text = raw.Substring(indent);
                Match  m;
                if (depth == 0 && (m = RunLine.Match(text)).Success) {
                    run = new RunResult {
                        Session   = m.Groups[2].Success ? m.Groups[2].Value : null,
                        IsStartup = m.Groups[3].Success,
                        LogFile   = file,
                    };
                    DateTime.TryParseExact(m.Groups[1].Value, StartFormat, Inv, DateTimeStyles.None, out run.Start);
                    runs.Add(run);
                    job  = null;
                    step = null;
                    sub  = null;
                    last = run.Start;
                } else if (run == null) {
                    continue;
                } else if (depth == 1 && (m = JobLine.Match(text)).Success) {
                    job = new JobResult {
                        IsTemplate = m.Groups[1].Value == "Template",
                        Number     = int.Parse(m.Groups[2].Value, Inv),
                        Name       = m.Groups[3].Value,
                    };
                    run.Jobs.Add(job);
                    step = null;
                    sub  = null;
                } else if (depth == 1 && (m = EndLine.Match(text)).Success) {
                    run.End        = TimeOfRun(m.Groups[1].Value, ref last);
                    run.IsFinished = true;
                } else if (job == null) {
                    continue;
                } else if (depth == 2 && (m = StepLine.Match(text)).Success) {
                    step      = StepOf(job, int.Parse(m.Groups[1].Value, Inv));
                    step.Name = m.Groups[2].Value;
                    sub       = null;
                } else if (depth == 3 && step != null && text.StartsWith("Template: ")) {
                    step.TemplateName = text.Substring("Template: ".Length);
                } else if (depth == 3 && (m = SubstepLine.Match(text)).Success) {
                    Outcome outcome;
                    if (!Enum.TryParse(m.Groups[3].Value, out outcome)) outcome = Outcome.NotRun;
                    sub = new SubstepResult {
                        Label   = m.Groups[1].Value + "." + m.Groups[2].Value,
                        Outcome = outcome,
                        Start   = TimeOfRun(m.Groups[4].Value, ref last),
                        End     = TimeOfRun(m.Groups[5].Value, ref last),
                        Kind    = m.Groups[6].Value == "script" ? SubstepKind.Script : SubstepKind.Package,
                        File    = m.Groups[7].Value,
                    };
                    step = StepOf(job, int.Parse(m.Groups[1].Value, Inv));
                    step.Substeps.RemoveAll(s => s.Label == sub.Label); // its Started line
                    step.Substeps.Add(sub);
                } else if (depth == 3 && (m = StartedLine.Match(text)).Success) {
                    sub = new SubstepResult {
                        Label   = m.Groups[1].Value + "." + m.Groups[2].Value,
                        Outcome = Outcome.Unfinished,
                        Message = "It started and never ended: the editor hung or closed.",
                        Start   = TimeOfRun(m.Groups[3].Value, ref last),
                        Kind    = m.Groups[4].Value == "script" ? SubstepKind.Script : SubstepKind.Package,
                        File    = m.Groups[5].Value,
                    };
                    sub.End = sub.Start;
                    step    = StepOf(job, int.Parse(m.Groups[1].Value, Inv));
                    step.Substeps.Add(sub);
                } else if (depth == 4 && sub != null) {
                    if (text.StartsWith("Message: "))
                        sub.Message = text.Substring("Message: ".Length);
                    else if (text.StartsWith("Id: "))
                        sub.SubstepId = text.Substring("Id: ".Length);
                    else if (text.StartsWith("ign: "))
                        sub.Ignored.Add(text.Substring("ign: ".Length));
                    else if (text.StartsWith("out: ") || text.StartsWith("err: "))
                        sub.Lines.Add(
                            new Genie.ProcessUtils.ProcessLine { IsError = text[0] == 'e', Text = text.Substring(5) });
                    else
                        sub.Settings.Add(text);
                }
            }
            foreach (RunResult r in runs) Settle(r);
            _read[file] = new KeyValuePair<DateTime, List<RunResult>>(written, runs);
            return runs;
        }

        // Steps and substeps in number order, and each job's and the run's times from its substeps.
        private static void Settle(RunResult run) {
            DateTime end = run.Start;
            foreach (JobResult j in run.Jobs) {
                j.Steps.Sort((a, b) => a.Number.CompareTo(b.Number));
                foreach (StepResult s in j.Steps) s.Substeps.Sort((a, b) => Sub(a.Label).CompareTo(Sub(b.Label)));
                j.Start = j.End = DateTime.MinValue;
                foreach (SubstepResult r in j.All()) {
                    if (j.Start == DateTime.MinValue || r.Start < j.Start) j.Start = r.Start;
                    if (r.End > j.End) j.End = r.End;
                }
                if (j.Start == DateTime.MinValue) j.Start = j.End = run.Start;
                if (j.End > end) end = j.End;
            }
            if (!run.IsFinished) run.End = end;
        }

        private static StepResult StepOf(JobResult job, int number) {
            StepResult step = job.Steps.Find(s => s.Number == number);
            if (step != null) return step;
            step = new StepResult { Number = number, Name = "" };
            job.Steps.Add(step);
            return step;
        }

        private static int Sub(string label) {
            int dot = label.IndexOf('.'), n;
            return int.TryParse(dot >= 0 ? label.Substring(dot + 1) : label, out n) ? n : 0;
        }

        // A time of day on the run's day, or the next day's if it went past midnight since the last time read.
        private static DateTime TimeOfRun(string time, ref DateTime last) {
            TimeSpan t;
            if (!TimeSpan.TryParseExact(time, @"hh\:mm\:ss\.fff", Inv, out t)) return last;
            DateTime at = last.Date + t;
            if (at < last.AddHours(-12)) at = at.AddDays(1);
            if (at > last) last = at;
            return at;
        }
    }

    internal sealed class SessionLog {
        public string          File;
        public DateTime        Start;
        public int             Order; // 2 for a second launch in the same minute
        public List<RunResult> Runs;
    }

    // == Startup jobs =================================================================================
    // Run once the editor is ready. While they run, a marker file says how far they got: written as they start,
    // rewritten before each script or package batch, deleted when their run ends, pass or fail. Still there at the
    // next launch, it means that launch hung or closed during them, so the prompt asks before running them again.

    internal static class Startup {
        private static RunResult _run; // holding the startup jobs, until it ends

        public static void Initialize() {
            Runner.Starting += delegate(JobResult job, List<SubstepResult> substeps) {
                if (_run != null && _run == Runner.Current) StartupMarker.Write(_run, job, substeps);
            };
            Runner.Finished += delegate(RunResult run) {
                if (run != _run) return;
                _run = null;
                StartupMarker.Delete();
            };
            Genie.WhenEditorReady(OnEditorReady);
        }

        // Off, or none to run: a marker from before has nothing left to ask about.
        private static void OnEditorReady() {
            if (!Store.StartupJobs.Value || Store.StartupOrder().Count == 0) {
                StartupMarker.Delete();
                return;
            }
            StartupMarker last = StartupMarker.Read();
            if (last != null)
                StartupPrompt.Show(last);
            else
                Run();
        }

        /// <summary>Every startup job in Order, as one run: now, or once the runs in the line have.</summary>
        public static void Run() {
            Runner.Start(Store.StartupOrder(), true, delegate(RunResult run) {
                _run = run;
                StartupMarker.Write(run, null, null);
            });
        }
    }

    // Data\PackageLoader\StartupRun.txt, a Key=Value line each. A file, not a setting: a settings backup taken while
    // it existed would bring it back on a restore.
    internal sealed class StartupMarker {
        public string    LogFile, JobName, Label, SubstepFile;
        public int       JobNumber, Batch; // Batch: substeps started together, more than 1 for a package batch
        public DateTime  When;             // the run's start, else the marker's last write
        public RunResult Run;              // as its log holds it, or null if the log is gone

        private static readonly CultureInfo  Inv = CultureInfo.InvariantCulture;
        private static bool                  _failed;

        private static string FilePath {
            get { return Path.Combine(Genie.GetDataFolder(), "StartupRun.txt"); }
        }

        /// <param name="substeps">What's about to run, or null while nothing has yet.</param>
        public static void Write(RunResult run, JobResult job, List<SubstepResult> substeps) {
            StringBuilder b = new StringBuilder();
            Line(b, "Log", run.LogFile);
            Line(b, "Run", run.Start.ToString(RunLog.StartFormat, Inv));
            if (job != null) {
                Line(b, "Job", job.Name);
                Line(b, "JobNumber", job.Number.ToString(Inv));
            }
            if (substeps != null && substeps.Count > 0) {
                Line(b, "Substep", substeps[0].Label);
                Line(b, "File", substeps[0].File);
                Line(b, "Batch", substeps.Count.ToString(Inv));
            }
            string why = Genie.FileUtils.TryWriteText(FilePath, b.ToString());
            if (why == null || _failed) return; // logged once a session
            _failed = true;
            Genie.Log("Couldn't write " + FilePath + ": " + why + " If the editor hangs or closes during the startup " +
                      "jobs, the next launch runs them again without asking.");
        }

        /// <summary>What the last launch left, or null: its startup jobs ended, or never began.</summary>
        public static StartupMarker Read() {
            string[] lines;
            DateTime written;
            try {
                if (!File.Exists(FilePath)) return null;
                lines   = File.ReadAllLines(FilePath);
                written = File.GetLastWriteTime(FilePath);
            } catch (Exception ex) {
                Genie.Log("Couldn't read " + FilePath + ": " + ex.Message);
                return null;
            }
            StartupMarker m     = new StartupMarker { When = written };
            bool          known = false; // the run's start, to find it in its log
            foreach (string line in lines) {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string value = line.Substring(eq + 1);
                switch (line.Substring(0, eq)) {
                    case "Log":
                        m.LogFile = value;
                        break;
                    case "Run":
                        known = DateTime.TryParseExact(value, RunLog.StartFormat, Inv, DateTimeStyles.None, out m.When);
                        if (!known) m.When = written;
                        break;
                    case "Job":
                        m.JobName = value;
                        break;
                    case "JobNumber":
                        int.TryParse(value, NumberStyles.Integer, Inv, out m.JobNumber);
                        break;
                    case "Substep":
                        m.Label = value;
                        break;
                    case "File":
                        m.SubstepFile = value;
                        break;
                    case "Batch":
                        int.TryParse(value, NumberStyles.Integer, Inv, out m.Batch);
                        break;
                }
            }
            if (known) m.Run = RunLog.FindRun(m.LogFile, m.When);
            // Its run ended after all, and only the marker's delete failed: nothing to ask about.
            if (m.Run != null && m.Run.IsFinished) {
                Delete();
                return null;
            }
            return m;
        }

        public static void Delete() {
            try {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            } catch (Exception ex) {
                Genie.Log("Couldn't delete " + FilePath + ": " + ex.Message);
            }
        }

        private static void Line(StringBuilder b, string key, string value) {
            b.Append(key).Append('=').Append((value ?? "").Replace("\r", "").Replace('\n', ' ')).Append("\r\n");
        }
    }

    // == Unfinished startup prompt ====================================================================
    // Shown at launch in place of the startup jobs while the last launch's marker is there. Every way out deletes the
    // marker; closing it by the X, or Enter or Esc, is Skip this time.

    internal sealed class StartupPrompt : UserControl {
        private readonly GenieWindow _window;
        private bool                 _answered;

        public static void Show(StartupMarker marker) {
            GenieWindow w        = Genie.WindowUtils.CreateWindow("Package Loader", 480, 270, "Unfinished startup");
            w.MinWidth           = 420;
            w.FitHeightToContent = true;
            w.ShowAddonInfo      = false; // a subwindow
            w.RememberIfOpen     = false; // a question for one launch
            w.Content            = new StartupPrompt(w, marker);
            w.Show();
        }

        private StartupPrompt(GenieWindow window, StartupMarker marker) {
            _window         = window;
            window.Closing += delegate {
                if (_answered) return;
                _answered = true;
                StartupMarker.Delete();
            };

            // [!] Startup jobs didn't finish last time
            //     They stopped at [job] › [1.1 file] on [date]. ...
            //     Before it: [job] [5 done] [1 failed]
            //     See that run in the run history
            StackPanel text = new StackPanel();
            GenieParagraph stopped = Genie.ControlUtils.CreateParagraph(Stopped(marker));
            stopped.Margin         = new Thickness(0, 8, 0, 0);
            text.Children.Add(stopped);
            object[] before = Before(marker);
            if (before.Length > 0) {
                GenieParagraph ran = Genie.ControlUtils.CreateParagraph(before);
                ran.FontSize       = Genie.Theme.FontSizeLabel;
                ran.Foreground     = Genie.Theme.TextSecondary;
                ran.Margin         = new Thickness(0, 8, 0, 0);
                text.Children.Add(ran);
            }
            if (marker.Run != null) {
                RunResult last = marker.Run;
                TextBlock link = Genie.ControlUtils.CreateLink("See that run in the run history",
                                                               delegate { Windows.ShowHistory(last); },
                                                               "Everything up to where it stopped is there");
                link.Margin              = new Thickness(0, 8, 0, 0);
                link.HorizontalAlignment = HorizontalAlignment.Left;
                text.Children.Add(link);
            }

            // [Turn off startup jobs] ............ [Skip this time] [Run them]
            Button turnOff = Genie.ControlUtils.CreateButton(
                "Turn off startup jobs", size: Genie.ControlUtils.ButtonSize.Small,
                tooltip: "Turns off Enable startup jobs in the gear menu. Nothing runs at launch until you turn it " +
                    "back on.",
                onClick: delegate { Answer(delegate { Store.StartupJobs.Value = false; }); });
            Button skip = Genie.ControlUtils.CreateButton(
                "Skip this time", size: Genie.ControlUtils.ButtonSize.Small,
                tooltip: "Run nothing now. Startup jobs run again at the next launch.",
                onClick: delegate { Answer(null); });
            skip.IsDefault = true;
            skip.IsCancel  = true;
            Button run =
                Genie.ControlUtils.CreateButton("Run them", size: Genie.ControlUtils.ButtonSize.Small,
                                                tooltip: "Run every startup job from the start, as at any launch",
                                                onClick: delegate { Answer(Startup.Run); });
            run.Margin     = new Thickness(0);
            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(skip);
            right.Children.Add(run);
            DockPanel bar = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            turnOff.HorizontalAlignment = HorizontalAlignment.Left;
            bar.Children.Add(turnOff);
            Content = Genie.ControlUtils.CreateDialogPanel(
                Genie.ControlUtils.CreateDialogIcon(Genie.ControlUtils.TagTone.Warning,
                                                    Genie.ControlUtils.DialogSymbol.Warning),
                "Startup jobs didn\u2019t finish last time", null, text, bar);
            Loaded  += delegate {
                skip.Focus();
            };
        }

        private void Answer(Action then) {
            if (_answered) return;
            _answered = true;
            StartupMarker.Delete();
            if (then != null) then();
            _window.Close();
        }

        // "They stopped at [job] › [1.1 file] on [date]." Less where the marker got no further.
        private static object[] Stopped(StartupMarker m) {
            const string Again = " The editor hung or closed during it, so running them again may stop there too.";
            GenieTag     when  = Tag(m.When.ToString("MMM d, HH:mm", CultureInfo.CurrentCulture),
                                     Genie.ControlUtils.TagTone.Neutral, "When that launch ran its startup jobs");
            if (m.JobName == null)
                return new object[] { "They began on ", when, " and never ended." + Again };
            GenieTag job = JobTag(m.JobName, "The job that was running");
            if (m.Label == null) return new object[] { "They stopped in ", job, " on ", when, "." + Again };
            string what = m.Batch > 1 ? "Substep " + m.Label + " and the " +
                                            Genie.TextUtils.Count(m.Batch - 1, "package") + " loading with it"
                                      : "Substep " + m.Label;
            GenieTag at = Tag(m.Label + " " + Path.GetFileName(m.SubstepFile ?? ""), Genie.ControlUtils.TagTone.Warning,
                              what + " started and never ended: " + m.SubstepFile);
            at.TextFont   = Genie.Theme.MonoFont;
            return new object[] { "They stopped at ", job, " \u203a ", at, " on ", when, "." + Again };
        }

        // "Before it: [job] [5 done] [1 failed]", a job each, the stopped one's ended substeps included. Empty if
        // nothing ran, or its log is gone.
        private static object[] Before(StartupMarker m) {
            List<object> parts = new List<object>();
            if (m.Run == null) return parts.ToArray();
            foreach (JobResult j in m.Run.Jobs) {
                if (m.JobNumber > 0 && j.Number > m.JobNumber) break;
                int done = j.Count(Outcome.Done), failed = j.Count(Outcome.Failed);
                if (done + failed == 0) continue;
                parts.Add(parts.Count == 0 ? "Before it: " : "  ");
                parts.Add(JobTag(j.Name, null));
                if (done > 0) {
                    parts.Add(" ");
                    parts.Add(Tag(done + " done", Genie.ControlUtils.TagTone.Success, null));
                }
                if (failed > 0) {
                    parts.Add(" ");
                    parts.Add(Tag(failed + " failed", Genie.ControlUtils.TagTone.Danger, null));
                }
            }
            return parts.ToArray();
        }

        private static GenieTag JobTag(string name, string tooltip) {
            GenieTag tag   = Tag(name, Genie.ControlUtils.TagTone.Info, tooltip);
            tag.TextWeight = FontWeights.SemiBold;
            return tag;
        }

        private static GenieTag Tag(string text, Genie.ControlUtils.TagTone tone, string tooltip) {
            return Genie.ControlUtils.CreateTag(text, tone, tooltip);
        }
    }

    // == Running window ===============================================================================
    // Opens as a pill when a run starts and expands into a window. It lives on a thread of its own, so it keeps
    // drawing, ticking and taking clicks while a package load holds the editor's. The two threads share only snapshots,
    // never changed once made. Clicks go back through Genie.Dispatcher, so during a load they wait for it to end.

    internal sealed class SubstepSnapshot {
        public string      Label, File, Tip;
        public SubstepKind Kind;
        public Outcome     Outcome;
        public bool        IsOff;
        public DateTime    Start;
        public double? Timeout;
    }

    internal sealed class StepSnapshot {
        public int     Number;
        public string  Name, Tip;
        public Outcome Outcome;

        public readonly List<SubstepSnapshot> Substeps = new List<SubstepSnapshot>();
    }

    internal sealed class JobSnapshot {
        public int      Number, Done, Total, Failed; // substeps that will run; off ones aren't counted
        public bool     IsTemplate;
        public string   Name, Tip;
        public Outcome  Outcome;
        public DateTime Start;

        public readonly List<StepSnapshot> Steps = new List<StepSnapshot>();
    }

    // A line of runs, as the running window shows it: its jobs numbered across the line, "JOB 2 OF 3".
    internal sealed class RunSnapshot {
        public RunLine   Line; // these two only to hand back to the editor's thread: they change there, so never
        public RunResult Run;  // read them here. Run: the one going, or the last once the line is done
        public bool      IsFinished, IsStopped, IsStartup, CanSkip;
        public string    Activity;
        public int       Done, Total, Failed;
        public int       Current; // index of the running job, or the last once finished

        public readonly List<JobSnapshot> Jobs = new List<JobSnapshot>();

        public JobSnapshot Job {
            get { return Jobs[Current]; }
        }

        public static RunSnapshot Of(RunLine line) {
            bool        going = !line.IsFinished && line.Runs.Contains(Runner.Current);
            RunResult   run   = going ? Runner.Current : line.Runs[line.Runs.Count - 1];
            RunSnapshot s     = new RunSnapshot {
                Line       = line,
                Run        = run,
                IsFinished = line.IsFinished,
                IsStopped  = line.IsStopped,
                IsStartup  = run.IsStartup,
                CanSkip    = Runner.IsScriptRunning,
                Activity   = Runner.Activity,
            };
            JobResult running = line.IsFinished ? null : Runner.RunningResult;
            foreach (RunResult r in line.Runs) {
                foreach (JobResult j in r.Jobs) {
                    if (j == running) s.Current = s.Jobs.Count;
                    JobSnapshot job = Of(j, s.Jobs.Count + 1, j == running, r.IsStopped);
                    s.Jobs.Add(job);
                    s.Done   += job.Done;
                    s.Total  += job.Total;
                    s.Failed += job.Failed;
                }
            }
            if (running == null) s.Current = Math.Max(0, s.Jobs.Count - 1);
            return s;
        }

        private static JobSnapshot Of(JobResult j, int number, bool isRunning, bool stopped) {
            List<SubstepResult> live = j.All().FindAll(r => r.WillRun);
            JobSnapshot         job  = new JobSnapshot {
                Number     = number,
                IsTemplate = j.IsTemplate,
                Name       = j.Name,
                Start      = j.Start,
                Total      = live.Count,
                Done       = live.FindAll(HasEnded).Count,
                Failed     = live.FindAll(r => r.Outcome == Outcome.Failed).Count,
                Outcome    = isRunning ? Outcome.Running : StepResult.Combine(live),
            };
            job.Tip = Tip(job.Outcome, job.Failed, stopped);
            foreach (StepResult r in j.Steps) {
                List<SubstepResult> on   = r.Substeps.FindAll(x => x.WillRun);
                StepSnapshot        step = new StepSnapshot {
                    Number  = r.Number,
                    Name    = r.Name,
                    Outcome = StepResult.Combine(on),
                };
                step.Tip = Tip(step.Outcome, on.FindAll(x => x.Outcome == Outcome.Failed).Count, stopped);
                foreach (SubstepResult x in r.Substeps) step.Substeps.Add(Of(x, stopped));
                job.Steps.Add(step);
            }
            return job;
        }

        private static SubstepSnapshot Of(SubstepResult r, bool stopped) {
            bool off = !r.WillRun || r.IsOff;
            return new SubstepSnapshot {
                Label   = r.Label,
                File    = r.File,
                Kind    = r.Kind,
                Outcome = off ? Outcome.Skipped : r.Outcome,
                IsOff   = off,
                Start   = r.Start,
                Timeout = r.Source != null ? Issues.Seconds(r.Source.Timeout) : null,
                Tip     = off                            ? "Off: skipped"
                          : r.Outcome == Outcome.Running ? (r.Kind == SubstepKind.Script ? "Running" : "Loading")
                          : r.Outcome == Outcome.NotRun  ? (stopped ? "Not run: stopped" : "Waiting")
                          : r.Outcome == Outcome.Failed  ? "Failed: " + r.Message
                                                         : r.Message,
            };
        }

        private static bool HasEnded(SubstepResult r) {
            return r.Outcome != Outcome.NotRun && r.Outcome != Outcome.Running;
        }

        private static string Tip(Outcome o, int failed, bool stopped) {
            switch (o) {
                case Outcome.Running:
                    return "Running";
                case Outcome.Failed:
                    return Genie.TextUtils.Count(failed, "substep") + " failed";
                case Outcome.Done:
                    return "Done";
                case Outcome.Skipped:
                    return stopped ? "Not run: stopped" : "Skipped";
                default:
                    return stopped ? "Not run: stopped" : "Waiting";
            }
        }
    }

    internal sealed class LauncherJob {
        public string                     Id, Name, Detail, LastRun, LastRunTip, Tip;
        public Genie.ControlUtils.TagTone Tone;
    }

    // The launcher's content, made on the editor's thread as the run's is.
    internal sealed class LauncherSnapshot {
        public readonly List<LauncherJob> Startup = new List<LauncherJob>(), OnDemand = new List<LauncherJob>();

        public bool   IsStartupOff;
        public string LastRun, LastFailed, LastRunTip; // the pill's: LastFailed is null without failures

        public static LauncherSnapshot Make() {
            LauncherSnapshot s = new LauncherSnapshot { IsStartupOff = !Store.StartupJobs.Value };
            foreach (Job j in Store.StartupOrder()) s.Startup.Add(Of(j, j.Order + ". "));
            foreach (Job j in Store.OnDemandOrder()) s.OnDemand.Add(Of(j, ""));
            s.FindLastRun();
            return s;
        }

        private static LauncherJob Of(Job j, string prefix) {
            LauncherJob job = new LauncherJob {
                Id     = j.Id,
                Name   = prefix + j.Name,
                Detail = Genie.TextUtils.Count(j.Steps.Count, "step") + " · " +
                         Genie.TextUtils.Count(j.SubstepCount, "substep"),
                LastRun    = j.LastRun.HasValue ? Genie.TextUtils.When(j.LastRun.Value) : "Never run",
                LastRunTip = Genie.TextUtils.WhenInFull(j.LastRun),
            };
            JobListRow.Status(j, out job.Tone, out job.Tip);
            return job;
        }

        // This session's latest run; before any, the job that ran last, as earlier results aren't in memory.
        private void FindLastRun() {
            if (Runner.History.Count > 0) {
                RunResult run = Runner.History[Runner.History.Count - 1];
                int       n   = run.Count(Outcome.Failed);
                LastRun       = "Last run " + Genie.TextUtils.When(run.Start) + ", " + run.Title;
                LastFailed    = n > 0 ? ", " + n + " failed" : null;
                LastRunTip    = Genie.TextUtils.WhenInFull(run.Start);
                return;
            }
            Job last = null;
            foreach (Job j in Store.Jobs) {
                if (j.LastRun.HasValue && (last == null || j.LastRun > last.LastRun)) last = j;
            }
            LastRun    = last != null ? "Last run " + Genie.TextUtils.When(last.LastRun.Value) + ", " + last.Name
                                      : "No runs yet";
            LastRunTip = last != null ? Genie.TextUtils.WhenInFull(last.LastRun) : null;
        }
    }

    // The editor's side: follows the runner and the jobs, and keeps the latest of each for the window's thread to pick
    // up. The window is open while there's a run to show or the launcher is on.
    internal static class RunningWindow {
        public const double Width = 420, MinWidth = 340, MaxWidth = 900, PillHeight = 52, FullHeight = 560;

        // Editor's thread only.
        private static bool      _isOpen, _isReady;
        private static RunLine   _openedFor;
        private static Action    _jobsChanged;

        // Written on one thread, read on the other.
        private static volatile RunSnapshot      _latestRun;
        private static volatile LauncherSnapshot _latestJobs;
        private static volatile Dispatcher       _thread;
        private static volatile RunLine          _dismissed; // the last line closed or left: it doesn't come back

        private static RunningPanel _panel; // the window's thread only

        public static void Initialize() {
            _jobsChanged               = Genie.WorkUtils.Coalesce(Update);
            Runner.Changed            += Update;
            Store.Changed             += _jobsChanged;
            Store.StartupJobs.Changed += delegate {
                _jobsChanged();
            };
            Store.ShowLauncher.Changed += delegate {
                _jobsChanged();
            };
            Genie.WhenEditorReady(delegate {
                _isReady = true;
                Update();
            });
        }

        /// <summary>On the window's thread: the line was closed, or left for the launcher.</summary>
        public static void Dismiss(RunLine line) {
            if (line != null) _dismissed = line;
        }

        // Runner.Changed runs before a load starts (Background goes ahead of RunAfterRender's ContextIdle), so the
        // window has what's loading before the editor's thread stops.
        private static void Update() {
            if (!_isReady) return;
            RunLine line = Runner.Line;
            _latestRun   = line != null && line != _dismissed ? RunSnapshot.Of(line) : null;
            _latestJobs  = Store.ShowLauncher.Value ? LauncherSnapshot.Make() : null;
            if (_isOpen) {
                Dispatcher thread = _thread;
                if (thread != null) thread.BeginInvoke(new Action(Deliver));
            } else if (_latestRun != null || _latestJobs != null) {
                Open(line);
            }
        }

        private static void Open(RunLine line) {
            _isOpen    = true;
            _openedFor = line;
            Genie.WindowUtils.ShowPillOnOwnThread("Package Loader: running", Width,
                                                  PillHeight, delegate(GenieWindow window) {
                                                      window.Closed += delegate {
                                                          OnClosed();
                                                      };
                                                      _panel         = new RunningPanel(window);
                                                      window.Content = _panel;
                                                      _thread        = Dispatcher.CurrentDispatcher;
                                                      Deliver();
                                                  }, "Running");
        }

        // On the window's thread. Several can queue up: each shows the latest, so the rest change nothing.
        private static void Deliver() {
            if (_panel == null) return;
            RunSnapshot run = _latestRun;
            _panel.Show(run != null && run.Line != _dismissed ? run : null, _latestJobs);
        }

        // On the window's thread. Only a line that came while it closed reopens it, so one failing to build can't loop.
        private static void OnClosed() {
            RunLine shown = _panel != null ? _panel.ShownLine : null;
            bool    built = _panel != null;
            _panel        = null;
            _thread       = null;
            Dismiss(shown);
            Genie.Dispatcher.BeginInvoke(new Action(delegate {
                _isOpen = false;
                if (!built) Dismiss(_openedFor);
                if (Runner.Line != null && Runner.Line != _dismissed) Update();
            }));
        }
    }

    // Runs on the window's thread, from snapshots alone. One window, three views:
    //   Launcher: [mark] PACKAGE LOADER / 4 jobs  Last run 23:44, name  [chevron], opening to the job list
    //   Pill:   [mark] PACKAGE LOADER · JOB 1 OF 2  1 failed           | 5 of 12 [expand] [close]
    //                  AdventAngels  4 / 6  Loading 3 packages · 0:42
    //   Window: title bar, header, progress, every job opening to its steps and substeps, Up next, Skip and Stop.
    internal sealed class RunningPanel : UserControl {
        private const int    CloseAfterSeconds = 5;   // a clean run
        private const double MaxListHeight     = 420; // the launcher's jobs; more scroll

        private enum View { Launcher, Pill, Full }

        private sealed class LineRow {
            public GenieRow        Row;
            public GenieStatusMark Mark;
            public TextBlock       Note;
        }

        private sealed class JobRow {
            public GenieRow        Row;
            public GenieStatusMark Mark;
            public TextBlock       Count;

            public readonly List<LineRow> Steps    = new List<LineRow>();
            public readonly List<LineRow> Substeps = new List<LineRow>();
        }

        private readonly GenieWindow      _window;
        private readonly FrameworkElement _pill, _full;

        // The pill
        private readonly GenieStatusMark  _pillMark = StepsBox.NewMark();
        private readonly TextBlock        _pillCaption, _pillFailed, _pillJob, _pillJobCount, _pillNow, _pillCount;
        private readonly GenieProgressBar _pillBar;
        private readonly Border           _pillClose;

        // The window
        private readonly GenieHeader      _header;
        private readonly GenieCountPill   _onStartup, _failed;
        private readonly GenieProgressBar _bar;
        private readonly TextBlock        _now, _count, _upNext, _closeNote;
        private readonly FrameworkElement _upNextStrip, _runningBar, _finishedBar;
        private readonly Border           _titleBar, _titleClose;
        private readonly Button           _skip, _stop;
        private readonly GenieRowList     _rows;
        private readonly ScrollViewer     _scroll;

        private readonly List<JobRow> _jobs = new List<JobRow>();

        // Opened or closed by a click, by job ("2") or step ("2.3"): sticks for the run.
        private readonly Dictionary<string, bool> _chosen = new Dictionary<string, bool>();

        private readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        // The launcher
        private readonly FrameworkElement  _launcher, _launcherTop, _listPart;
        private readonly TextBlock         _jobCount, _lastRun;
        private readonly GenieExpandButton _listChevron;
        private readonly GenieRowList      _jobList;

        private LauncherSnapshot _jobsNow; // null while the launcher is off
        private View             _view;
        private bool             _isListOpen;

        private RunSnapshot _run;
        private GenieRow    _inView;     // the running substep's row, last brought into view
        private bool        _isSetting;  // opening rows from here, not a click
        private bool        _isStopping; // Stop was clicked: the run ends when the editor's thread gets to it
        private DateTime? _closeAt;

        public RunningPanel(GenieWindow window) {
            _window     = window;
            window.EnableWidthDrag(RunningWindow.MinWidth, RunningWindow.MaxWidth);
            _tick.Tick += delegate {
                OnTick();
            };
            window.Closed += delegate {
                _tick.Stop();
            };

            // == The pill ==
            _pillCaption           = Genie.ControlUtils.CreateMutedText("", Genie.Theme.FontSizeCaption);
            _pillFailed            = Genie.ControlUtils.CreateMutedText("", Genie.Theme.FontSizeCaption);
            _pillFailed.Foreground = Genie.Theme.DangerTagText;
            _pillFailed.Margin     = new Thickness(5, 0, 0, 0);
            FrameworkElement mark  = Genie.ControlUtils.CreatePackageIcon(11, Genie.Theme.TypeTagText);
            mark.Margin            = new Thickness(0, 0, 5, 0);
            StackPanel caption     = new StackPanel { Orientation = Orientation.Horizontal };
            caption.Children.Add(mark);
            caption.Children.Add(_pillCaption);
            caption.Children.Add(_pillFailed);

            _pillJob = new TextBlock {
                FontSize     = Genie.Theme.FontSizeBody,
                FontWeight   = FontWeights.SemiBold,
                MaxWidth     = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _pillJobCount         = Genie.ControlUtils.CreateMutedText("");
            _pillJobCount.Margin  = new Thickness(6, 0, 6, 0);
            _pillNow              = Genie.ControlUtils.CreateMutedText("");
            _pillNow.TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel job         = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };
            DockPanel.SetDock(_pillJob, Dock.Left);
            DockPanel.SetDock(_pillJobCount, Dock.Left);
            job.Children.Add(_pillJob);
            job.Children.Add(_pillJobCount);
            job.Children.Add(_pillNow);

            StackPanel lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(caption);
            lines.Children.Add(job);

            _pillCount        = Genie.ControlUtils.CreateMutedText("");
            _pillCount.Margin = new Thickness(8, 0, 4, 0);
            Border divider    = StepsBox.Divider();
            divider.Margin    = new Thickness(8, 0, 0, 0);
            Border expand     = Genie.ControlUtils.CreateGrowIcon(delegate { SetView(View.Full); }, "Show the steps");
            _pillClose        = Genie.ControlUtils.CreateCloseIcon(Dismiss, "Close");
            _pillClose.Margin = new Thickness(2, 0, 0, 0);

            Grid row = new Grid { Margin = new Thickness(15, 0, 10, 3) };
            foreach (GridLength width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto,
                                                 GridLength.Auto, GridLength.Auto, GridLength.Auto })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            _pillMark.Margin = new Thickness(0, 0, 8, 0);
            Place(row, _pillMark, 0);
            Place(row, lines, 1);
            Place(row, divider, 2);
            Place(row, _pillCount, 3);
            Place(row, expand, 4);
            Place(row, _pillClose, 5);

            _pillBar                   = Genie.ControlUtils.CreateProgressBar(3);
            _pillBar.CornerRadius      = new CornerRadius(0);
            _pillBar.VerticalAlignment = VerticalAlignment.Bottom;
            Grid pill                  = new Grid();
            pill.Children.Add(row);
            pill.Children.Add(_pillBar);
            pill.Cursor         = System.Windows.Input.Cursors.Hand;
            _pill               = pill;
            window.BodyClicked += delegate {
                if (_view == View.Pill)
                    SetView(View.Full);
                else if (_view == View.Full && _titleBar.IsMouseOver)
                    SetView(View.Pill);
                else if (_view == View.Launcher && _launcherTop.IsMouseOver)
                    SetListOpen(!_isListOpen);
            };

            // == The window ==
            // [mark] Package Loader ............ [collapse] [close]
            FrameworkElement titleMark = Genie.ControlUtils.CreatePackageIcon(14, Genie.Theme.TypeTagText);
            titleMark.Margin           = new Thickness(0, 0, 8, 0);
            TextBlock title            = new TextBlock {
                Text              = "Package Loader",
                FontSize          = Genie.Theme.FontSizeLabel,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Border collapse =
                Genie.ControlUtils.CreateShrinkIcon(delegate { SetView(View.Pill); }, "Collapse to the pill");
            _titleClose        = Genie.ControlUtils.CreateCloseIcon(Dismiss, "Close");
            _titleClose.Margin = new Thickness(4, 0, 0, 0);
            DockPanel bar      = new DockPanel { Margin = new Thickness(10, 6, 8, 6) };
            DockPanel.SetDock(titleMark, Dock.Left);
            DockPanel.SetDock(_titleClose, Dock.Right);
            DockPanel.SetDock(collapse, Dock.Right);
            bar.Children.Add(titleMark);
            bar.Children.Add(_titleClose);
            bar.Children.Add(collapse);
            bar.Children.Add(title);
            _titleBar = new Border {
                Child           = bar,
                Background      = Brushes.Transparent, // a click anywhere on it collapses
                BorderBrush     = Genie.Theme.Separator,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };

            // RUNNING JOB 1 OF 2 / name [On startup] [1 failed]
            _header    = Genie.ControlUtils.CreatePanelHeader(window, "Running job", 0);
            _onStartup = Genie.ControlUtils.CreateCountPill("On startup", Genie.ControlUtils.TagTone.Info);
            _failed    = Genie.ControlUtils.CreateCountPill("", Genie.ControlUtils.TagTone.Danger, true);
            _header.AddPill(_onStartup);
            _header.AddPill(_failed);

            // progress bar, then what's happening and the count
            _bar = Genie.ControlUtils.CreateProgressBar();
            _now = new TextBlock {
                FontSize     = Genie.Theme.FontSizeLabel,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _count           = Genie.ControlUtils.CreateMutedText("");
            _count.Margin    = new Thickness(8, 0, 0, 0);
            DockPanel status = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
            DockPanel.SetDock(_count, Dock.Right);
            status.Children.Add(_count);
            status.Children.Add(_now);
            StackPanel progress = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
            progress.Children.Add(_bar);
            progress.Children.Add(status);
            Border progressBox = new Border {
                Child           = progress,
                BorderBrush     = Genie.Theme.Separator,
                BorderThickness = new Thickness(0, 0, 0, 1),
            };

            _rows = Genie.ControlUtils.CreateRowList();
            _rows.SetColumns(GenieColumn.Fill, GenieColumn.Auto, GenieColumn.Fixed(18));
            _rows.RowPaddingY = 3;
            _rows.ChildIndent = 14;
            _scroll           = new ScrollViewer {
                Content                       = _rows,
                Padding                       = new Thickness(10),
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable                     = false,
            };

            // Up next: Material work
            _upNext = new TextBlock {
                FontSize     = Genie.Theme.FontSizeLabel,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            TextBlock upNext = Genie.ControlUtils.CreateMutedText("Up next");
            upNext.Margin    = new Thickness(0, 0, 6, 0);
            DockPanel next   = new DockPanel();
            DockPanel.SetDock(upNext, Dock.Left);
            next.Children.Add(upNext);
            next.Children.Add(_upNext);
            _upNextStrip =
                Genie.ControlUtils.CreateFooter(next, new Thickness(12, 6, 12, 6), Genie.Theme.PanelBackground);

            // [Skip] ............ [Stop], or a note, Run history and [Close] once it's over
            _skip = Genie.ControlUtils.CreateButton("Skip", tooltip: "Stop this script and go on to the next substep",
                                                    onClick: OnSkip);
            _stop = Genie.ControlUtils.CreateButton("Stop", Genie.ControlUtils.ButtonTone.Danger,
                                                    tooltip: "Stop now. Nothing after this substep runs, in any job.",
                                                    onClick: OnStop);
            _stop.Margin      = new Thickness(0);
            DockPanel running = new DockPanel();
            DockPanel.SetDock(_stop, Dock.Right);
            running.Children.Add(_stop);
            _skip.HorizontalAlignment = HorizontalAlignment.Left;
            running.Children.Add(_skip);
            _runningBar = running;

            _closeNote = Genie.ControlUtils.CreateMutedText("");
            TextBlock history =
                Genie.ControlUtils.CreateLink("Run history", OnHistory, "See this run, substep by substep");
            history.Margin = new Thickness(0, 0, 10, 0);
            // Not Close: where a finished run's eye lands, and a misclick here shouldn't lose it.
            Button toPill      = Genie.ControlUtils.CreateButton("Collapse", tooltip: "Back to the pill",
                                                                 onClick: delegate { SetView(View.Pill); });
            toPill.Margin      = new Thickness(0);
            DockPanel finished = new DockPanel();
            DockPanel.SetDock(toPill, Dock.Right);
            DockPanel.SetDock(history, Dock.Right);
            finished.Children.Add(toPill);
            finished.Children.Add(history);
            finished.Children.Add(_closeNote);
            _finishedBar = finished;

            Grid buttons = new Grid();
            buttons.Children.Add(_runningBar);
            buttons.Children.Add(_finishedBar);
            Border footer = Genie.ControlUtils.CreateFooter(buttons, new Thickness(12, 8, 12, 8));

            DockPanel full = new DockPanel();
            foreach (FrameworkElement top in new FrameworkElement[] { _titleBar, _header, progressBox }) {
                DockPanel.SetDock(top, Dock.Top);
                full.Children.Add(top);
            }
            DockPanel.SetDock(footer, Dock.Bottom);
            DockPanel.SetDock(_upNextStrip, Dock.Bottom);
            full.Children.Add(footer);
            full.Children.Add(_upNextStrip);
            full.Children.Add(_scroll);
            _full = full;

            // == The launcher ==
            FrameworkElement launcherMark = Genie.ControlUtils.CreatePackageIcon(16, Genie.Theme.TypeTagText);
            launcherMark.Margin           = new Thickness(0, 0, 10, 0);
            _jobCount                     = Genie.ControlUtils.CreateBoldText("", Genie.Theme.FontSizeBody);
            _jobCount.TextTrimming        = TextTrimming.None;
            _jobCount.Margin              = new Thickness(0, 0, 6, 0);
            _lastRun                      = Genie.ControlUtils.CreateMutedText("");
            _lastRun.TextTrimming         = TextTrimming.CharacterEllipsis;
            DockPanel summary             = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            DockPanel.SetDock(_jobCount, Dock.Left);
            summary.Children.Add(_jobCount);
            summary.Children.Add(_lastRun);
            StackPanel launcherLines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            launcherLines.Children.Add(
                Genie.ControlUtils.CreateMutedText("PACKAGE LOADER", Genie.Theme.FontSizeCaption));
            launcherLines.Children.Add(summary);
            _listChevron          = Genie.ControlUtils.CreateExpandButton(false, "Show the jobs");
            _listChevron.Toggled += SetListOpen;
            _listChevron.Margin   = new Thickness(8, 0, 0, 0);
            DockPanel topRow      = new DockPanel { Margin = new Thickness(15, 0, 12, 0) };
            DockPanel.SetDock(launcherMark, Dock.Left);
            DockPanel.SetDock(_listChevron, Dock.Right);
            topRow.Children.Add(launcherMark);
            topRow.Children.Add(_listChevron);
            topRow.Children.Add(launcherLines);
            _launcherTop = new Border {
                Child      = topRow,
                Height     = RunningWindow.PillHeight - 2, // inside the window's border
                Background = Brushes.Transparent,          // a click anywhere on it opens or closes the list
                Cursor     = System.Windows.Input.Cursors.Hand,
            };

            _jobList = Genie.ControlUtils.CreateRowList();
            _jobList.SetColumns(GenieColumn.Fixed(8), GenieColumn.Fill, GenieColumn.Auto, GenieColumn.Auto,
                                GenieColumn.Auto);
            _jobList.BorderThickness = new Thickness(0, 1, 0, 0);
            _jobList.CornerRadius    = new CornerRadius(0);
            _jobList.RowPaddingY     = 4;
            _jobList.EmptyText       = "No jobs yet.";
            ScrollViewer listScroll  = new ScrollViewer {
                Content                       = _jobList,
                MaxHeight                     = MaxListHeight,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable                     = false,
            };
            TextBlock hide = Genie.ControlUtils.CreateLink(
                "Hide the launcher", OnHideLauncher,
                "Turns off Show the launcher. Turn it on again in Package Loader\u2019s " + "gear menu.");
            Button runHistory =
                Genie.ControlUtils.CreateButton("Run history", size: Genie.ControlUtils.ButtonSize.Small,
                                                tooltip: "Open the run history at the latest run", onClick: delegate {
                                                    ToEditor(delegate { Windows.ShowHistory(null); });
                                                });
            Button mainWindow = Genie.ControlUtils.CreateButton(
                "Package Loader", size: Genie.ControlUtils.ButtonSize.Small, tooltip: "Open the Package Loader window",
                onClick: delegate { ToEditor(delegate { Windows.ShowMain(); }); });
            mainWindow.Margin = new Thickness(0);
            StackPanel right  = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(runHistory);
            right.Children.Add(mainWindow);
            DockPanel listBar = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            listBar.Children.Add(right);
            hide.HorizontalAlignment = HorizontalAlignment.Left;
            listBar.Children.Add(hide);
            StackPanel listPart = new StackPanel();
            listPart.Children.Add(listScroll);
            listPart.Children.Add(
                Genie.ControlUtils.CreateFooter(listBar, new Thickness(12, 6, 10, 6), Genie.Theme.PanelBackground));
            _listPart           = listPart;
            StackPanel launcher = new StackPanel();
            launcher.Children.Add(_launcherTop);
            launcher.Children.Add(_listPart);
            _launcher = launcher;

            Grid root = new Grid();
            root.Children.Add(_launcher);
            root.Children.Add(_pill);
            root.Children.Add(_full);
            Content = root;
            SetView(View.Pill);
        }

        /// <summary>The line on show, to hand back to the editor's thread.</summary>
        public RunLine ShownLine {
            get { return _run != null ? _run.Line : null; }
        }

        /// <summary>Its run going, or its last: where Run history opens.</summary>
        public RunResult ShownRun {
            get { return _run != null ? _run.Run : null; }
        }

        /// <summary>What to show: the run if there is one, else the launcher. Neither: it closes.</summary>
        public void Show(RunSnapshot run, LauncherSnapshot jobs) {
            if (!_window.IsOpen) return; // closing: a new run gets a new window
            _jobsNow = jobs;
            if (run != null) {
                ShowRun(run);
            } else if (_run != null) {
            } else if (jobs == null) {
                _window.Close();
            } else {
                ShowLauncher();
            }
        }

        private void ShowRun(RunSnapshot run) {
            if (_run == null || _run.Line != run.Line)
                StartOver();
            else if (run.Jobs.Count < _jobs.Count)
                DropRows(); // a Stop let go of the runs waiting
            _run = run;
            UpdateAutoClose();
            ShowPill();
            ShowHeader();
            ShowRows();
            ShowButtons();
            ShowTimes();
        }

        private void StartOver() {
            _jobs.Clear();
            _chosen.Clear();
            _rows.ClearRows();
            _inView     = null;
            _isStopping = false;
            _closeAt    = null;
            _isListOpen = false;
            _listChevron.IsExpanded = false;
            if (_view == View.Launcher) SetView(View.Pill);
        }

        // Rows go by position, so with jobs gone from the line they're drawn again; clicks on them still hold.
        private void DropRows() {
            _jobs.Clear();
            _rows.ClearRows();
            _inView = null;
        }

        private void SetView(View view) {
            _view                = view;
            _launcher.Visibility = view == View.Launcher ? Visibility.Visible : Visibility.Collapsed;
            _pill.Visibility     = view == View.Pill ? Visibility.Visible : Visibility.Collapsed;
            _full.Visibility     = view == View.Full ? Visibility.Visible : Visibility.Collapsed;
            _listPart.Visibility = _isListOpen ? Visibility.Visible : Visibility.Collapsed;
            double height        = RunningWindow.PillHeight;
            if (view == View.Full) height = RunningWindow.FullHeight;
            if (view == View.Launcher && _isListOpen) {
                _listPart.Measure(new Size(_window.PillWidth - 2, double.PositiveInfinity));
                height += _listPart.DesiredSize.Height;
            }
            _pillJob.MaxWidth = _window.PillWidth * 0.36; // the rest is for what's happening
            _window.ResizeTo(_window.PillWidth, height);
            _window.CornerRadius = height > RunningWindow.PillHeight ? 8 : RunningWindow.PillHeight / 2;
            UpdateAutoClose();
            ShowTimes();
        }

        // A clean run leaves by itself, but not from under someone reading it: the wait starts once it's a pill.
        private void UpdateAutoClose() {
            bool isClean = _run != null && _run.IsFinished && !_run.IsStopped && _run.Failed == 0;
            if (!isClean || _view != View.Pill)
                _closeAt = null;
            else if (!_closeAt.HasValue)
                _closeAt = DateTime.Now.AddSeconds(CloseAfterSeconds);
            if (_run == null || _run.IsFinished && !_closeAt.HasValue)
                _tick.Stop();
            else if (!_tick.IsEnabled) // Start on a running timer restarts it: steady updates would starve it
                _tick.Start();
        }

        // The run is over with: back to the launcher, or gone if that's off.
        private void Dismiss() {
            RunningWindow.Dismiss(ShownLine);
            _run     = null;
            _closeAt = null;
            _tick.Stop();
            if (_jobsNow == null)
                _window.Close();
            else
                ShowLauncher();
        }

        private void ShowLauncher() {
            LauncherSnapshot jobs = _jobsNow;
            _jobCount.Text        = Genie.TextUtils.Count(jobs.Startup.Count + jobs.OnDemand.Count, "job");
            _lastRun.Inlines.Clear();
            _lastRun.Inlines.Add(new System.Windows.Documents.Run(jobs.LastRun));
            if (jobs.LastFailed != null)
                _lastRun.Inlines.Add(
                    new System.Windows.Documents.Run(jobs.LastFailed) { Foreground = Genie.Theme.DangerTagText });
            Genie.ControlUtils.SetToolTip(_lastRun, jobs.LastRunTip);
            if (_isListOpen) FillJobList();
            SetView(View.Launcher);
        }

        private void SetListOpen(bool open) {
            _isListOpen             = open;
            _listChevron.IsExpanded = open;
            Genie.ControlUtils.SetToolTip(_listChevron, open ? "Hide the jobs" : "Show the jobs");
            if (open) FillJobList();
            SetView(View.Launcher);
        }

        // Rebuilt whole: it only changes while it isn't being used, when jobs are edited in the main window.
        private void FillJobList() {
            _jobList.ClearRows();
            AddJobSection("On startup", _jobsNow.Startup, _jobsNow.IsStartupOff);
            AddJobSection("On demand", _jobsNow.OnDemand, false);
        }

        private void AddJobSection(string caption, List<LauncherJob> jobs, bool isOff) {
            StackPanel head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Genie.ControlUtils.CreateCaption(caption));
            if (isOff) {
                GenieTag off = Genie.ControlUtils.CreateTag(
                    "Off", Genie.ControlUtils.TagTone.Warning,
                    "Enable startup jobs is off (gear menu), so these don\u2019t run at startup", null, null,
                    Genie.ControlUtils.TagSize.Small);
                off.Margin = new Thickness(6, 0, 0, 0);
                head.Children.Add(off);
            }
            Grid.SetColumnSpan(head, 2);
            GenieRow section =
                _jobList.AddRow(head, null,
                                Genie.ControlUtils.CreateMutedText(jobs.Count.ToString(CultureInfo.InvariantCulture),
                                                                   Genie.Theme.FontSizeCaption));
            section.Fill     = Genie.Theme.PanelBackground;
            foreach (LauncherJob j in jobs) AddJob(j);
        }

        private void AddJob(LauncherJob job) {
            string    id   = job.Id;
            TextBlock name = new TextBlock {
                Text         = job.Name,
                FontSize     = Genie.Theme.FontSizeBody,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            StackPanel lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(name);
            lines.Children.Add(Genie.ControlUtils.CreateMutedText(job.Detail, Genie.Theme.FontSizeCaption));
            TextBlock last         = Genie.ControlUtils.CreateMutedText(job.LastRun, Genie.Theme.FontSizeCaption);
            last.VerticalAlignment = VerticalAlignment.Center;
            Genie.ControlUtils.SetToolTip(last, job.LastRunTip);
            Border open =
                Genie.ControlUtils.CreateGearIcon(delegate { OpenJob(id); }, "Open " + job.Name + " in Package Loader");
            Border     run   = Genie.ControlUtils.CreateRunIcon(delegate { RunJob(id); }, "Run " + job.Name);
            StackPanel icons = new StackPanel { Orientation = Orientation.Horizontal };
            icons.Children.Add(open);
            icons.Children.Add(run);
            _jobList.AddRow(Genie.ControlUtils.CreateDot(job.Tone, job.Tip), lines, last, icons);
        }

        private void OpenJob(string id) {
            SetListOpen(false);
            ToEditor(delegate {
                Job job = Store.Jobs.Find(j => j.Id == id);
                if (job != null) Windows.ShowMain(job);
            });
        }

        // The list closes now; the pill turns into the run's once the editor's thread has started it.
        private void RunJob(string id) {
            SetListOpen(false);
            ToEditor(delegate {
                Job job = Store.Jobs.Find(j => j.Id == id);
                if (job != null) Runner.Start(job);
            });
        }

        private void OnHideLauncher() {
            ToEditor(delegate { Store.ShowLauncher.Value = false; });
        }

        private static void ToEditor(Action work) {
            Genie.Dispatcher.BeginInvoke(work);
        }

        // == Clicks ===================================================================================

        private void OnSkip() {
            Genie.ControlUtils.Block(_skip, "Skipping\u2026");
            Genie.Dispatcher.BeginInvoke(new Action(Runner.Skip));
        }

        private void OnStop() {
            _isStopping = true;
            ShowButtons();
            ShowTimes();
            Genie.Dispatcher.BeginInvoke(new Action(Runner.Stop));
        }

        private void OnHistory() {
            RunResult run = ShownRun;
            Genie.Dispatcher.BeginInvoke(new Action(delegate { Windows.ShowHistory(run); }));
        }

        // == Showing ==================================================================================

        private void ShowPill() {
            RunSnapshot r = _run;
            Outcome     o = !r.IsFinished  ? Outcome.Running
                            : r.Failed > 0 ? Outcome.Failed
                            : r.IsStopped  ? Outcome.Skipped
                                           : Outcome.Done;
            StepsBox.SetMark(_pillMark, o, r.IsStopped ? "Stopped" : r.Job.Tip, null);
            string state = !r.IsFinished
                               ? (r.Job.IsTemplate ? "TEMPLATE " : "JOB ") + (r.Current + 1) + " OF " + r.Jobs.Count
                           : r.IsStopped      ? "STOPPED"
                           : r.Jobs.Count > 1 ? "ALL " + r.Jobs.Count + " JOBS DONE"
                                              : "DONE";
            _pillCaption.Text = "PACKAGE LOADER \u00b7 " + state;
            _pillFailed.Text  = r.Failed > 0 ? r.Failed + " failed" : "";
            _pillJob.Text     = r.Job.Name;
            Genie.ControlUtils.SetToolTip(_pillJob, r.Job.Name);
            _pillJobCount.Text = r.Job.Done + " / " + r.Job.Total;
            Genie.ControlUtils.SetToolTip(_pillJobCount,
                                          r.Job.Done + " of " + r.Job.Total + " substeps done in " + r.Job.Name);
            _pillCount.Text = r.Done + " of " + r.Total;
            Genie.ControlUtils.SetToolTip(_pillCount, r.Done + " of " + r.Total + " substeps done across " +
                                                          Genie.TextUtils.Count(r.Jobs.Count, "job"));
            _pillClose.Visibility = r.IsFinished ? Visibility.Visible : Visibility.Collapsed;
            Genie.ControlUtils.SetToolTip(_pillClose, _jobsNow != null ? "Back to the launcher" : "Close");
            SetProgress(_pillBar);
        }

        private void ShowHeader() {
            RunSnapshot r         = _run;
            string      running   = r.Job.IsTemplate ? "Running template" : "Running job";
            _header.Caption       = r.IsStopped && r.IsFinished ? "Stopped"
                                    : r.IsFinished              ? "Finished"
                                    : r.Jobs.Count > 1 ? running + " " + (r.Current + 1) + " of " + r.Jobs.Count
                                                       : running;
            _header.Title         = r.Job.Name;
            _onStartup.Visibility = r.IsStartup ? Visibility.Visible : Visibility.Collapsed;
            _failed.Text          = r.Failed + " failed";
            _failed.Visibility    = r.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            _count.Text           = r.Done + " of " + r.Total;
            SetProgress(_bar);
            if (r.IsFinished)
                Genie.ControlUtils.Unblock(_titleClose);
            else
                Genie.ControlUtils.Block(_titleClose, "Closes once the run ends. Stop ends it now.");

            List<string> waiting = new List<string>();
            if (!r.IsFinished) {
                for (int i = r.Current + 1; i < r.Jobs.Count; i++) waiting.Add(r.Jobs[i].Name);
            }
            _upNext.Text            = string.Join(", ", waiting);
            _upNextStrip.Visibility = waiting.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetProgress(GenieProgressBar bar) {
            bar.Value = _run.Total > 0 ? (double)_run.Done / _run.Total : _run.IsFinished ? 1 : 0;
            bar.Ink   = _run.Failed > 0 ? Genie.Theme.DangerTagText : Genie.Theme.InfoTagText;
        }

        private void ShowButtons() {
            RunSnapshot r           = _run;
            _runningBar.Visibility  = r.IsFinished ? Visibility.Collapsed : Visibility.Visible;
            _finishedBar.Visibility = r.IsFinished ? Visibility.Visible : Visibility.Collapsed;
            if (_isStopping) {
                Genie.ControlUtils.Block(_skip, "Stopping");
                Genie.ControlUtils.Block(_stop, "Stopping: a package load finishes first");
                return;
            }
            Genie.ControlUtils.Unblock(_stop);
            if (r.CanSkip)
                Genie.ControlUtils.Unblock(_skip);
            else
                Genie.ControlUtils.Block(_skip, "Only a script can be skipped: a package load runs to its end");
        }

        // Rows are added once; after that only what they show changes, so spinners keep turning.
        private void ShowRows() {
            GenieRow running = null;
            for (int j = 0; j < _run.Jobs.Count; j++) {
                JobSnapshot job = _run.Jobs[j];
                if (j == _jobs.Count) _jobs.Add(AddJob(job));
                JobRow row = _jobs[j];
                StepsBox.SetMark(row.Mark, job.Outcome, job.Tip, null);
                row.Count.Text = job.Done + " / " + job.Total;
                bool jobOpen =
                    SetOpen(row.Row, JobKey(job), job.Outcome == Outcome.Running || _run.IsFinished && job.Failed > 0);
                int s = 0, u = 0;
                foreach (StepSnapshot step in job.Steps) {
                    LineRow stepRow = row.Steps[s++];
                    StepsBox.SetMark(stepRow.Mark, step.Outcome, step.Tip, null);
                    bool stepOpen = SetOpen(stepRow.Row, StepKey(job, step),
                                            step.Outcome == Outcome.Running || step.Outcome == Outcome.Failed);
                    foreach (SubstepSnapshot sub in step.Substeps) {
                        LineRow line = row.Substeps[u++];
                        bool    busy = sub.Outcome == Outcome.Running;
                        StepsBox.SetMark(line.Mark, sub.Outcome, sub.Tip, null);
                        line.Row.Fill     = busy ? Genie.Theme.InfoTagBackground : null;
                        line.Row.IsDimmed = sub.IsOff;
                        if (busy && jobOpen && stepOpen && running == null) running = line.Row;
                    }
                }
            }
            if (running == null || running == _inView) return;
            _inView = running;
            if (_full.Visibility != Visibility.Visible) return;
            GenieRow target = running;
            Dispatcher.BeginInvoke(new Action(target.BringIntoView), DispatcherPriority.Loaded);
        }

        // Opens or closes a row as the run says, unless a click has decided it. Returns whether it's open.
        private bool SetOpen(GenieRow row, string key, bool open) {
            bool chosen;
            if (_chosen.TryGetValue(key, out chosen)) open = chosen;
            _isSetting     = true;
            row.IsExpanded = open;
            _isSetting     = false;
            return open;
        }

        private static string JobKey(JobSnapshot job) {
            return job.Number.ToString(CultureInfo.InvariantCulture);
        }

        private static string StepKey(JobSnapshot job, StepSnapshot step) {
            return JobKey(job) + "." + step.Number.ToString(CultureInfo.InvariantCulture);
        }

        // Job n  name ......... n / n  (mark), opening to its steps, each opening to its substeps.
        private JobRow AddJob(JobSnapshot job) {
            JobRow    row  = new JobRow { Mark = StepsBox.NewMark() };
            TextBlock name = Genie.ControlUtils.CreateBoldText(job.Name, Genie.Theme.FontSizeBody);
            Genie.ControlUtils.SetToolTip(name, job.Name);
            row.Count = Genie.ControlUtils.CreateMutedText("");
            row.Row   = _rows.AddRow(Numbered((job.IsTemplate ? "Template " : "Job ") + job.Number, double.NaN, name),
                                     row.Count, row.Mark);
            foreach (StepSnapshot step in job.Steps) {
                LineRow stepRow = new LineRow { Mark = StepsBox.NewMark() };
                stepRow.Row =
                    row.Row.AddRow(Numbered(step.Number.ToString(CultureInfo.InvariantCulture), 16,
                                            Genie.ControlUtils.CreateBoldText(step.Name, Genie.Theme.FontSizeLabel)),
                                   null, stepRow.Mark);
                row.Steps.Add(stepRow);
                foreach (SubstepSnapshot sub in step.Substeps) {
                    GenieFileNameText file = Genie.ControlUtils.CreateFileNameText(sub.File, "No file");
                    file.Margin            = new Thickness(0);
                    LineRow line =
                        new LineRow { Mark = StepsBox.NewMark(), Note = Genie.ControlUtils.CreateMutedText("") };
                    line.Row               = stepRow.Row.AddRow(Numbered(sub.Label, 28, file), line.Note, line.Mark);
                    row.Substeps.Add(line);
                }
                Collapsible(stepRow.Row, StepKey(job, step), "Show or hide this step\u2019s substeps");
            }
            Collapsible(row.Row, JobKey(job), "Show or hide this job\u2019s steps");
            return row;
        }

        private void Collapsible(GenieRow row, string key, string tooltip) {
            row.ExpandsOnClick   = true;
            row.ExpandToolTip    = tooltip;
            row.ExpandedChanged += delegate(bool open) {
                if (!_isSetting) _chosen[key] = open;
            };
        }

        // A number, right-aligned in width (NaN: its own width), then the row's text.
        private static DockPanel Numbered(string number, double width, UIElement text) {
            TextBlock n     = Genie.ControlUtils.CreateMutedText(number);
            n.Width         = width;
            n.TextAlignment = TextAlignment.Right;
            n.Margin        = new Thickness(0, 0, 8, 0);
            DockPanel cell  = new DockPanel();
            DockPanel.SetDock(n, Dock.Left);
            cell.Children.Add(n);
            cell.Children.Add(text);
            return cell;
        }

        // == Time =====================================================================================

        private void OnTick() {
            if (_closeAt.HasValue && DateTime.Now >= _closeAt.Value)
                Dismiss();
            else
                ShowTimes();
        }

        // What's happening, with the job's time, and a running script's seconds: all worked out here, so they go on
        // through a load.
        private void ShowTimes() {
            RunSnapshot r = _run;
            if (r == null) return;
            string text;
            if (r.IsFinished) {
                text = r.IsStopped    ? "Stopped. Nothing after that ran."
                       : r.Failed > 0 ? "Finished with failures."
                                      : "Finished.";
            } else if (_isStopping) {
                text = "Stopping\u2026";
            } else {
                text = (r.Activity ?? "Starting") + " \u00b7 " + Genie.TextUtils.Duration(DateTime.Now - r.Job.Start);
            }
            _pillNow.Text = text;
            _now.Text     = text;
            Genie.ControlUtils.SetToolTip(_pillNow, text);

            if (r.IsFinished) {
                double left     = _closeAt.HasValue ? (_closeAt.Value - DateTime.Now).TotalSeconds : 0;
                string goes     = _jobsNow != null ? "Back to the launcher in " : "Closing in ";
                _closeNote.Text = _closeAt.HasValue ? goes + Math.Max(1, (int)Math.Ceiling(left)) + " s."
                                  : r.Failed > 0    ? Genie.TextUtils.Count(r.Failed, "substep") + " failed."
                                  : r.IsStopped     ? ""
                                                    : "Finished.";
            }
            for (int j = 0; j < r.Jobs.Count && j < _jobs.Count; j++) {
                int u = 0;
                foreach (StepSnapshot step in r.Jobs[j].Steps) {
                    foreach (SubstepSnapshot sub in step.Substeps) _jobs[j].Substeps[u++].Note.Text = Note(sub);
                }
            }
        }

        // "12 s of 120", on a running script.
        private static string Note(SubstepSnapshot s) {
            if (s.Outcome != Outcome.Running || s.Kind != SubstepKind.Script) return "";
            long seconds = Math.Max(0, (long)(DateTime.Now - s.Start).TotalSeconds);
            return seconds + " s" +
                   (s.Timeout.HasValue ? " of " + s.Timeout.Value.ToString("0.##", CultureInfo.CurrentCulture) : "");
        }

        // == Bits =====================================================================================

        private static void Place(Grid grid, UIElement element, int column) {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
    }

    // == Help =========================================================================================
    // Only what a user won't guess.

    internal static class HelpWindow {
        public static GenieWindow Create() {
            GenieWindow w   = Genie.WindowUtils.CreateWindow("Package Loader: help", 680, 560);
            w.MinWidth      = 560;
            w.MinHeight     = 400;
            w.ShowAddonInfo = false; // a subwindow

            GenieHelpPanel help = Genie.ControlUtils.CreateHelpPanel();
            help.AddTopic(
                "Startup jobs",
                "Jobs set to Run on startup run by themselves when the editor opens, once the Content Browser is " +
                    "ready. Lowest Order first; ties go by name.",
                "They run only while Enable startup jobs (gear menu) is on.",
                "If a launch never finished them (the editor hung or crashed), the next launch asks before running " +
                    "them again: Run them, Skip this time, or Turn off startup jobs.",
                "Stop at first failure skips the rest of that job. The gear menu\u2019s Stop all jobs at first " +
                    "failure skips everything after, in every job.");

            GenieHelpTopic packages = help.AddTopic(
                "Loading packages",
                "A package substep loads the file at its path, wherever it is: a ModBuddy project, a UPK repo, the " +
                    "SDK.",
                "The editor names a package after its file, so a renamed copy is a different package.",
                "One name, one file per session. A package already loaded from another file fails, and so does a " +
                    "second file of the same name in one run. Restart the editor to switch copies.",
                "Packages between two scripts load together in one pass, in any order.",
                "A load with errors fails, with the editor\u2019s own error text in the run history. To let errors " +
                    "through, use the substep\u2019s gear, or Ignore these in the run history.");
            packages.AddTip(
                "Did you know?",
                "Opening packages along with their dependencies is brittle because a package finds its imports by " +
                    "name, in a list that holds only the SDK\u2019s packages at launch. The Content Browser\u2019s " +
                    "Open adds each file to that list only as it loads it, in an order you don\u2019t control, so a " +
                    "package that loads before its dependencies can\u2019t find them.",
                "Package Loader adds its files to that list before loading any, so dependencies resolve whatever " +
                    "the order.",
                "Want Open to do the same? Turn on Addons \u203a Options \u203a Fix Dependency Handling " +
                    "for Open.");

            help.AddTopic("Step templates", "A template is a named list of substeps that any number of steps can use.",
                          "Steps using it are live: edit the template and every step using it changes.",
                          "A step keeps its own substeps while it uses a template; they just don\u2019t run. Switch " +
                              "back any time.",
                          "Deleting a template sends the steps that used it back to their own substeps.",
                          "Run one by itself from its row or its strip, to try it without a job.");

            GenieHelpTopic scripts = help.AddTopic(
                "Scripts",
                "A script runs hidden and can\u2019t be answered by hand. If it asks something (y/n, \u201cPress " +
                    "any key\u201d), put the answers in its Input, one per line, in the order it asks.",
                "Package Loader sets `%PL_SDK%` (the SDK folder), `%PL_JOB%`, `%PL_STEP%`, `%PL_STEPNAME%` and " +
                    "`%PL_SUBSTEP%` for every script. Add your own under Variables; a value can use them, like " +
                    "`%PL_SDK%\\Binaries`.",
                "It passes when its exit code is one of its success codes (default 0). Some tools differ: robocopy " +
                    "needs `0-7`.",
                "End it with `exit /b 0`, or else it passes or fails on its last command\u2019s code.",
                "Past its timeout it\u2019s killed and fails.");
            scripts.AddExample("echo text", "An info line in the run history.");
            scripts.AddExample("echo text >&2", "An error line. The last one shows as why it failed.");
            scripts.AddExample("exit /b 1", "Fails, unless 1 is a success code.");

            help.AddTopic(
                "Run history",
                "Every run is kept as it was when it ran. Renaming, editing or deleting a job later doesn\u2019t " +
                    "change it.",
                "One log file per editor launch, in `Data\\PackageLoader\\Logs`. How many are kept is at the bottom " +
                    "of the run history.",
                "Click a status circle to see that row\u2019s latest run.");
            help.AddTopic(
                "Settings backups", "Jobs and templates save as you edit. There\u2019s no Save button.",
                "Each launch backs them up to `Data\\PackageLoader\\SettingsBackups.zip` before startup jobs run. " +
                    "How many are kept is in the gear menu.",
                "Gear menu: Restore settings. Your current settings are backed up first, so a restore can be undone.");
            help.AddTopic(
                "Move, copy, paste, delete",
                "Drag a row by its dots to move it: a step among the steps, a substep within its step or into " +
                    "another step with its own substeps.",
                "Copy puts readable text on the clipboard. Paste replaces the row you click, asking first unless " +
                    "it\u2019s blank. To add, paste onto a new blank row (the + on a row\u2019s edge), or " +
                    "use Paste at end.",
                "Delete asks first. Undo on the message after it brings it back, until your next edit.");
            w.Content = help;
            return w;
        }
    }

    // == Restore settings =============================================================================
    // The framework's restore panel, told what jobs and templates are. A restore replaces the settings whole, so not
    // during a run, whose jobs would be swapped out from under it.

    internal static class RestoreWindow {
        public static GenieWindow Create() {
            GenieWindow w = Genie.WindowUtils.CreateWindow("Package Loader: restore settings", 720, 460);
            w.MinWidth    = 600;
            w.MinHeight   = 380;
            GenieRestorePanel panel =
                Genie.ControlUtils.CreateSettingsRestorePanel(w, Describe, Pills, Preview, delegate {
                    return Runner.IsRunning ? "Not while a job is running" : null;
                }, Store.Load);
            bool   wasRunning = Runner.IsRunning;
            Action follow     = delegate {
                if (Runner.IsRunning == wasRunning) return;
                wasRunning = Runner.IsRunning;
                panel.Refresh();
            };
            Genie.ControlUtils.WhileLoaded(panel, delegate { Runner.Changed += follow; },
                                           delegate { Runner.Changed -= follow; });
            w.Content = panel;
            return w;
        }

        private static string Jobs(GenieSettingsSnapshot s) {
            return Genie.TextUtils.Count(s.GetSections(Store.JobPrefix).Count, "job");
        }

        private static string Templates(GenieSettingsSnapshot s) {
            return Genie.TextUtils.Count(s.GetSections(Store.TemplatePrefix).Count, "template");
        }

        private static string Describe(GenieSettingsSnapshot s) {
            return Jobs(s) + " \u00b7 " + Templates(s);
        }

        private static IEnumerable<UIElement> Pills(GenieSettingsDiff diff) {
            return new UIElement[] {
                Genie.ControlUtils.CreateCountPill(Jobs(diff.Backup)),
                Genie.ControlUtils.CreateCountPill(Templates(diff.Backup)),
            };
        }

        private static UIElement Preview(GenieSettingsDiff diff) {
            return Genie.ControlUtils.CreateSettingsDiffList(
                diff, s => s.Get("Name") ?? s.Name, Codec.Describe, new GenieSettingsGroup("Jobs", Store.JobPrefix),
                new GenieSettingsGroup("Step templates", Store.TemplatePrefix));
        }
    }

    // == Steps box ====================================================================================
    // A job's steps, each opening to its substeps, or a template's substeps alone. Structural edits (add, move,
    // delete, paste, a kind or template switch) rebuild it; typing and file changes save in place and refresh the
    // marks.

    internal sealed class StepsBox : UserControl {
        internal const double NumberWidth = 30; // a substep's: "2.10"

        internal const double TypeWidth = 140; // a substep's Type group: Package or Script

        private const string TemplateRowWhy = "Part of the template: edit it there";

        private readonly MainPanel    _main;
        private readonly Job          _job;      // or
        private readonly Template     _template; // the one shown
        private readonly GenieRowList _list;

        private readonly List<SubstepRow> _substeps            = new List<SubstepRow>();
        private readonly List<StepRow> _steps                  = new List<StepRow>();
        private readonly Dictionary<GenieRow, object> _rowTags = new Dictionary<GenieRow, object>();
        private Border                                _bar; // the closing bar

        // Paste icons and buttons, each with why the copied item can't paste there (null: it can).
        private readonly List<KeyValuePair<FrameworkElement, Func<CopyText, string>>> _pastes =
            new List<KeyValuePair<FrameworkElement, Func<CopyText, string>>>();

        // Which steps are open, by id, for the session: rebuilds keep them. Unset: Auto-expand (gear) decides.
        private static readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();

        /// <summary>Forgets which steps were opened or closed: Auto-expand decides again.</summary>
        public static void ForgetOpen() {
            Open.Clear();
        }

        private static bool StartsOpen(Step step, Template template) {
            switch (Store.AutoExpand) {
                case StepsExpand.OwnSubsteps:
                    return template == null;
                case StepsExpand.FailedSteps:
                    StepResult r = Runner.LatestOf(step);
                    return r != null && r.Outcome == Outcome.Failed;
                case StepsExpand.Never:
                    return false;
                default:
                    return true;
            }
        }

        private sealed class StepRow {
            public Step            Step;
            public GenieRow        Row;
            public GenieStatusMark Status;
            public GenieCountPill  Count;
            public TextBlock       Number;
        }

        // A drag's move: a step to At among the steps, or a substep from its list to At in To (counted without it).
        private sealed class Move {
            public Step          Step;
            public Substep       Substep;
            public List<Substep> From, To;
            public int           At;

            // The list as it would be after the move.
            public List<T> Apply<T>(List<T> list, T item, bool into) {
                List<T> after = new List<T>(list);
                after.Remove(item);
                if (into) after.Insert(Math.Min(At, after.Count), item);
                return after;
            }
        }

        public StepsBox(MainPanel main, Job job, Template template) {
            _main     = main;
            _job      = job;
            _template = template;
            _list     = Genie.ControlUtils.CreateRowList();
            // The groups take the slack, with the count pill or the file icons at their right end: each row sizes its
            // own, so File runs up to the icons. A child row's indent comes out of the slack, so everything after it
            // lines up at the right edge.
            // Number: a step's chevron follows its number, so substeps indent under it. Each level's numbers line up; a
            // substep's keeps room for "2.10", so a drag's renumbering can't shift its groups.
            _list.SetColumns(GenieColumn.Auto, GenieColumn.Number, GenieColumn.Fill, GenieColumn.Auto, GenieColumn.Auto,
                             GenieColumn.Auto);
            _list.ReserveDragHandles   = true; // a template's rows under a step have none
            _list.RowDrop             = PlanDrop;
            _list.RowDragPreview      += delegate(GenieRow row, GenieRowDrop drop) {
                Renumber(drop != null ? drop.Tag as Move : null);
            };
            _list.ShowInsertButtons  = true;
            _list.ShowExpandAll      = true; // a job's steps; a template's substeps don't expand
            _list.InsertRequested   += OnInsert;
            Content                  = _list;
            Build();
        }

        public bool IsTemplate {
            get { return _template != null; }
        }

        // What Undo shows again once it has put things back.
        private object Shown {
            get { return _job != null ? (object)_job : _template; }
        }

        // == Building =================================================================================

        private void Build() {
            foreach (StepRow s in _steps) Open[s.Step.Id] = s.Row.IsExpanded;
            _list.ClearRows();
            if (_bar != null) ((Panel)_bar.Parent).Children.Remove(_bar); // ClearRows leaves the list's own bars
            _substeps.Clear();
            _steps.Clear();
            _rowTags.Clear();
            _pastes.Clear();

            if (_template != null) {
                List<Substep> subs = _template.Substeps;
                _list.EmptyText    = "No substeps yet.";
                for (int k = 0; k < subs.Count; k++) AddSubstep(null, null, subs, k, true);
                _bar = _list.AddBar(AddBar(subs));
                _list.AddInsertButtons(_bar, delegate { InsertSubstep(subs, subs.Count, SubstepKind.Package); },
                                       "Add a package substep at the end", null, null);
            } else {
                _list.EmptyText = "No steps yet.";
                for (int i = 0; i < _job.Steps.Count; i++) AddStep(i);
                Button paste = Genie.ControlUtils.CreateButton(
                    "Paste at end", size: Genie.ControlUtils.ButtonSize.Small,
                    tooltip: "Paste the copied steps at the end; a job\u2019s steps, or substeps as a new step",
                    onClick: PasteSteps);
                Track(paste, PasteStepsWhy);
                _bar = _list.AddBar(
                    Bar(Genie.ControlUtils.CreateButton("Add step", size: Genie.ControlUtils.ButtonSize.Small,
                                                        tooltip: "Add a step at the end",
                                                        onClick: delegate { InsertStep(_job.Steps.Count); }),
                        paste));
                _list.AddInsertButtons(_bar, delegate { InsertStep(_job.Steps.Count); }, "Add a step at the end", null,
                                       null);
            }
            Refresh();
        }

        private void AddStep(int index) {
            Step step         = _job.Steps[index];
            Template template = Store.FindTemplate(step.TemplateId);

            CheckBox         on    = Genie.ControlUtils.CreateCheckBox("", step.Enabled, "Enabled: runs with the job");

            TextBox name      = Genie.ControlUtils.CreateTextBox(step.Name);
            name.TextChanged += delegate {
                step.Name = name.Text;
                Store.Save(_job);
            };
            GenieFieldGroup nameGroup     = Genie.ControlUtils.CreateFieldGroup("Name", name);
            UIElement       templateGroup = TemplateDropdown(step, template);

            GenieCountPill count    = Genie.ControlUtils.CreateCountPill("");
            count.VerticalAlignment = VerticalAlignment.Center;
            GenieStatusMark status           = NewMark();
            Border paste            = Track(Genie.ControlUtils.CreatePasteIcon(delegate {
                PasteStep(step);
            }, "Paste the copied step in place of this one"), StepRowWhy);
            Border strip            = Genie.ControlUtils.CreateIconGroup(
                Genie.ControlUtils.ButtonSize.Small,
                RunIcon(delegate { _main.Run(_job, it => it.Step == step); }, "Run this step"),
                Genie.ControlUtils.CreateCopyIcon(delegate { return CopyText.Of(step); }, "Copy this step"), paste,
                Genie.ControlUtils.CreateDeleteIcon(delegate { DeleteStep(step); }, "Delete this step"));

            FrameworkElement handle = Genie.ControlUtils.CreateDragHandle("Drag to move this step");
            TextBlock        number = Number((index + 1).ToString());
            GenieRow row = _list.AddRow(on, number, WithEnd(Groups(nameGroup, templateGroup), count, "StepCount"),
                                        Divider(), status, strip);
            row.SetDragHandle(handle);
            row.InsertAboveTip = "Insert a step above";
            row.InsertBelowTip = "Insert a step below";
            row.ExpandToolTip  = "Show its substeps";
            row.KeepUndimmed(on);
            row.IsDimmed  = !step.Enabled;
            on.Checked   += delegate {
                step.Enabled = true;
                row.IsDimmed = false;
                Store.Save(_job);
            };
            on.Unchecked += delegate {
                step.Enabled = false;
                row.IsDimmed = true;
                Store.Save(_job);
            };
            _rowTags[row] = step;
            _steps.Add(new StepRow { Step = step, Row = row, Status = status, Count = count, Number = number });

            if (template != null) {
                for (int k = 0; k < template.Substeps.Count; k++) AddSubstep(row, step, template.Substeps, k, false);
                int    others = Store.UsesOf(template).Count - 1;
                Border band   = row.AddBar(
                    Bar(TemplateListRow.Layers(),
                        Genie.ControlUtils.CreateNoteText(
                            "From template \u2018" + template.Name + "\u2019" +
                            (others > 0 ? ", shared with " + Genie.TextUtils.Count(others, "other step") : "") + "."),
                        EditButton(template)));
                _list.AddInsertButtons(band, null, null, delegate { InsertStep(index + 1); }, "Insert a step below");
            } else {
                for (int k = 0; k < step.Substeps.Count; k++) AddSubstep(row, step, step.Substeps, k, true);
                Border bar = row.AddBar(AddBar(step.Substeps));
                _list.AddInsertButtons(
                    bar, delegate { InsertSubstep(step.Substeps, step.Substeps.Count, SubstepKind.Package); },
                    "Add a package substep at the end", delegate { InsertStep(index + 1); }, "Insert a step below");
            }
            bool open;
            row.IsExpanded = Open.TryGetValue(step.Id, out open) ? open : StartsOpen(step, template);
            // Open, the + under a freeform step's row adds its first substep; closed, the next step.
            if (template == null) {
                Action tips = delegate {
                    row.InsertBelowTip = row.IsExpanded ? "Insert a substep at the top" : "Insert a step below";
                };
                row.ExpandedChanged += delegate {
                    tips();
                };
                tips();
            }
        }

        // A substep row. editable: false for a template's substeps under a step that uses it (edit the template).
        private void AddSubstep(GenieRow parent, Step step, List<Substep> list, int index, bool editable) {
            Substep    s      = list[index];
            int        number = step != null ? _job.Steps.IndexOf(step) + 1 : 0;
            SubstepRow row    = new SubstepRow(this, s, step, number, list, index, editable);
            GenieRow   r      = parent != null ? parent.AddRow(row.Cells) : _list.AddRow(row.Cells);
            row.Row           = r;
            r.Fill            = Genie.Theme.PanelBackground;
            if (row.Handle != null) r.SetDragHandle(row.Handle);
            r.KeepUndimmed(row.Check);
            r.IsDimmed       = !s.Enabled;
            r.InsertBelowTip = editable ? "Insert a substep below" : null;
            _rowTags[r]      = row;
            _substeps.Add(row);
        }

        // Add package / Add script / Paste at end, under a freeform step's substeps or a template's.
        private UIElement AddBar(List<Substep> list) {
            Button paste =
                Genie.ControlUtils.CreateButton("Paste at end", size: Genie.ControlUtils.ButtonSize.Small,
                                                tooltip: "Paste the copied substeps, or a step\u2019s, at the end",
                                                onClick: delegate { PasteAtEnd(list); });
            Track(paste, PasteAtEndWhy);
            Button package = Genie.ControlUtils.CreateButton(
                "Add package", size: Genie.ControlUtils.ButtonSize.Small, tooltip: "Add a package substep at the end",
                onClick: delegate { InsertSubstep(list, list.Count, SubstepKind.Package); });
            Button script = Genie.ControlUtils.CreateButton(
                "Add script", size: Genie.ControlUtils.ButtonSize.Small, tooltip: "Add a script substep at the end",
                onClick: delegate { InsertSubstep(list, list.Count, SubstepKind.Script); });
            return Bar(package, script, paste);
        }

        // Use with several packages selected: the row takes the first and the rest go in below it. Files other rows of
        // the list already have are left out.
        internal void UseSelected(string[] names, Substep row, Step step, List<Substep> list) {
            const int       ConfirmOver = 20;
            HashSet<string> have        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Substep s in list) {
                if (s != row) have.Add(Genie.FileUtils.GetFullPath(s.File) ?? s.File);
            }
            List<string> files   = new List<string>();
            int          already = 0, noFile = 0;
            foreach (string name in names) {
                string file = Genie.PackageUtils.GetPackageFile(name);
                if (file == null)
                    noFile++;
                else if (!have.Add(Genie.FileUtils.GetFullPath(file) ?? file))
                    already++;
                else
                    files.Add(file);
            }
            string skipped = (already > 0 ? " " + already + " already here." : "") +
                             (noFile > 0 ? " " + Genie.TextUtils.Count(noFile, "package") + " with no file." : "");
            if (files.Count == 0) {
                _main.Inform("Nothing added." + skipped);
                return;
            }
            Template template = Store.OwnerOf(list) as Template;
            string owner      = step != null ? step.Name : template != null ? template.Name : "";
            string target     = owner.Length > 0 ? "\u2018" + owner + "\u2019" : "this step";
            string count      = Genie.TextUtils.Count(files.Count, "package");
            if (files.Count > ConfirmOver &&
                !Genie.ControlUtils.Confirm(this, "Add " + count + " to " + target + "?", "Add", null, null,
                                            Genie.ControlUtils.TagTone.Info))
                return;
            string        before = row.File;
            List<Substep> added  = new List<Substep>();
            for (int i = 1; i < files.Count; i++)
                added.Add(new Substep { Kind = SubstepKind.Package, File = files[i] });
            row.File = files[0];
            list.InsertRange(list.IndexOf(row) + 1, added);
            SaveOwner(list);
            Build();
            if (files.Count == 1) {
                _main.Inform("Added 1 package." + skipped);
                return;
            }
            object shown = Shown;
            _main.Offer("Added " + count + " to " + target + "." + skipped, delegate {
                row.File = before;
                list.RemoveAll(added.Contains);
                SaveOwner(list);
                _main.Reshow(shown);
            });
        }

        // Its pencil purple, as templates are.
        private Button EditButton(Template template) {
            Button b = Genie.ControlUtils.CreateButton(
                "Edit template", Genie.ControlUtils.ButtonTone.Standard, Genie.ControlUtils.ButtonSize.Small,
                "Open the template; edits reach every step using it", null, null, null,
                Genie.ControlUtils.CreatePencilIcon(Genie.Theme.TypeTagText, Genie.Theme.TypeTagBorder),
                delegate { _main.Select(template); });
            b.Margin = new Thickness(0);
            return b;
        }

        // Own substeps, the templates, then commands. Selected only once filled: a selection made while the list
        // was still being filled was dropped when it first opened, and with it the step's template.
        private UIElement TemplateDropdown(Step step, Template current) {
            const string Own = "own";
            ComboBox     box = Genie.ControlUtils.CreateDropdown(null, null, null, Genie.ControlUtils.ButtonSize.Small);
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            ComboBoxItem selected   = Genie.ControlUtils.AddDropdownChoice(
                box, "None: own substeps", Own, Genie.TextUtils.Count(step.Substeps.Count, "substep"),
                "Run the step's own substeps");
            if (Store.Templates.Count > 0) Genie.ControlUtils.AddDropdownHeader(box, "Templates");
            List<Template> templates = new List<Template>(Store.Templates);
            templates.Sort((a, b) => Store.ByName(a.Name, b.Name));
            foreach (Template t in templates) {
                ComboBoxItem item = Genie.ControlUtils.AddDropdownChoice(
                    box, t.Name, t, Genie.TextUtils.Count(t.Substeps.Count, "substep"), null,
                    Genie.ControlUtils.LayersGeometry, Genie.Theme.TypeTagText);
                if (t == current) selected = item;
            }
            Genie.ControlUtils.AddDropdownSeparator(box);
            Genie.ControlUtils.AddDropdownCommand(box, "New template from own substeps", delegate {
                Template t = new Template {
                    Name = Genie.TextUtils.FreeName(step.Name.Trim().Length > 0 ? step.Name.Trim() : "New template",
                                                    n => Store.CheckTemplateName(n, null) == null),
                };
                foreach (Substep s in step.Substeps) t.Substeps.Add(s.Copy());
                Store.Add(t);
                step.TemplateId = t.Id;
                Store.Save(_job);
                Build();
            }, "Copy the step's own substeps into a new template, and use it");
            if (current != null)
                Genie.ControlUtils.AddDropdownCommand(box, "Edit \u2018" + current.Name + "\u2019",
                                                      delegate { _main.Select(current); });
            box.SelectedItem      = selected;
            box.SelectionChanged += delegate {
                ComboBoxItem item = box.SelectedItem as ComboBoxItem;
                if (item == null || item.Tag == null) return; // not a choice
                Template t = item.Tag as Template;
                if (t == Store.FindTemplate(step.TemplateId)) return;
                step.TemplateId = t == null ? null : t.Id;
                Store.Save(_job);
                Dispatcher.BeginInvoke(new Action(Build)); // not inside the dropdown's own event
            };

            GenieFieldGroup group = Genie.ControlUtils.CreateFieldGroup("Template", box);
            if (current != null) { // the box alone, after the group cleared its fill; the label stays grey
                box.Background = Genie.Theme.TypeTagBackground;
                box.Foreground = Genie.Theme.TypeTagText;
                Genie.ControlUtils.SetToolTip(box, "Runs the template\u2019s substeps, not its own");
            }
            return group;
        }

        // == Edits ====================================================================================

        private void InsertStep(int at) {
            Step step = new Step { Name = "Step " + (_job.Steps.Count + 1) };
            _job.Steps.Insert(Math.Min(at, _job.Steps.Count), step);
            Open[step.Id] = true;
            Store.Save(_job);
            Build();
        }

        internal void InsertSubstep(List<Substep> list, int at, SubstepKind kind) {
            list.Insert(Math.Min(at, list.Count), new Substep { Kind = kind });
            OpenStepsOf(list);
            SaveOwner(list);
            Build();
        }

        private void OpenStepsOf(List<Substep> list) {
            foreach (StepRow s in _steps) {
                if (s.Step.Substeps != list) continue;
                Open[s.Step.Id]  = true;
                s.Row.IsExpanded = true; // Build keeps what the row shows
            }
        }

        private const string TemplateDropWhy = "Uses a template: edit its substeps there";

        // Where a dragged row lands over another, or null where it stays. A step goes before or after another. A
        // substep goes before or after an editable substep, or onto a step with its own: first if it's open, last if
        // it's closed. Never into a step using a template.
        private GenieRowDrop PlanDrop(GenieRow dragged, GenieRow over, bool below) {
            object source      = _rowTags.ContainsKey(dragged) ? _rowTags[dragged] : null;
            object target      = _rowTags.ContainsKey(over) ? _rowTags[over] : null;
            Step step          = source as Step;
            SubstepRow sub     = source as SubstepRow;
            Step ontoStep      = target as Step;
            SubstepRow ontoSub = target as SubstepRow;
            GenieRowDrop                drop;
            if (step != null) {
                if (ontoStep == null) return null;
                int now = _job.Steps.IndexOf(step), at = _job.Steps.IndexOf(ontoStep) + (below ? 1 : 0);
                if (at > now) at--;
                if (at == now) return null;
                Move m     = new Move { Step = step, At = at };
                drop       = below ? GenieRowDrop.After(over, delegate { Apply(m); })
                                   : GenieRowDrop.Before(over, delegate { Apply(m); });
                drop.Label = (at + 1).ToString(CultureInfo.InvariantCulture);
                drop.Tag   = m;
                return drop;
            }
            if (sub == null) return null;
            if (ontoSub != null && !ontoSub.IsEditable)
                return GenieRowDrop.Refuse(RowOf(ontoSub.Step), TemplateDropWhy);
            if (ontoStep != null && Store.FindTemplate(ontoStep.TemplateId) != null)
                return GenieRowDrop.Refuse(over, TemplateDropWhy);
            Move move = new Move { Substep = sub.Substep, From = sub.List };
            Step into;
            if (ontoSub != null) {
                move.To = ontoSub.List;
                move.At = ontoSub.List.IndexOf(ontoSub.Substep) + (below ? 1 : 0);
                into    = ontoSub.Step;
            } else if (ontoStep != null) {
                move.To = ontoStep.Substeps;
                move.At = over.IsExpanded ? 0 : ontoStep.Substeps.Count;
                into    = ontoStep;
            } else {
                return null;
            }
            int from = sub.List.IndexOf(sub.Substep);
            if (move.To == move.From) {
                if (move.At > from) move.At--;
                if (move.At == from) return null;
            }
            drop       = ontoSub != null   ? (below ? GenieRowDrop.After(over, delegate { Apply(move); })
                                                    : GenieRowDrop.Before(over, delegate { Apply(move); }))
                         : over.IsExpanded ? GenieRowDrop.AtStart(over, delegate { Apply(move); })
                                           : GenieRowDrop.Into(over, delegate { Apply(move); }, "Adds at the end");
            drop.Label = (into != null ? (_job.Steps.IndexOf(into) + 1) + "." : "") + (move.At + 1);
            if (!over.IsExpanded && ontoSub == null) drop.Result = "To end of " + StepName(ontoStep);
            drop.Tag = move;
            return drop;
        }

        private GenieRow RowOf(Step step) {
            StepRow s = _steps.Find(r => r.Step == step);
            return s != null ? s.Row : null;
        }

        // Numbers and counts as they'd be after the move, while a drag shows it; null puts them back.
        private void Renumber(Move m) {
            List<Step> steps = _job != null ? _job.Steps : new List<Step>();
            if (m != null && m.Step != null) steps = m.Apply(steps, m.Step, true);
            foreach (StepRow s in _steps) {
                s.Number.Text      = (steps.IndexOf(s.Step) + 1).ToString(CultureInfo.InvariantCulture);
                List<Substep> subs = Store.SubstepsOf(s.Step);
                if (m != null && m.Substep != null && (subs == m.From || subs == m.To))
                    subs = m.Apply(subs, m.Substep, subs == m.To);
                s.Count.Text = Genie.TextUtils.Count(subs.Count, "substep");
            }
            foreach (SubstepRow row in _substeps) {
                List<Substep> list = row.List;
                if (m != null && m.Substep != null && (list == m.From || list == m.To))
                    list = m.Apply(list, m.Substep, list == m.To);
                int at = list.IndexOf(row.Substep);
                if (at < 0) continue; // the dragged one, hidden
                row.Number.Text = (row.Step != null ? (steps.IndexOf(row.Step) + 1) + "." : "") + (at + 1);
            }
        }

        // Makes the move, shows where it landed, and offers Undo.
        private void Apply(Move m) {
            object shown = Shown;
            string what;
            Action undo;
            if (m.Step != null) {
                int was = _job.Steps.IndexOf(m.Step);
                _job.Steps.Remove(m.Step);
                _job.Steps.Insert(Math.Min(m.At, _job.Steps.Count), m.Step);
                what = "Moved " + StepName(m.Step) + " to step " + (m.At + 1) + ".";
                undo = delegate {
                    _job.Steps.Remove(m.Step);
                    _job.Steps.Insert(Math.Min(was, _job.Steps.Count), m.Step);
                    Store.Save(_job);
                    _main.Reshow(shown);
                };
                Store.Save(_job);
            } else {
                int was = m.From.IndexOf(m.Substep);
                m.From.Remove(m.Substep);
                m.To.Insert(Math.Min(m.At, m.To.Count), m.Substep);
                OpenStepsOf(m.To);
                Step into = _job != null ? _job.Steps.Find(st => st.Substeps == m.To) : null;
                string
                    where = (into != null ? (_job.Steps.IndexOf(into) + 1) +
  "." : "") + (m.At + 1);
                what = "Moved " + SubstepName(m.Substep) + " to " +
                       where + ".";
                undo = delegate {
                    m.To.Remove(m.Substep);
                    m.From.Insert(Math.Min(was, m.From.Count), m.Substep);
                    SaveOwner(m.From);
                    _main.Reshow(shown);
                };
                SaveOwner(m.To);
            }
            Build();
            SubstepRow moved  = m.Substep != null ? _substeps.Find(r => r.Substep == m.Substep) : null;
            GenieRow   landed = m.Step != null ? RowOf(m.Step) : moved != null ? moved.Row : null;
            if (landed != null) {
                landed.BringIntoView();
                landed.Flash();
            }
            _main.Offer(what, undo);
        }

        // Asks first unless it's blank. Undo puts it back where it was.
        private void DeleteStep(Step step) {
            int    n    = step.Substeps.Count;
            string what = StepName(step);
            if (n > 0 && !Genie.ControlUtils.Confirm(this, "Delete " + what + "?", "Delete",
                                                     "Its " + Genie.TextUtils.Count(n, "substep") + " go with it.",
                                                     null, Genie.ControlUtils.TagTone.Danger))
                return;
            Job job   = _job;
            int index = job.Steps.IndexOf(step);
            job.Steps.RemoveAt(index);
            Store.Save(job);
            Build();
            _main.Offer("Deleted " + what + ".", delegate {
                job.Steps.Insert(Math.Min(index, job.Steps.Count), step);
                Store.Save(job);
                _main.Reshow(job);
            });
        }

        internal void DeleteSubstep(List<Substep> list, Substep s) {
            string what = SubstepName(s);
            if (s.File.Length > 0 && !Genie.ControlUtils.Confirm(this, "Delete " + what + "?", "Delete", null, null,
                                                                 Genie.ControlUtils.TagTone.Danger))
                return;
            int    index = list.IndexOf(s);
            object shown = Shown;
            list.RemoveAt(index);
            SaveOwner(list);
            Build();
            _main.Offer("Deleted " + what + ".", delegate {
                list.Insert(Math.Min(index, list.Count), s);
                Store.SaveOwner(Store.OwnerOf(list));
                _main.Reshow(shown);
            });
        }

        // == Paste ====================================================================================
        // Onto a row: replaces it, asking first unless it's blank, with Undo after. At the end: adds, no asking.

        // The row gives way to the copied substeps, one or several.
        internal void PasteSubstep(List<Substep> list, Substep old) {
            CopyText copy = _main.ReadCopy();
            if (copy == null || copy.Kind != CopyKind.Substeps) return;
            List<Substep> pasted = copy.Substeps;
            string        what   = SubstepName(old);
            if (old.File.Length > 0 &&
                !Genie.ControlUtils.Confirm(this, "Replace " + what + "?", "Replace",
                                            pasted.Count == 1
                                                ? "It becomes the copied one: " + SubstepName(pasted[0]) + "."
                                                : "The " + pasted.Count + " copied substeps take its place."))
                return;
            object shown = Shown;
            int    index = list.IndexOf(old);
            list.RemoveAt(index);
            list.InsertRange(index, pasted);
            SaveOwner(list);
            Build();
            _main.Offer("Replaced " + what + ".", delegate {
                foreach (Substep p in pasted) list.Remove(p);
                list.Insert(Math.Min(index, list.Count), old);
                Store.SaveOwner(Store.OwnerOf(list));
                _main.Reshow(shown);
            });
        }

        // The step gives way to the copied one, its template and own substeps included.
        private void PasteStep(Step old) {
            CopyText copy = _main.ReadCopy();
            if (copy == null || StepRowWhy(copy) != null) return;
            Step   pasted     = copy.Steps[0];
            Job    job        = _job;
            string what       = StepName(old);
            Template template = Store.FindTemplate(old.TemplateId);
            if ((old.Substeps.Count > 0 || template != null) &&
                !Genie.ControlUtils.Confirm(this, "Replace " + what + "?", "Replace", StepLoss(old, template)))
                return;
            int index        = job.Steps.IndexOf(old);
            job.Steps[index] = pasted;
            Open[pasted.Id]  = true;
            Store.Save(job);
            Build();
            _main.Offer("Replaced " + what + ".", delegate {
                int at = job.Steps.IndexOf(pasted);
                if (at < 0) return;
                job.Steps[at] = old;
                Store.Save(job);
                _main.Reshow(job);
            });
        }

        private static string StepRowWhy(CopyText c) {
            if (c.Kind != CopyKind.Steps) return MainPanel.Where(c);
            return c.Steps.Count == 1 ? null : "Several steps paste at the end of a job";
        }

        private void PasteAtEnd(List<Substep> list) {
            CopyText copy = _main.ReadCopy();
            if (copy == null || PasteAtEndWhy(copy) != null) return;
            list.AddRange(copy.Kind == CopyKind.Substeps ? copy.Substeps : copy.Steps[0].Substeps);
            OpenStepsOf(list);
            SaveOwner(list);
            Build();
        }

        // Substeps, or one freeform step's.
        private static string PasteAtEndWhy(CopyText c) {
            if (c.Kind == CopyKind.Substeps) return null;
            if (c.Kind != CopyKind.Steps) return MainPanel.Where(c);
            if (c.Steps.Count > 1) return "Several steps paste at the end of a job";
            return c.Steps[0].TemplateId == null
                       ? null
                       : "A templated step runs its template’s substeps: paste it onto a step row, or at the end " +
                             "of a job";
        }

        // The closing bar's: steps, a job's steps, or substeps as one new step.
        private void PasteSteps() {
            CopyText copy = _main.ReadCopy();
            if (copy == null || PasteStepsWhy(copy) != null) return;
            List<Step> steps = new List<Step>();
            if (copy.Kind == CopyKind.Steps) {
                steps.AddRange(copy.Steps);
            } else if (copy.Kind == CopyKind.Job) {
                steps.AddRange(copy.Job.Steps);
            } else {
                Step step = new Step { Name = "Step " + (_job.Steps.Count + 1) };
                step.Substeps.AddRange(copy.Substeps);
                steps.Add(step);
            }
            foreach (Step s in steps) Open[s.Id] = true;
            _job.Steps.AddRange(steps);
            Store.Save(_job);
            Build();
        }

        private static string PasteStepsWhy(CopyText c) {
            return c.Kind == CopyKind.Template ? MainPanel.Where(c) : null;
        }

        // Tracks a paste control: enabled while the copied item pastes there.
        private T Track<T>(T control, Func<CopyText, string> whyNot)
            where T : FrameworkElement {
            _pastes.Add(new KeyValuePair<FrameworkElement, Func<CopyText, string>>(control, whyNot));
            _main.UpdatePaste(control, whyNot);
            return control;
        }

        public void UpdatePaste() {
            foreach (KeyValuePair<FrameworkElement, Func<CopyText, string>> p in _pastes)
                _main.UpdatePaste(p.Key, p.Value);
        }

        // Only rows that can take a paste are tracked; a template's rows under a step say where to edit them.
        internal Border PasteIcon(List<Substep> list, Substep s, bool editable) {
            Border icon = Genie.ControlUtils.CreatePasteIcon(delegate { PasteSubstep(list, s); },
                                                             "Paste the copied substeps in place of this one");
            if (!editable)
                Genie.ControlUtils.Block(icon, TemplateRowWhy);
            else
                Track(icon, c => c.Kind == CopyKind.Substeps ? null : MainPanel.Where(c));
            return icon;
        }

        internal void Rebuild() {
            Build();
        }

        internal void SaveOwner(List<Substep> list) {
            if (_template != null && list == _template.Substeps)
                Store.Save(_template);
            else
                Store.Save(_job);
        }

        internal void RunSubstep(Step step, Substep s) {
            if (_template != null)
                _main.Run(_template, it => it.Substep == s);
            else if (_job != null)
                _main.Run(_job, it => it.Step == step && it.Substep == s);
        }

        internal Border RunIcon(Action onClick, string tooltip) {
            return Genie.ControlUtils.CreateRunIcon(onClick, tooltip);
        }

        internal MainPanel Main {
            get { return _main; }
        }

        // Above a step: a step before it. Below a step: its first substep while open (a freeform one), else a step
        // after it. Below a substep: a substep after it.
        private void OnInsert(GenieRow row, bool above) {
            object tag     = _rowTags.ContainsKey(row) ? _rowTags[row] : null;
            Step step      = tag as Step;
            SubstepRow sub = tag as SubstepRow;
            if (step != null) {
                int index = _job.Steps.IndexOf(step);
                if (above)
                    InsertStep(index);
                else if (row.IsExpanded && Store.FindTemplate(step.TemplateId) == null)
                    InsertSubstep(step.Substeps, 0, SubstepKind.Package);
                else
                    InsertStep(index + 1);
            } else if (sub != null) {
                InsertSubstep(sub.List, sub.List.IndexOf(sub.Substep) + (above ? 0 : 1), sub.Substep.Kind);
            }
        }

        // == Marks ====================================================================================

        /// <summary>Issues, step counts and run marks, from the model as it is now. Cheap: no rebuild.</summary>
        public void Refresh() {
            List<RunItem>             order  = _template != null ? Issues.Order(_template) : Issues.Order(_job, false);
            Dictionary<string, Issue> issues = Issues.Find(order);
            foreach (SubstepRow row in _substeps) {
                RunItem item = order.Find(i => i.Substep == row.Substep && i.Step == row.Step);
                Issue   issue;
                issues.TryGetValue(item != null ? Issues.Key(item) : "", out issue);
                Func<SubstepResult> latest = delegate {
                    return _template != null ? Runner.LatestOf(_template, row.Substep) : Runner.LatestOf(item);
                };
                row.Update(issue, item == null ? null : latest(),
                           item == null ? null : (Action)delegate { ShowInHistory(latest()); });
            }
            foreach (StepRow s in _steps) {
                Step          step = s.Step;
                List<Substep> subs = Store.SubstepsOf(step);
                s.Count.Text       = Genie.TextUtils.Count(subs.Count, "substep");
                Genie.ControlUtils.SetToolTip(s.Count, Files(subs));
                StepResult r = Runner.LatestOf(step);
                SetMark(s.Status, r == null ? Outcome.NotRun : r.Outcome,
                        r == null ? "Not run"
                                  : Describe(r.Outcome, r.Substeps.Count > 0 ? r.Substeps[0].Start : DateTime.Now),
                        delegate { ShowInHistory(Runner.LatestOf(step)); });
            }
        }

        private static void ShowInHistory(object result) {
            if (result != null) Windows.ShowHistory(Runner.RunOf(result), false, result);
        }

        /// <summary>How many enabled substeps will fail as set up, for the warning band.</summary>
        public int FailingCount() {
            List<RunItem>             order  = _template != null ? Issues.Order(_template) : Issues.Order(_job, true);
            Dictionary<string, Issue> issues = Issues.Find(order);
            int                       n      = 0;
            foreach (RunItem item in order) {
                Issue issue;
                if (item.Substep.Enabled && issues.TryGetValue(Issues.Key(item), out issue) && !issue.IsInfo) n++;
            }
            return n;
        }

        private static string Files(List<Substep> subs) {
            if (subs.Count == 0) return "No substeps";
            StringBuilder b = new StringBuilder();
            foreach (Substep s in subs) {
                if (b.Length > 0) b.Append('\n');
                b.Append(s.File.Length > 0 ? Path.GetFileName(s.File) : "(no file)");
                if (!s.Enabled) b.Append(" (off)");
            }
            return b.ToString();
        }

        // Once run, a click opens the run history.
        internal static void SetMark(GenieStatusMark mark, Outcome o, string tooltip, Action onClick) {
            bool ran     = onClick != null && (o == Outcome.Done || o == Outcome.Failed || o == Outcome.Skipped);
            mark.Mark    = MarkOf(o);
            mark.OnClick = ran ? onClick : null;
            Genie.ControlUtils.SetToolTip(mark, ran ? tooltip + ". Click to see it in the run history" : tooltip);
        }

        /// <summary>A mark for something not run yet, for SetMark to change as it runs.</summary>
        internal static GenieStatusMark NewMark() {
            return Genie.ControlUtils.CreateStatusMark(Genie.ControlUtils.StatusMark.NotRun);
        }

        internal static GenieStatusMark Mark(Outcome o, string tooltip) {
            return Genie.ControlUtils.CreateStatusMark(MarkOf(o), tooltip);
        }

        private static Genie.ControlUtils.StatusMark MarkOf(Outcome o) {
            switch (o) {
                case Outcome.Done:
                    return Genie.ControlUtils.StatusMark.Done;
                case Outcome.Failed:
                case Outcome.Unfinished:
                    return Genie.ControlUtils.StatusMark.Failed;
                case Outcome.Skipped:
                    return Genie.ControlUtils.StatusMark.Skipped;
                case Outcome.Running:
                    return Genie.ControlUtils.StatusMark.Running;
                default:
                    return Genie.ControlUtils.StatusMark.NotRun;
            }
        }

        internal static string Describe(Outcome o, DateTime when) {
            string at = " at " + when.ToString("HH:mm", CultureInfo.CurrentCulture);
            switch (o) {
                case Outcome.Done:
                    return "Done" + at;
                case Outcome.Failed:
                    return "Failed" + at;
                case Outcome.Skipped:
                    return "Skipped" + at;
                case Outcome.Running:
                    return "Running";
                default:
                    return "Not run";
            }
        }

        // == Bits =====================================================================================

        // "‘Content’", or "step 2" with no name. For confirms and toasts.
        private string StepName(Step step) {
            return step.Name.Trim().Length > 0 ? "\u2018" + step.Name.Trim() + "\u2019"
                                               : "step " + (_job.Steps.IndexOf(step) + 1);
        }

        private static string SubstepName(Substep s) {
            return s.File.Length > 0 ? "the substep for " + Path.GetFileName(s.File)
                                     : "the " + s.Kind.ToString().ToLowerInvariant() + " substep with no file";
        }

        // What replacing a step throws away.
        private static string StepLoss(Step old, Template template) {
            string own  = old.Substeps.Count > 0
                              ? "Its " + Genie.TextUtils.Count(old.Substeps.Count, "substep") + " go with it."
                              : "";
            string uses = template != null ? "It stops using \u2018" + template.Name + "\u2019." : "";
            return (own + " " + uses).Trim();
        }

        private static string Cap(string text) {
            return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;
        }

        internal static TextBlock Number(string text) {
            return new TextBlock {
                Text              = text,
                FontFamily        = Genie.Theme.MonoFont,
                FontSize          = Genie.Theme.FontSizeLabel,
                Foreground        = Genie.Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        internal static Border Divider() {
            Border d            = Genie.ControlUtils.CreateSeparator(Orientation.Vertical);
            d.Height            = 16;
            d.VerticalAlignment = VerticalAlignment.Center;
            return d;
        }

        // Two groups sharing a row's slack 40/60: a step's Name and its Template, the longer of the two.
        private static Grid Groups(UIElement left, UIElement right) {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            Grid.SetColumn(right, 1);
            ((FrameworkElement)right).Margin = new Thickness(Genie.Theme.Spacing, 0, 0, 0);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }

        // Content taking the slack, then an end piece: a step's count pill, a substep's file icons. With sharedWidth,
        // the end's slot is as wide as the widest in the list (a SharedSizeGroup), so a longer pill on one row doesn't
        // shift that row's groups off the others'.
        internal static Grid WithEnd(UIElement content, FrameworkElement end, string sharedWidth = null) {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = sharedWidth });
            end.Margin              = new Thickness(Genie.Theme.Spacing, 0, 0, 0);
            end.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(end, 1);
            grid.Children.Add(content);
            grid.Children.Add(end);
            return grid;
        }

        // A fixed group, then one taking the rest: a substep's Type and File.
        internal static DockPanel GroupsFixedFirst(FrameworkElement first, double width, UIElement rest) {
            DockPanel panel = new DockPanel();
            first.Width     = width;
            first.Margin    = new Thickness(0, 0, Genie.Theme.Spacing, 0);
            DockPanel.SetDock(first, Dock.Left);
            panel.Children.Add(first);
            panel.Children.Add(rest);
            return panel;
        }

        internal static StackPanel Bar(params UIElement[] parts) {
            StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (UIElement p in parts) {
                FrameworkElement fe = p as FrameworkElement;
                if (fe != null) {
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    if (bar.Children.Count > 0) fe.Margin = new Thickness(Genie.Theme.Spacing, 0, 0, 0);
                }
                bar.Children.Add(p);
            }
            return bar;
        }
    }

    // [drag] [on] 2.1 [Type | Package ▾] [File | issue name.upk ... gear] [use|browse|find] | status [strip]
    // A script has no Use: the Content Browser holds no scripts. Its slot stays, so Browse and Find line up.
    internal sealed class SubstepRow {
        public readonly Substep Substep;
        public readonly Step    Step; // null in a template's own view
        public readonly List<Substep> List;
        public readonly CheckBox      Check;
        public readonly bool             IsEditable; // false for a template's row under a step using it
        public readonly FrameworkElement Handle;     // null when it can't be moved
        public readonly TextBlock        Number;
        public readonly               UIElement[] Cells;
        public GenieRow               Row;

        private readonly StepsBox _box;
        private readonly Border   _issue = new Border {
            // in the File field, as it's about the file: no column of its own
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 4, 0),
            Visibility        = Visibility.Collapsed,
        };
        private readonly GenieStatusMark   _status = StepsBox.NewMark();
        private readonly GenieFieldGroup   _fileGroup;
        private readonly GenieFileNameText _file;
        private readonly Border            _find, _gear;

        public SubstepRow(StepsBox box, Substep s, Step step, int stepNumber, List<Substep> list, int index,
                          bool editable) {
            _box    = box;
            Substep = s;
            Step    = step;
            List    = list;
            IsEditable = editable;
            if (editable)
                Handle = Genie.ControlUtils.CreateDragHandle(
                    step != null ? "Drag to move this substep, here or into another step"
                                 : "Drag to move this substep");

            Check          = Genie.ControlUtils.CreateCheckBox("", s.Enabled, "Enabled: runs with its step");
            Check.Checked += delegate {
                SetEnabled(true);
            };
            Check.Unchecked += delegate {
                SetEnabled(false);
            };

            ComboBox kind             = Genie.ControlUtils.CreateDropdown(new[] { "Package", "Script" }, null, null,
                                                                          Genie.ControlUtils.ButtonSize.Small);
            kind.HorizontalAlignment  = HorizontalAlignment.Stretch;
            kind.SelectedIndex        = s.Kind == SubstepKind.Script ? 1 : 0;
            kind.SelectionChanged    += delegate {
                SubstepKind k = kind.SelectedIndex == 1 ? SubstepKind.Script : SubstepKind.Package;
                if (k == s.Kind) return;
                s.Kind = k;
                box.SaveOwner(list);
                box.Dispatcher.BeginInvoke(new Action(box.Rebuild)); // its icons differ
            };
            GenieFieldGroup kindGroup = Genie.ControlUtils.CreateFieldGroup("Type", kind);

            // A template's row under a step: its setters purple, as templates are, to show whose they are.
            Brush stroke = editable ? Genie.Theme.UseIconStroke : Genie.Theme.TypeTagText;
            Brush fill   = editable ? Genie.Theme.UseIconFill : Genie.Theme.TypeTagBorder;
            _gear        = Genie.ControlUtils.CreateGearIcon(delegate {
                if (s.Kind == SubstepKind.Script)
                    ScriptPanel.Show(_gear, s, box, list, editable);
                else
                    PackagePanel.Show(_gear, s, box, list, editable);
            }, s.Kind == SubstepKind.Script ? "Script settings" : "Package settings", stroke, fill);
            _gear.Margin = new Thickness(0, 0, 2, 0);
            Genie.ControlUtils.SetDot(_gear, s.HasSettings);
            _file = Genie.ControlUtils.CreateFileNameText(s.File);
            DockPanel fileField = new DockPanel();
            DockPanel.SetDock(_gear, Dock.Right);
            fileField.Children.Add(_gear);
            DockPanel.SetDock(_issue, Dock.Left);
            fileField.Children.Add(_issue);
            fileField.Children.Add(_file);
            _fileGroup = Genie.ControlUtils.CreateFieldGroup("File", fileField);

            Border browse = Genie.ControlUtils.CreateBrowseIcon(
                Browse, s.Kind == SubstepKind.Package ? "Choose a .upk or .umap" : "Choose a .bat or .cmd", stroke,
                fill);
            _find = Genie.ControlUtils.CreateFindIcon(delegate { Genie.FileUtils.ShowInExplorer(s.File); },
                                                      "Show the file in File Explorer");
            Border use =
                s.Kind == SubstepKind.Package
                    ? Genie.ControlUtils.CreateUsePackageIcon(
                          UseSelected,
                          "Use the package selected in the Content Browser. Several: the rest go in below this row.",
                          fill, stroke)
                    : new Border { Width = 18 };
            StackPanel icons =
                new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            icons.Children.Add(use);
            icons.Children.Add(browse);
            icons.Children.Add(_find);

            List<Border> stripIcons = new List<Border>();
            stripIcons.Add(box.RunIcon(delegate { box.RunSubstep(step, s); }, "Run this substep"));
            stripIcons.Add(
                Genie.ControlUtils.CreateCopyIcon(delegate { return CopyText.Of(list, s); }, "Copy this substep"));
            stripIcons.Add(box.PasteIcon(list, s, editable));
            Border trash =
                Genie.ControlUtils.CreateDeleteIcon(delegate { box.DeleteSubstep(list, s); }, "Delete this substep");
            stripIcons.Add(trash);
            Border strip =
                Genie.ControlUtils.CreateIconGroup(Genie.ControlUtils.ButtonSize.Small, stripIcons.ToArray());

            // A template's substeps under a step: change them in the template.
            if (!editable) {
                const string Why = "Part of the template: edit it there";
                Check.IsEnabled  = false;
                kind.IsEnabled   = false;
                Genie.ControlUtils.Block(browse, Why);
                Genie.ControlUtils.Block(trash, Why);
                if (s.Kind == SubstepKind.Package) Genie.ControlUtils.Block(use, Why);
            }

            string label = (step != null ? stepNumber + "." : "") + (index + 1);
            Number          = StepsBox.Number(label);
            Number.MinWidth = StepsBox.NumberWidth;
            Cells = new UIElement[] { Check,
                                      Number,
                                      StepsBox.WithEnd(
                                          StepsBox.GroupsFixedFirst(kindGroup, StepsBox.TypeWidth, _fileGroup), icons),
                                      StepsBox.Divider(),
                                      _status,
                                      strip };
        }

        private void SetEnabled(bool on) {
            Substep.Enabled = on;
            if (Row != null) Row.IsDimmed = !on;
            _box.SaveOwner(List);
        }

        private void SetFile(string file) {
            Substep.File = file ?? "";
            _file.Path   = Substep.File;
            _box.SaveOwner(List);
        }

        private void UseSelected() {
            string[] names = Genie.ContentBrowserUtils.GetSelectedPackageNames();
            if (names.Length > 1) {
                _box.UseSelected(names, Substep, Step, List);
                return;
            }
            string file = names.Length > 0 ? Genie.PackageUtils.GetPackageFile(names[0]) : null;
            if (file != null) SetFile(file);
        }

        private void Browse() {
            string current = Genie.FileUtils.GetFullPath(Substep.File);
            string folder  = current != null ? Path.GetDirectoryName(current) : null;
            if (folder != null && !Directory.Exists(folder)) folder = null;
            string picked =
                Substep.Kind == SubstepKind.Package
                    ? Genie.WindowUtils.PickFile(_box, "Choose a package", "Packages", "*.upk;*.umap", folder)
                    : Genie.WindowUtils.PickFile(_box, "Choose a script", "Batch files", "*.bat;*.cmd", folder);
            if (picked != null) SetFile(picked);
        }

        /// <param name="onShowRun">Opens the run history at this row's latest run, once it has run.</param>
        public void Update(Issue issue, SubstepResult result, Action onShowRun) {
            Genie.ControlUtils.SetDot(_gear, Substep.HasSettings); // the run history can change it too
            if (issue != null) {
                FrameworkElement warn =
                    issue.IsInfo ? Genie.ControlUtils.CreateInfoIcon(Genie.Theme.InfoTagText, 13)
                                 : Genie.ControlUtils.CreateWarningIcon(
                                       issue.IsWrong ? Genie.Theme.DangerTagText : Genie.Theme.WarningTagText, 13);
                Genie.ControlUtils.SetToolTip(warn, issue.Text);
                _issue.Child      = warn;
                _issue.Visibility = Visibility.Visible;
                Genie.ControlUtils.SetTone(_fileGroup, issue.Tone, issue.Text);
            } else {
                _issue.Child      = null;
                _issue.Visibility = Visibility.Collapsed;
                Genie.ControlUtils.ClearTone(_fileGroup);
            }
            string full = Genie.FileUtils.GetFullPath(Substep.File);
            if (full != null && File.Exists(full))
                Genie.ControlUtils.Unblock(_find);
            else
                Genie.ControlUtils.Block(_find, "No file at this path");
            StepsBox.SetMark(_status, result == null ? Outcome.NotRun : result.Outcome,
                             result == null ? "Not run"
                                            : StepsBox.Describe(result.Outcome, result.Start) +
                                                  (result.Message.Length > 0 ? ": " + result.Message : ""),
                             onShowRun);
        }
    }

    // == Script panel =================================================================================
    // Opens under a script substep's gear. Every field saves as it changes; Done or a click outside closes it.

    internal static class ScriptPanel {
        private const double LabelWidth = 104;

        public static void Show(Border gear, Substep s, StepsBox box, List<Substep> list, bool editable) {
            GeniePopover popover = Genie.ControlUtils.CreatePopover();
            Action       save    = delegate {
                box.SaveOwner(list);
            };
            StackPanel panel = new StackPanel { Width = 440, Margin = new Thickness(4) };

            TextBox args      = Field(s.Arguments, true, editable);
            args.TextChanged += delegate {
                s.Arguments = args.Text;
                save();
            };
            Genie.ControlUtils.SetToolTip(args, "Passed as typed, after the file. %VAR% is expanded.");
            panel.Children.Add(Line("Arguments", args));

            GeniePathBox folder      = Genie.ControlUtils.CreatePathBox(s.WorkingFolder, !editable);
            folder.VerticalAlignment = VerticalAlignment.Center;
            Border find              = Genie.ControlUtils.CreateFindIcon(
                delegate { Genie.FileUtils.OpenFolder(folder.Text.Trim()); }, "Open the folder in File Explorer");
            Border browse = Genie.ControlUtils.CreateBrowseIcon(delegate {
                string picked = Genie.WindowUtils.PickFolder(folder, "Choose the working folder", folder.Text.Trim());
                if (picked != null) folder.Text = picked;
            }, "Choose the working folder");
            if (!editable) Genie.ControlUtils.Block(browse, "Part of the template: edit it there");
            Action syncFind = delegate {
                string f = folder.Text.Trim();
                if (f.Length == 0 || Directory.Exists(f))
                    Genie.ControlUtils.Unblock(find);
                else
                    Genie.ControlUtils.Block(find, "Folder not found");
                if (f.Length == 0) Genie.ControlUtils.Block(find, "Empty: the script\u2019s own folder");
            };
            folder.TextChanged += delegate {
                s.WorkingFolder = folder.Text.Trim();
                syncFind();
                save();
            };
            syncFind();
            DockPanel folderRow = new DockPanel();
            DockPanel.SetDock(find, Dock.Right);
            DockPanel.SetDock(browse, Dock.Right);
            folderRow.Children.Add(find);
            folderRow.Children.Add(browse);
            folder.Margin = new Thickness(0, 0, 4, 0);
            folderRow.Children.Add(folder);
            Genie.ControlUtils.SetToolTip(folder, "Where the script runs. Empty: the script\u2019s own folder.");
            panel.Children.Add(Line("Working folder", folderRow));

            GenieNumberBox timeout = Genie.ControlUtils.CreateDecimalBox(
                Issues.Seconds(s.Timeout), 0.1, 86400, 2, delegate(double? v) {
                s.Timeout = v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
                save();
                }, true, true, 1, "Seconds before it\u2019s stopped and fails. Empty: no limit.");
            timeout.IsEnabled = editable;
            panel.Children.Add(Line("Timeout", Beside(timeout, "seconds; empty: no limit")));

            TextBox codes          = Field(s.SuccessCodes, false, editable);
            codes.Width            = 120;
            GenieProblemText codesProblem = Genie.ControlUtils.CreateProblemText(codes);
            Action           checkCodes   = delegate {
                string problem;
                Genie.ProcessUtils.ParseExitCodes(codes.Text, out problem);
                codesProblem.Problem = problem;
            };
            Genie.ControlUtils.FilterInput(codes, Genie.ProcessUtils.CleanExitCodes);
            codes.TextChanged += delegate {
                s.SuccessCodes = codes.Text.Trim();
                checkCodes();
                save();
            };
            checkCodes();
            Genie.ControlUtils.SetToolTip(codes,
                                          "Exit codes that pass: whole numbers and ranges, comma-separated " +
                                              "(\u201c0, 2\u201d, \u201c0-7\u201d). Empty: 0. robocopy needs 0-7.");
            StackPanel codesCell = new StackPanel();
            codesCell.Children.Add(Beside(codes, "empty: 0"));
            codesCell.Children.Add(codesProblem);
            panel.Children.Add(Line("Success codes", codesCell));

            TextBox input                      = Field(s.Input, true, editable);
            input.AcceptsReturn                = true;
            input.TextWrapping                 = TextWrapping.NoWrap;
            input.Height                       = 40;
            input.VerticalContentAlignment     = VerticalAlignment.Top;
            input.VerticalScrollBarVisibility  = ScrollBarVisibility.Auto;
            input.TextChanged                 += delegate {
                s.Input = input.Text;
                save();
            };
            Genie.ControlUtils.SetToolTip(input, "Typed into the script when it asks, one answer per line, in order. " +
                                                     "Then input closes.");
            panel.Children.Add(Line("Input", input));

            panel.Children.Add(Variables(s, save, editable));

            // About script usage (left), Done (right)
            Border about      = AboutNote();
            about.Visibility  = Visibility.Collapsed;
            DockPanel foot    = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            Button    done    = Genie.ControlUtils.CreateButton("Done", size: Genie.ControlUtils.ButtonSize.Small,
                                                                onClick: popover.Close);
            done.Margin       = new Thickness(0);
            DockPanel.SetDock(done, Dock.Right);
            foot.Children.Add(done);
            StackPanel link =
                new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            FrameworkElement info = Genie.ControlUtils.CreateInfoIcon(Genie.Theme.TextSecondary, 13);
            info.Margin           = new Thickness(0, 0, 5, 0);
            link.Children.Add(info);
            link.Children.Add(Genie.ControlUtils.CreateLink("About script usage", delegate {
                about.Visibility = about.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            }, "How a script reports back to the run history"));
            foot.Children.Add(link);
            panel.Children.Add(about);
            panel.Children.Add(foot);

            popover.Content  = panel;
            popover.Closed  += delegate {
                Genie.ControlUtils.SetDot(gear, s.HasSettings);
                box.Refresh();
            };
            popover.Show(gear);
        }

        // NAME = value [trash], then + Add variable. A name keeps only letters, digits and _, never leading digits.
        private static UIElement Variables(Substep s, Action save, bool editable) {
            StackPanel all   = new StackPanel();
            StackPanel rows  = new StackPanel();
            GenieProblemText why   = Genie.ControlUtils.CreateProblemText();
            Action     check = delegate {
                why.Problem = Issues.CheckVariables(s.Variables);
            };
            Action fill = null;
            fill        = delegate {
                rows.Children.Clear();
                foreach (Variable v in s.Variables) {
                    Variable variable = v;
                    TextBox  name     = Field(v.Name, true, editable);
                    TextBox  value    = Field(v.Value, true, editable);
                    name.Width        = 130;
                    Genie.ControlUtils.FilterInput(name, Genie.ProcessUtils.CleanVariableName);
                    name.TextChanged += delegate {
                        variable.Name = name.Text;
                        check();
                        save();
                    };
                    value.TextChanged += delegate {
                        variable.Value = value.Text;
                        save();
                    };
                    Border trash = Genie.ControlUtils.CreateDeleteIcon(delegate {
                        s.Variables.Remove(variable);
                        save();
                        fill();
                    }, "Remove this variable");
                    if (!editable) Genie.ControlUtils.Block(trash, "Part of the template: edit it there");
                    DockPanel row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                    DockPanel.SetDock(name, Dock.Left);
                    DockPanel.SetDock(trash, Dock.Right);
                    TextBlock eq = new TextBlock {
                        Text              = "=",
                        Margin            = new Thickness(6, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground        = Genie.Theme.TextSecondary,
                    };
                    DockPanel.SetDock(eq, Dock.Left);
                    row.Children.Add(name);
                    row.Children.Add(eq);
                    row.Children.Add(trash);
                    value.Margin = new Thickness(0, 0, 4, 0);
                    row.Children.Add(value);
                    rows.Children.Add(row);
                }
                check();
            };
            fill();
            TextBlock add = Genie.ControlUtils.CreateLink("+ Add variable", delegate {
                s.Variables.Add(new Variable());
                save();
                fill();
            }, "Set an environment variable for this script only");
            add.IsEnabled = editable;
            all.Children.Add(rows);
            all.Children.Add(add);
            all.Children.Add(why);
            return Line("Variables", all);
        }

        private static Border AboutNote() {
            StackPanel note = new StackPanel();
            string[] points = {
                "echo text: an info line in the run history.",
                "echo text >&2: an error line (>&2 sends it to the error output). The last one is why it failed.",
                "End with exit /b 0, or it passes or fails on its last command\u2019s code.",
                "No one can answer prompts: put the answers in Input or Arguments, and exit non-zero when it " +
                    "can\u2019t do its job. A run can\u2019t tell a prompt that went unanswered.",
                "timeout won\u2019t run here (input is redirected): wait with ping -n 2 127.0.0.1 >nul.",
                "Set for you: %PL_SDK% %PL_JOB% %PL_STEP% %PL_STEPNAME% %PL_SUBSTEP%. Add your own under Variables.",
            };
            foreach (string p in points) {
                TextBlock t = Genie.ControlUtils.CreateNoteText("\u2022 " + p);
                t.Margin    = new Thickness(0, 0, 0, 3);
                note.Children.Add(t);
            }
            return new Border {
                Child           = note,
                Background      = Genie.Theme.InfoTagBackground,
                BorderBrush     = Genie.Theme.InfoTagBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(Genie.Theme.BoxCornerRadius),
                Padding         = new Thickness(10, 8, 10, 5),
                Margin          = new Thickness(0, 8, 0, 0),
            };
        }

        private static TextBox Field(string text, bool mono, bool editable) {
            TextBox box    = Genie.ControlUtils.CreateTextBox(text, mono);
            box.IsReadOnly = !editable;
            return box;
        }

        private static UIElement Beside(UIElement field, string after) {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(field);
            TextBlock t         = Genie.ControlUtils.CreateNoteText(after);
            t.Margin            = new Thickness(6, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            return row;
        }

        private static DockPanel Line(string label, UIElement field) {
            DockPanel line      = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            TextBlock l         = Genie.ControlUtils.CreateFieldLabel(label);
            l.Width             = LabelWidth;
            l.Margin            = new Thickness(0, 4, 0, 0);
            l.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(l, Dock.Left);
            line.Children.Add(l);
            line.Children.Add(field);
            return line;
        }
    }

    // == Package panel ================================================================================
    // Opens under a package substep's gear: what its load errors do. Every change saves at once.

    internal static class PackagePanel {
        public static void Show(Border gear, Substep s, StepsBox box, List<Substep> list, bool editable) {
            GeniePopover popover = Genie.ControlUtils.CreatePopover();
            StackPanel   panel   = new StackPanel { Width = 460, Margin = new Thickness(4) };
            object       owner   = Store.OwnerOf(list);
            panel.Children.Add(new TextBlock {
                Text       = "When this package raises load errors",
                FontSize   = Genie.Theme.FontSizeLabel,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 8),
            });

            RadioButton fail   = Mode("Fail the substep", s.LoadErrors == LoadErrorMode.Fail, editable);
            RadioButton listed = Mode("Ignore these, fail on any other", s.LoadErrors == LoadErrorMode.IgnoreListed,
                                      editable);
            RadioButton all    = Mode("Ignore every load error", s.LoadErrors == LoadErrorMode.IgnoreAll, editable);
            StackPanel  rows   = new StackPanel();
            TextBox     newest  = null;
            bool        filling = false;
            Action      fill    = null;
            // A row is rebuilt only when one comes or goes, so typing keeps its box and caret.
            fill = delegate {
                filling          = true;
                fail.IsChecked   = s.LoadErrors == LoadErrorMode.Fail;
                listed.IsChecked = s.LoadErrors == LoadErrorMode.IgnoreListed;
                all.IsChecked    = s.LoadErrors == LoadErrorMode.IgnoreAll;
                filling          = false;
                rows.Opacity     = s.LoadErrors == LoadErrorMode.IgnoreListed ? 1 : 0.55;
                rows.Children.Clear();
                for (int i = 0; i < s.IgnoredErrors.Count; i++) {
                    int     index = i;
                    TextBox error = Genie.ControlUtils.CreateTextBox(s.IgnoredErrors[i], true,
                                                                     "An error as the run history or the editor's " +
                                                                         "Load Errors dialog words it");
                    error.IsReadOnly   = !editable;
                    error.TextChanged += delegate {
                        s.IgnoredErrors[index] = error.Text;
                        Store.SaveOwner(owner);
                    };
                    Border trash = Genie.ControlUtils.CreateDeleteIcon(delegate {
                        s.IgnoredErrors.RemoveAt(index);
                        s.FailIfNoneListed();
                        Store.SaveIgnores(list);
                        fill();
                    }, "Stop ignoring this error");
                    if (!editable) Genie.ControlUtils.Block(trash, "Part of the template: edit it there");
                    rows.Children.Add(Row(error, trash));
                    newest = error;
                }
                if (s.IgnoredErrors.Count > 0) return;
                TextBlock none = Genie.ControlUtils.CreateNoteText("None yet. Add one, or ignore them from the run " +
                                                                   "history.");
                none.Margin    = new Thickness(0, 0, 0, 4);
                rows.Children.Add(none);
            };
            TextBlock add = Genie.ControlUtils.CreateLink("+ Add error", delegate {
                s.IgnoredErrors.Add("");
                if (s.LoadErrors == LoadErrorMode.Fail) s.LoadErrors = LoadErrorMode.IgnoreListed;
                Store.SaveIgnores(list);
                fill();
                TextBox added = newest;
                added.Dispatcher.BeginInvoke(new Action(delegate { added.Focus(); }),
                                             System.Windows.Threading.DispatcherPriority.Loaded);
            }, "Type or paste an error to ignore");
            add.IsEnabled           = editable;
            add.HorizontalAlignment = HorizontalAlignment.Left;
            Action<RadioButton, LoadErrorMode> follow = delegate(RadioButton button, LoadErrorMode mode) {
                button.Checked += delegate {
                    if (filling) return;
                    s.LoadErrors = mode;
                    Store.SaveIgnores(list);
                    fill();
                };
            };
            follow(fail, LoadErrorMode.Fail);
            follow(listed, LoadErrorMode.IgnoreListed);
            follow(all, LoadErrorMode.IgnoreAll);
            fill();

            panel.Children.Add(fail);
            panel.Children.Add(listed);
            StackPanel errors = new StackPanel { Margin = new Thickness(21, 0, 0, 8) };
            errors.Children.Add(rows);
            errors.Children.Add(add);
            panel.Children.Add(errors);
            panel.Children.Add(all);

            Template template = owner as Template;
            if (template != null) {
                TextBlock note = Genie.ControlUtils.CreateNoteText("From template \u2018" + template.Name +
                                                                   "\u2019: this applies to every step using it.");
                note.Margin    = new Thickness(0, 4, 0, 0);
                panel.Children.Add(note);
            }
            Button done = Genie.ControlUtils.CreateButton("Done", size: Genie.ControlUtils.ButtonSize.Small,
                                                          onClick: popover.Close);
            done.Margin              = new Thickness(0, 10, 0, 0);
            done.HorizontalAlignment = HorizontalAlignment.Right;
            panel.Children.Add(done);

            popover.Content  = panel;
            popover.Closed  += delegate {
                if (editable) {
                    s.TidyIgnored();
                    Store.SaveIgnores(list);
                }
                Genie.ControlUtils.SetDot(gear, s.HasSettings);
                box.Refresh();
            };
            popover.Show(gear);
        }

        private static RadioButton Mode(string text, bool on, bool editable) {
            RadioButton button = Genie.ControlUtils.CreateRadioButton(text, on);
            button.IsEnabled   = editable;
            button.Margin      = new Thickness(0, 0, 0, 6);
            return button;
        }

        private static DockPanel Row(TextBox error, Border trash) {
            DockPanel row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(trash, Dock.Right);
            row.Children.Add(trash);
            error.Margin = new Thickness(0, 0, 4, 0);
            row.Children.Add(error);
            return row;
        }
    }

    // == Main window ==================================================================================

    internal sealed class MainPanel : UserControl {
        private const string NothingCopied = "Nothing copied: copy a job, step, substep or template first";

        private readonly GenieWindow      _window;
        private readonly GenieHeader      _jobHeader, _templateHeader;
        private readonly GenieCountPill   _steps, _onStartup, _failed, _templateSubsteps, _usedBy;
        private readonly RunInfo          _lastRun, _templateLastRun;
        private readonly GenieBanner      _warning, _running;
        private readonly GenieListPane    _pane;
        private readonly GenieListSection _startup, _demand, _templates;
        private readonly ContentControl   _view = new ContentControl { Focusable = false };
        private readonly GenieStatusPill  _toast;

        private readonly Dictionary<object, ListBoxItem> _items = new Dictionary<object, ListBoxItem>();
        private object                                   _selected;
        private bool                                     _showOnRelease;
        private readonly System.Windows.Threading.DispatcherTimer _clock;
        private bool? _lockShown;
        private IView  _lockedView;
        private string _shape;
        private bool   _filling;

        // The clipboard's text while it's Package Loader's ini lines, and what it reads as now.
        private string   _clipText, _clipProblem;
        private CopyText _clip;

        private readonly List<ComboBox> _autoExpand = new List<ComboBox>(); // the gears' Auto-expand, one per header

        // The toast's Undo, valid while Store.Revision stays as it was.
        private Action _undo;
        private int    _undoRevision;

        // Notifications, no polling:
        //   any save (Store.Changed)   -> list rows, header, bands, marks and paste targets; the list is rebuilt only
        //                                 if its order changed; any other edit ends an Undo
        //   the run (Runner.Changed)   -> the same, and the lock on a job running or waiting
        //   Enable startup jobs        -> the Off tag and band
        //   the clipboard (WM_CLIPBOARDUPDATE) -> what the paste icons and buttons take

        public MainPanel(GenieWindow window) {
            _window   = window;
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _jobHeader      = Header("JOB");
            _steps          = Pill(_jobHeader, Genie.ControlUtils.TagTone.Neutral);
            _onStartup      = Pill(_jobHeader, Genie.ControlUtils.TagTone.Info);
            _failed         = Pill(_jobHeader, Genie.ControlUtils.TagTone.Danger);
            _onStartup.Text = "On startup";
            _failed.Cursor  = System.Windows.Input.Cursors.Hand;
            Genie.ControlUtils.SetToolTip(_failed, "Show the failures in the run history");
            _failed.MouseLeftButtonUp += delegate {
                Job job               = _selected as Job;
                JobResult           r = job != null ? Runner.LatestOf(job) : null;
                if (r != null) Windows.ShowHistory(Runner.RunOf(r), true);
            };
            _lastRun = new RunInfo(_jobHeader);
            _jobHeader.AddAction(HistoryButton());
            _clock       = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += delegate {
                OnTick();
            };
            _templateHeader   = Header("STEP TEMPLATE");
            _templateSubsteps = Pill(_templateHeader, Genie.ControlUtils.TagTone.Neutral);
            _usedBy           = Pill(_templateHeader, Genie.ControlUtils.TagTone.Neutral);
            _templateLastRun  = new RunInfo(_templateHeader);
            _templateHeader.AddAction(HistoryButton());
            root.Children.Add(_jobHeader);
            root.Children.Add(_templateHeader);

            _warning        = Genie.ControlUtils.CreateBanner(Genie.ControlUtils.TagTone.Warning);
            _running        = Genie.ControlUtils.CreateBanner(Genie.ControlUtils.TagTone.Info);
            _running.IsBusy = true;

            _pane        = Genie.ControlUtils.CreateListPane("Jobs", true);
            _pane.Margin = new Thickness(10, 10, 0, 10); // the addon info sits over the box's bottom edge
            _pane.EnableSearch("Search jobs and templates", Match,
                               "Nothing matches \u2018{0}\u2019 in names or files.");
            _startup             = _pane.List.AddSection("On startup");
            _demand              = _pane.List.AddSection("On demand");
            _templates           = _pane.List.AddSection("Step templates");
            _startup.EmptyText   = "None.";
            _demand.EmptyText    = "None.";
            _templates.EmptyText = "None.";
            _startup.Badge       = Genie.ControlUtils.CreateTag(
                "Off", Genie.ControlUtils.TagTone.Warning,
                "Enable startup jobs is off (gear menu), so these don\u2019t run at startup");
            _startup.AddAction(Genie.ControlUtils.CreateAddIcon(delegate { AddJob(true); }, "Add a startup job"));
            _demand.AddAction(Genie.ControlUtils.CreateAddIcon(delegate { AddJob(false); }, "Add an on-demand job"));
            _templates.AddAction(Genie.ControlUtils.CreateAddIcon(AddTemplate, "Add a step template"));
            // A row selects on the press, but a row's icon acts on the release: showing the job between the two can
            // move the bands, and the rows with them, so the release would land on the next row. So a selection made
            // with the button down shows once it's up, after the icon has had its click.
            _pane.List.SelectionChanged += delegate {
                if (_filling) return;
                if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                    _showOnRelease = true;
                else
                    ShowSelected();
            };
            _pane.List.PreviewMouseLeftButtonUp += delegate {
                if (!_showOnRelease) return;
                _showOnRelease = false;
                Dispatcher.BeginInvoke(new Action(ShowSelected), System.Windows.Threading.DispatcherPriority.Input);
            };

            Grid.SetRow(_warning, 1);
            Grid.SetRow(_running, 2);
            root.Children.Add(_warning);
            root.Children.Add(_running);

            Grid right = new Grid();
            right.Children.Add(_view);
            _toast = Genie.ControlUtils.CreateStatusPill();
            right.Children.Add(_toast);

            Grid body = Genie.ControlUtils.CreateSplitPanel(_pane, right, 0.27, 240, 560);
            Grid.SetRow(body, 3);
            root.Children.Add(body);
            Content = root;

            FillList();
            List<Job> jobs = Sorted(true);
            jobs.AddRange(Sorted(false));
            object first = jobs.Count > 0 ? (object)jobs[0] : Store.Templates.Count > 0 ? Store.Templates[0] : null;
            Show(first);

            Genie.ClipboardUtils.WatchClipboard(this, OnClipboard);
            Genie.ControlUtils.WhileLoaded(this, Attach, Detach);
        }

        // The header's last run: [history icon] "Last run 23:41", a link to that run in the run history, with how it
        // went in its tooltip; while in a run, a spinner in the icon's place and "Running 0:42" or "Waiting to run".
        // A run from an earlier session isn't in memory, only its time, so that's plain text.
        private sealed class RunInfo {
            private readonly Border           _icon    = new Border { VerticalAlignment = VerticalAlignment.Center };
            private readonly FrameworkElement _history = Genie.ControlUtils.CreateHistoryIcon(null, 13);
            private readonly FrameworkElement _spinner = Genie.ControlUtils.CreateSpinner(13); // one: it keeps turning
            private readonly TextBlock        _text    = Genie.ControlUtils.CreateMutedText();
            private readonly TextBlock        _link;
            private RunResult                 _run; // the link's

            public RunInfo(GenieHeader header) {
                _link           = Genie.ControlUtils.CreateLink("", delegate {
                    if (_run != null) Windows.ShowHistory(_run);
                });
                _icon.Margin    = new Thickness(0, 0, 5, 0);
                StackPanel line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(_icon);
                line.Children.Add(_text);
                line.Children.Add(_link);
                header.AddAction(line);
                line.Margin = new Thickness(0, 0, 8, 0); // after AddAction, which sets its own
            }

            /// <param name="planned">What's in the line for this job or template, or null.</param>
            /// <param name="saved">Its last run, from any session.</param>
            public void Show(Job planned, JobResult r, DateTime? saved) {
                _run             = planned == null && r != null ? Runner.RunOf(r) : null;
                _icon.Child      = planned != null ? _spinner : _history;
                _link.Visibility = _run != null ? Visibility.Visible : Visibility.Collapsed;
                _text.Visibility = _run != null ? Visibility.Collapsed : Visibility.Visible;
                if (planned != null) {
                    bool running = Runner.RunningJob == planned;
                    _text.Text =
                        running ? "Running " + Genie.TextUtils.Duration(DateTime.Now - r.Start) : "Waiting to run";
                    Genie.ControlUtils.SetToolTip(
                        _text, running ? "Started " + r.Start.ToString("T", CultureInfo.CurrentCulture) : null);
                } else if (_run != null) {
                    int failed = r.Count(Outcome.Failed);
                    _link.Text = "Last run " + Genie.TextUtils.When(r.Start);
                    Genie.ControlUtils.SetToolTip(_link, Genie.TextUtils.WhenInFull(r.Start) + ". Took " +
                                                             Genie.TextUtils.Duration(r.End - r.Start) + ": " +
                                                             r.Count(Outcome.Done) + " done" +
                                                             (failed > 0 ? ", " + failed + " failed" : "") +
                                                             ". Click to see it in the run history.");
                } else {
                    _text.Text = saved.HasValue ? "Last run " + Genie.TextUtils.When(saved.Value) : "Never run";
                    Genie.ControlUtils.SetToolTip(_text, Genie.TextUtils.WhenInFull(saved));
                }
            }

            public void Clear() {
                _run             = null;
                _icon.Child      = null;
                _link.Visibility = Visibility.Collapsed;
                _text.Text       = "";
            }
        }

        private GenieHeader Header(string caption) {
            GenieHeader h =
                Genie.ControlUtils.CreatePanelHeader(_window, caption, Genie.ControlUtils.WindowControl.Settings);
            GenieSettingsButton gear = h.Controls.Settings;
            gear.AddCheckItem("Enable startup jobs", Store.StartupJobs,
                              "Run the jobs ON STARTUP, unattended, each time the editor opens");
            gear.AddCheckItem("Stop all jobs at first failure", Store.StopAllAtFirstFailure,
                              "A failed substep skips everything after it, in every job");
            _autoExpand.Add(gear.AddChoiceItem(
                "Auto-expand", new[] { "Never", "Failed steps", "Own substeps only", "All steps" },
                (int)Store.AutoExpand, SetAutoExpand,
                "Which steps start open when a job is shown. Open or close any step by its arrow, or all of them at " +
                    "the top of the steps."));
            gear.AddCheckItem("Show the launcher", Store.ShowLauncher,
                              "A pill over the editor that lists your jobs, to run or open them from there");
            gear.AddSeparator();
            gear.AddBackupItem();
            gear.AddMenuItem("Restore settings\u2026", "Go back to your jobs and settings from an earlier launch",
                             Windows.ShowRestore);
            gear.AddSeparator();
            gear.AddMenuItem("Help", "How startup jobs, packages and scripts behave", Windows.ShowHelp);
            return h;
        }

        // Both headers' gears show it. A change redraws the job shown as reopening the window would.
        private void SetAutoExpand(int choice) {
            if ((int)Store.AutoExpand == choice) return;
            Store.AutoExpand = (StepsExpand)choice;
            foreach (ComboBox box in _autoExpand) box.SelectedIndex = choice;
            StepsBox.ForgetOpen();
            Show(_selected);
        }

        // Opens at the selected job's latest run, else the latest of all.
        private Button HistoryButton() {
            return Genie.ControlUtils.CreateButton(
                "Run history", tooltip: "Every run, with what each substep did; a log file per editor session",
                onClick: ShowLatestRun);
        }

        private void ShowLatestRun() {
            Job job           = _selected           as Job;
            Template template = _selected as Template;
            JobResult r = job != null ? Runner.LatestOf(job) : template != null ? Runner.LatestOf(template) : null;
            Windows.ShowHistory(r != null ? Runner.RunOf(r) : null);
        }

        private static GenieCountPill Pill(GenieHeader header, Genie.ControlUtils.TagTone tone) {
            GenieCountPill pill = Genie.ControlUtils.CreateCountPill("", tone);
            header.AddPill(pill);
            return pill;
        }

        private void Attach() {
            Store.Changed             += OnChanged;
            Runner.Changed            += OnChanged;
            Store.StartupJobs.Changed += OnToggle;
            Store.Reloaded            += OnReloaded;
            OnClipboard();
            OnChanged();
        }

        private void Detach() {
            _clock.Stop();
            Store.Changed             -= OnChanged;
            Runner.Changed            -= OnChanged;
            Store.StartupJobs.Changed -= OnToggle;
            Store.Reloaded            -= OnReloaded;
        }

        // The list and view hold the old objects: the same job or template again by id, else the first.
        private void OnReloaded() {
            Job      job      = _selected as Job;
            Template template = _selected as Template;
            object   again    = null;
            if (job != null) again = Store.Jobs.Find(j => j.Id == job.Id);
            if (template != null) again = Store.Templates.Find(t => t.Id == template.Id);
            List<Job> jobs = Sorted(true);
            jobs.AddRange(Sorted(false));
            _shape = null;
            FillList();
            foreach (ComboBox box in _autoExpand) box.SelectedIndex = (int)Store.AutoExpand;
            StepsBox.ForgetOpen();
            Show(again ?? (jobs.Count > 0 ? (object)jobs[0] : Store.Templates.Count > 0 ? Store.Templates[0] : null));
        }

        private void ShowSelected() {
            ListBoxItem li = _pane.List.SelectedItem as ListBoxItem;
            if (li != null && li.Tag != null && li.Tag != _selected) Show(li.Tag);
        }

        private void OnToggle(bool on) {
            OnChanged();
        }

        private void OnChanged() {
            if (_undo != null && Store.Revision != _undoRevision) {
                _undo = null;
                _toast.Hide();
            }
            if (_clipText != null) ReadClipboard(); // a deleted template makes a step using it unreadable
            FillList();
            UpdateHeader();
            IView view = _view.Content as IView;
            if (view != null) view.Refresh();
            UpdateBands();
            UpdateClock();
        }

        // While a run goes, a tick a second for its time: the header and the running job's row.
        private void UpdateClock() {
            if (!Runner.IsRunning)
                _clock.Stop();
            else if (!_clock.IsEnabled)
                _clock.Start();
        }

        private void OnTick() {
            UpdateHeader();
            Job         job = Runner.RunningJob;
            ListBoxItem li;
            if (job != null && _items.TryGetValue((object)job.RunsTemplate ?? job, out li))
                ((IListRow)li.Content).Update();
            UpdateClock();
        }

        // == Copy, paste and Undo =====================================================================

        private void OnClipboard() {
            string text = Genie.ClipboardUtils.GetText();
            _clipText   = text;
            ReadClipboard();
            if (_clip == null && _clipProblem == null) _clipText = null; // not ours: no need to read it after edits
        }

        private void ReadClipboard() {
            string problem = null;
            _clip          = CopyText.Read(_clipText, out problem);
            _clipProblem   = problem != null ? "The copied text can\u2019t be read: " + problem : null;
            IView view     = _view.Content as IView;
            if (view != null) view.UpdatePaste();
        }

        /// <summary>The copied item as new objects, with new ids, for one paste; null if there's none.</summary>
        public CopyText ReadCopy() {
            string problem;
            return CopyText.Read(_clipText, out problem);
        }

        /// <summary>Enables a paste icon or button while the copied item pastes there; else says why not.</summary>
        public void UpdatePaste(FrameworkElement control, Func<CopyText, string> whyNot) {
            string why = _clipProblem ?? (_clip == null ? NothingCopied : whyNot(_clip));
            if (why == null)
                Genie.ControlUtils.Unblock(control);
            else
                Genie.ControlUtils.Block(control, why);
        }

        /// <summary>Where a copied item of this kind pastes, for a paste control it doesn't fit.</summary>
        public static string Where(CopyText c) {
            switch (c.Kind) {
                case CopyKind.Substeps:
                    return "Substeps paste onto a substep row, or at the end of a step or template";
                case CopyKind.Steps:
                    return "Steps paste at the end of a job, or one step onto a step row";
                case CopyKind.Job:
                    return "A job pastes onto a job\u2019s strip, or its steps at the end of a job";
                default:
                    return "A template pastes onto a template\u2019s strip";
            }
        }

        /// <summary>
        /// The toast after a delete or a paste over something, with Undo for about 8 s; any other edit ends the
        /// chance. undo puts the model back and shows it.
        /// </summary>
        public void Offer(string text, Action undo) {
            _undo         = undo;
            _undoRevision = Store.Revision;
            _toast.ShowDone(text, "Undo", delegate {
                Action u = _undo;
                _undo    = null;
                if (u != null) u();
            });
        }

        public void Inform(string text) {
            _toast.ShowInfo(text);
        }

        /// <summary>Shows a job or template again, its view rebuilt, as after an Undo.</summary>
        public void Reshow(object item) {
            Show(item);
        }

        public void PasteJob(Job job) {
            CopyText clip = ReadCopy();
            if (clip == null || clip.Kind != CopyKind.Job) return;
            if (job.Steps.Count > 0 &&
                !Genie.ControlUtils.Confirm(this, "Replace the job \u2018" + job.Name + "\u2019?", "Replace",
                                            "Its settings and " + Genie.TextUtils.Count(job.Steps.Count, "step") +
                                                " give way to the copied job\u2019s. It keeps its name, as names " +
                                                "are unique."))
                return;
            bool       startup = job.RunOnStartup, stop = job.StopAtFirstFailure;
            int        order       = job.Order;
            List<Step> steps       = job.Steps;
            job.RunOnStartup       = clip.Job.RunOnStartup;
            job.StopAtFirstFailure = clip.Job.StopAtFirstFailure;
            job.Order              = clip.Job.Order;
            job.Steps              = clip.Job.Steps;
            Store.Save(job);
            Show(job);
            Offer("Replaced the job \u2018" + job.Name + "\u2019.", delegate {
                job.RunOnStartup       = startup;
                job.StopAtFirstFailure = stop;
                job.Order              = order;
                job.Steps              = steps;
                Store.Save(job);
                Show(job);
            });
        }

        public void PasteTemplate(Template t) {
            CopyText clip = ReadCopy();
            if (clip == null || clip.Kind != CopyKind.Template) return;
            int uses = Store.UsesOf(t).Count;
            if (t.Substeps.Count > 0 &&
                !Genie.ControlUtils.Confirm(
                    this, "Replace the substeps of \u2018" + t.Name + "\u2019?", "Replace",
                    "Its " + Genie.TextUtils.Count(t.Substeps.Count, "substep") + " go" +
                        (uses > 0 ? ", in the " + Genie.TextUtils.Count(uses, "step") + " using it too." : ".")))
                return;
            List<Substep> before = t.Substeps;
            t.Substeps           = clip.Template.Substeps;
            Store.Save(t);
            Show(t);
            Offer("Replaced the substeps of \u2018" + t.Name + "\u2019.", delegate {
                t.Substeps = before;
                Store.Save(t);
                Show(t);
            });
        }

        // == Selection ================================================================================

        /// <summary>Shows a job or template in the right pane, and selects it in the list.</summary>
        public void Select(object item) {
            Show(item);
        }

        private void Show(object item) {
            _selected         = item;
            Job job           = item           as Job;
            Template template = item as Template;
            if (job != null)
                _view.Content = new JobView(this, job);
            else if (template != null)
                _view.Content = new TemplateView(this, template);
            else
                _view.Content = Genie.ControlUtils.CreateEmptyState(
                    "No jobs yet",
                    "A job gets the editor ready for a kind of work: it loads packages and runs scripts, in steps.",
                    "Add one with the + beside ON STARTUP or ON DEMAND.");
            FrameworkElement fe = _view.Content as FrameworkElement;
            if (fe != null && job == null && template == null) fe.Margin = Genie.Theme.PaneInset;

            ListBoxItem li;
            _filling                = true;
            _pane.List.SelectedItem = item != null && _items.TryGetValue(item, out li) ? li : null;
            if (_pane.List.SelectedItem != null) _pane.List.ScrollIntoView(_pane.List.SelectedItem);
            _filling = false;
            UpdateHeader();
            UpdateBands();
        }

        public void Run(Job job, Predicate<RunItem> only) {
            if (!Runner.Start(job, only))
                _toast.ShowInfo("\u2018" + job.Name + "\u2019 is running or waiting to run already.");
        }

        public void Run(Template template, Predicate<RunItem> only) {
            if (!Runner.Start(template, only))
                _toast.ShowInfo("\u2018" + template.Name + "\u2019 is running or waiting to run already.");
        }

        // == Header and bands =========================================================================

        private void UpdateHeader() {
            Job job                    = _selected           as Job;
            Template template          = _selected as Template;
            _jobHeader.Visibility      = template == null ? Visibility.Visible : Visibility.Collapsed;
            _templateHeader.Visibility = template != null ? Visibility.Visible : Visibility.Collapsed;
            if (template != null) {
                _templateHeader.Title  = template.Name;
                _templateSubsteps.Text = Genie.TextUtils.Count(template.Substeps.Count, "substep");
                int uses               = Store.UsesOf(template).Count;
                _usedBy.Text           = "Used by " + Genie.TextUtils.Count(uses, "step");
                Job locker             = Runner.LockerOf(template);
                _templateLastRun.Show(locker != null && locker.RunsTemplate == template ? locker : null,
                                      Runner.LatestOf(template), template.LastRun);
                return;
            }
            _jobHeader.Title      = job != null ? job.Name : "";
            _steps.Visibility     = job != null ? Visibility.Visible : Visibility.Collapsed;
            _onStartup.Visibility = job != null && job.RunOnStartup ? Visibility.Visible : Visibility.Collapsed;
            JobResult r           = job != null ? Runner.LatestOf(job) : null;
            int       failed      = r != null ? r.Count(Outcome.Failed) : 0;
            _failed.Text          = failed + " failed";
            _failed.Visibility    = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (job == null) {
                _lastRun.Clear();
                return;
            }
            _steps.Text = Genie.TextUtils.Count(job.Steps.Count, "step");
            _lastRun.Show(Runner.IsLocked(job) ? job : null, r, job.LastRun);
        }

        /// <summary>
        /// "Last updated 2026-11-18 23:57" under a job's or template's box, at its right: it's about the definition,
        /// not its runs. Nothing until it's first edited, as one saved before Updated was kept has no time.
        /// </summary>
        public static TextBlock CreateUpdated() {
            TextBlock text = Genie.ControlUtils.CreateMutedText();
            // Its right edge on the strips': their rows' right padding (10) and the box's border (1).
            text.HorizontalAlignment = HorizontalAlignment.Right;
            text.Margin              = new Thickness(0, 0, 11, 0);
            return text;
        }

        /// <summary>
        /// The line under a job's or template's box: left (a template's USED BY caption, or null), then Last updated
        /// at the right, on one baseline. One cell, so neither pushes the other down.
        /// </summary>
        public static Grid UnderBox(TextBlock left, TextBlock updated) {
            Grid line = new Grid { Margin = new Thickness(0, 14, 0, 4) };
            if (left != null) {
                left.HorizontalAlignment = HorizontalAlignment.Left;
                line.Children.Add(left);
                Genie.ControlUtils.AlignBaselines(left, updated);
            }
            line.Children.Add(updated);
            return line;
        }

        public static void ShowUpdated(TextBlock text, DateTime? updated) {
            text.Visibility = updated.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (updated.HasValue)
                text.Text = "Last updated " + updated.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        // One message, the first that applies: running (locked), else substeps that will fail, else startup off.
        private void UpdateBands() {
            Job job           = _selected           as Job;
            Template template = _selected as Template;
            bool locked       = job != null ? Runner.IsLocked(job) : template != null && Runner.IsLocked(template);
            // While anything runs, so the bands don't come and go as the selection moves.
            _running.Visibility = Runner.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            Job running         = Runner.RunningJob;
            if (running == null) {
            } else if (job != null && running == job) {
                _running.Text = "Running. This job is locked until the run ends.";
            } else if (job != null && locked) {
                _running.Text =
                    "Waiting to run after \u2018" + running.Name + "\u2019. This job is locked until it " + "has run.";
            } else if (locked) {
                Job user = Runner.LockerOf(template);
                if (user.RunsTemplate != template)
                    _running.Text = "\u2018" + user.Name + "\u2019 is running or waiting with this template, so the " +
                                    "template is locked until that job has run.";
                else if (user == running)
                    _running.Text = "Running. This template is locked until the run ends.";
                else
                    _running.Text = "Waiting to run after \u2018" + running.Name + "\u2019. This template is locked " +
                                    "until it has run.";
            } else {
                _running.Text = "\u2018" + running.Name + "\u2019 is running. This " +
                                (template != null ? "template" : "job") + " isn\u2019t running or waiting.";
            }
            IView view                         = _view.Content as IView;
            int                        failing = view != null ? view.Failing : 0;
            string                     warn    = null;
            if (failing > 0)
                warn = Genie.TextUtils.Count(failing, "substep") + " will fail as set up.";
            else if (job != null && job.RunOnStartup && !Store.StartupJobs.Value)
                warn = "Enable startup jobs is off, so this job won\u2019t run at startup.";
            _warning.Text       = warn ?? "";
            _warning.Visibility = !locked && warn != null ? Visibility.Visible : Visibility.Collapsed;
            if (view != null && (!_lockShown.HasValue || _lockShown.Value != locked || view != _lockedView)) {
                view.SetLocked(locked); // only on a change: it walks the whole view
                _lockShown  = locked;
                _lockedView = view;
            }
        }

        // == List =====================================================================================

        private List<Job> Sorted(bool startup) {
            return startup ? Store.StartupOrder() : Store.OnDemandOrder();
        }

        // Rebuilt only when what's listed, or its order, changed; otherwise each row updates in place.
        private void FillList() {
            List<Job>      startup = Sorted(true), demand = Sorted(false);
            List<Template> templates = new List<Template>(Store.Templates);
            templates.Sort((a, b) => Store.ByName(a.Name, b.Name));
            StringBuilder shape = new StringBuilder();
            foreach (Job j in startup) shape.Append(j.Id).Append(',');
            shape.Append('|');
            foreach (Job j in demand) shape.Append(j.Id).Append(',');
            shape.Append('|');
            foreach (Template t in templates) shape.Append(t.Id).Append(',');

            if (shape.ToString() != _shape) {
                _shape   = shape.ToString();
                _filling = true;
                _items.Clear();
                _startup.ClearItems();
                _demand.ClearItems();
                _templates.ClearItems();
                foreach (Job j in startup) _items[j] = _startup.AddItem(new JobListRow(this, j, true), j);
                foreach (Job j in demand) _items[j] = _demand.AddItem(new JobListRow(this, j, false), j);
                foreach (Template t in templates) _items[t] = _templates.AddItem(new TemplateListRow(this, t), t);
                ListBoxItem li;
                _pane.List.SelectedItem = _selected != null && _items.TryGetValue(_selected, out li) ? li : null;
                _filling                = false;
                _pane.ApplySearch();
            } else {
                foreach (ListBoxItem li in _items.Values) {
                    IListRow row = li.Content as IListRow;
                    if (row != null) row.Update();
                }
                _pane.ApplySearch();
            }
            _startup.Badge.Visibility = Store.StartupJobs.Value ? Visibility.Collapsed : Visibility.Visible;
        }

        // A name match, else the first file the job or template would run.
        private static string Match(object tag, string query) {
            Job job                      = tag           as Job;
            Template template            = tag as Template;
            string                  name = job != null ? job.Name : template != null ? template.Name : null;
            if (name == null) return null;
            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return "";
            List<RunItem> items = job != null ? Issues.Order(job, false) : Issues.Order(template);
            foreach (RunItem item in items) {
                string file = Path.GetFileName(item.Substep.File);
                if (file.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return (item.Substep.Kind == SubstepKind.Package ? "Loads " : "Runs ") + file;
            }
            return null;
        }

        private void AddJob(bool startup) {
            int order = 1;
            foreach (Job j in Store.Jobs) {
                if (j.RunOnStartup) order = Math.Max(order, j.Order + 1);
            }
            Job job = new Job { Name = Genie.TextUtils.FreeName("New job", n => Store.CheckJobName(n, null) == null),
                                RunOnStartup = startup, Order = order };
            job.Steps.Add(new Step { Name = "Step 1" });
            Store.Add(job);
            Show(job);
        }

        private void AddTemplate() {
            Template t = new Template { Name = Genie.TextUtils.FreeName(
                                            "New template", n => Store.CheckTemplateName(n, null) == null) };
            Store.Add(t);
            Show(t);
        }

        public void DeleteJob(Job job) {
            if (!Genie.ControlUtils.Confirm(this, "Delete the job \u2018" + job.Name + "\u2019?", "Delete",
                                            "Its " + Genie.TextUtils.Count(job.Steps.Count, "step") + " go with it.",
                                            null, Genie.ControlUtils.TagTone.Danger))
                return;
            int index = Store.Jobs.IndexOf(job);
            Store.Delete(job);
            Show(Store.Jobs.Count > 0 ? (object)Store.Jobs[0] : Store.Templates.Count > 0 ? Store.Templates[0] : null);
            Offer("Deleted the job \u2018" + job.Name + "\u2019.", delegate {
                Store.Insert(index, job);
                Show(job);
            });
        }

        // Steps using it go back to their own substeps; Undo puts them on it again.
        public void DeleteTemplate(Template t) {
            int uses = Store.UsesOf(t).Count;
            if (!Genie.ControlUtils.Confirm(this, "Delete the template \u2018" + t.Name + "\u2019?", "Delete",
                                            uses > 0 ? Genie.TextUtils.Count(uses, "step") +
                                                           " use it; they go back to their own substeps."
                                                     : null,
                                            null, Genie.ControlUtils.TagTone.Danger))
                return;
            int        index = Store.Templates.IndexOf(t);
            List<Step> users = Store.Delete(t);
            Show(Store.Templates.Count > 0 ? (object)Store.Templates[0] : Store.Jobs.Count > 0 ? Store.Jobs[0] : null);
            Offer("Deleted the template \u2018" + t.Name + "\u2019.", delegate {
                Store.Insert(index, t);
                foreach (Step s in users) {
                    s.TemplateId = t.Id;
                    Job job      = Store.JobOf(s);
                    if (job != null) Store.Save(job);
                }
                Show(t);
            });
        }
    }

    internal interface IView {
        void Refresh();
        int  Failing { get; }
        void SetLocked(bool locked);
        void UpdatePaste();
    }

    internal interface IListRow {
        void Update();
    }

    // [order] dot  name             last run  [play]
    //              2 steps · 7 substeps
    internal sealed class JobListRow : DockPanel, IListRow {
        /// <summary>A job's status dot, from its latest run this session.</summary>
        public static void Status(Job job, out Genie.ControlUtils.TagTone tone, out string tip) {
            JobResult r = Runner.LatestOf(job);
            Outcome   o = r == null ? Outcome.NotRun : r.Outcome;
            tone        = o == Outcome.Failed ? Genie.ControlUtils.TagTone.Danger
                          : o == Outcome.Done ? Genie.ControlUtils.TagTone.Success
                                              : Genie.ControlUtils.TagTone.Neutral;
            tip = o == Outcome.Failed ? "Failed last run" : o == Outcome.Done ? "Ran clean" : "Not run this session";
        }

        private readonly Job       _job;
        private readonly TextBlock _name, _sub, _when;
        private readonly Border    _dot =
            new Border { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly Border    _play = new Border { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _order;
        private readonly Border    _run;
        private Border             _spinner;

        public JobListRow(MainPanel main, Job job, bool startup) {
            _job         = job;
            Margin       = new Thickness(2, 4, 2, 4);
            _order       = StepsBox.Number("");
            _order.Width = 18;
            if (startup) {
                DockPanel.SetDock(_order, Dock.Left);
                Children.Add(_order);
            }
            DockPanel.SetDock(_dot, Dock.Left);
            Children.Add(_dot);
            _run = Genie.ControlUtils.CreateRunIcon(delegate { main.Run(job, null); }, "Run this job");
            DockPanel.SetDock(_play, Dock.Right);
            Children.Add(_play);
            _when = new TextBlock {
                FontSize          = Genie.Theme.FontSizeTag,
                Foreground        = Genie.Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 4, 0),
            };
            DockPanel.SetDock(_when, Dock.Right);
            Children.Add(_when);
            StackPanel lines = new StackPanel();
            _name =
                new TextBlock { FontSize = Genie.Theme.FontSizeBody, TextTrimming = TextTrimming.CharacterEllipsis };
            _sub = new TextBlock { FontSize = Genie.Theme.FontSizeTag, Foreground = Genie.Theme.TextSecondary };
            lines.Children.Add(_name);
            lines.Children.Add(_sub);
            Children.Add(lines);
            Update();
        }

        public void Update() {
            _order.Text = _job.Order.ToString(CultureInfo.InvariantCulture);
            _name.Text  = _job.Name;
            Genie.ControlUtils.SetSearchable(_name);
            _sub.Text  = Genie.TextUtils.Count(_job.Steps.Count, "step") + " \u00b7 " +
                         Genie.TextUtils.Count(_job.SubstepCount, "substep");
            _when.Text = _job.LastRun.HasValue ? Genie.TextUtils.When(_job.LastRun.Value) : "Never run";
            if (Runner.RunningJob == _job)
                _when.Text = Genie.TextUtils.Duration(DateTime.Now - Runner.LatestOf(_job).Start);
            else if (Runner.IsLocked(_job))
                _when.Text = "waiting";
            Genie.ControlUtils.SetToolTip(_when, Genie.TextUtils.WhenInFull(_job.LastRun));

            Genie.ControlUtils.TagTone tone;
            string                     tip;
            Status(_job, out tone, out tip);
            _dot.Child = Genie.ControlUtils.CreateDot(tone, tip);
            if (Runner.IsLocked(_job)) {
                // One spinner for the row's life, so it keeps turning (CreateSpinner).
                if (_spinner == null)
                    _spinner =
                        new Border { Child = Genie.ControlUtils.CreateSpinner(14), Background = Brushes.Transparent };
                Genie.ControlUtils.SetToolTip(_spinner, Runner.RunningJob == _job ? "Running" : "Waiting to run");
                _play.Child = _spinner;
            } else {
                _play.Child = _run;
            }
        }
    }

    // layers  name                         last run  [run]
    //         3 substeps · used by 2 steps
    internal sealed class TemplateListRow : DockPanel, IListRow {
        private readonly Template  _template;
        private readonly TextBlock _name, _sub, _when;
        private readonly Border    _play = new Border { VerticalAlignment = VerticalAlignment.Center };
        private readonly Border    _run;
        private Border             _spinner;

        public TemplateListRow(MainPanel main, Template t) {
            _template             = t;
            Margin                = new Thickness(2, 4, 2, 4);
            FrameworkElement icon = Layers();
            DockPanel.SetDock(icon, Dock.Left);
            Children.Add(icon);
            _run = Genie.ControlUtils.CreateRunIcon(delegate { main.Run(t, null); }, "Run this template");
            DockPanel.SetDock(_play, Dock.Right);
            Children.Add(_play);
            _when = new TextBlock {
                FontSize          = Genie.Theme.FontSizeTag,
                Foreground        = Genie.Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 4, 0),
            };
            DockPanel.SetDock(_when, Dock.Right);
            Children.Add(_when);
            StackPanel lines = new StackPanel();
            _name =
                new TextBlock { FontSize = Genie.Theme.FontSizeBody, TextTrimming = TextTrimming.CharacterEllipsis };
            _sub = new TextBlock { FontSize = Genie.Theme.FontSizeTag, Foreground = Genie.Theme.TextSecondary };
            lines.Children.Add(_name);
            lines.Children.Add(_sub);
            Children.Add(lines);
            Update();
        }

        public void Update() {
            _name.Text = _template.Name;
            Genie.ControlUtils.SetSearchable(_name);
            Job  locker    = Runner.LockerOf(_template);
            bool own       = locker != null && locker.RunsTemplate == _template;
            bool running   = locker != null && Runner.RunningJob == locker;
            _sub.Text      = locker != null
                                 ? "locked by a run"
                                 : Genie.TextUtils.Count(_template.Substeps.Count, "substep") + " \u00b7 used by " +
                                       Genie.TextUtils.Count(Store.UsesOf(_template).Count, "step");
            DateTime? last = _template.LastRun;
            _when.Text     = own && running  ? Genie.TextUtils.Duration(DateTime.Now - Runner.LatestOf(_template).Start)
                             : own           ? "waiting"
                             : last.HasValue ? Genie.TextUtils.When(last.Value)
                                             : "Never run";
            Genie.ControlUtils.SetToolTip(_when, Genie.TextUtils.WhenInFull(last));
            if (locker == null) {
                _play.Child = _run;
                return;
            }
            // One spinner for the row's life, so it keeps turning (CreateSpinner).
            if (_spinner == null)
                _spinner =
                    new Border { Child = Genie.ControlUtils.CreateSpinner(14), Background = Brushes.Transparent };
            Genie.ControlUtils.SetToolTip(_spinner, !own      ? "In the run, through \u2018" + locker.Name + "\u2019"
                                                    : running ? "Running"
                                                              : "Waiting to run");
            _play.Child = _spinner;
        }

        // Templates are purple throughout (Type), as blue means info.
        internal static FrameworkElement Layers() {
            FrameworkElement icon = Genie.ControlUtils.CreateLayersIcon(Genie.Theme.TypeTagText);
            icon.Margin           = new Thickness(2, 0, 6, 0);
            return icon;
        }
    }

    // == Job and template views =======================================================================

    // [Job name | box] [x Run on startup | Order n] [x] Stop at first failure ...... [run|copy|paste|trash]
    // then the steps box.
    internal sealed class JobView : UserControl, IView {
        private readonly MainPanel        _main;
        private readonly Job              _job;
        private readonly StepsBox         _box;
        private readonly TextBlock        _updated = MainPanel.CreateUpdated();
        private readonly Border           _paste;
        private readonly FrameworkElement _lock;

        public JobView(MainPanel main, Job job) {
            _main          = main;
            _job           = job;
            StackPanel all = new StackPanel { Margin = Genie.Theme.PaneInset };

            // Its strip's right edge on the steps' strips: their rows' right padding (10) and the box's border (1).
            DockPanel settings = new DockPanel { Margin = new Thickness(0, 0, 11, 0) };
            _paste       = Genie.ControlUtils.CreatePasteIcon(delegate { main.PasteJob(job); },
                                                              "Paste the copied job over this one; it keeps its name");
            Border strip = Genie.ControlUtils.CreateIconGroup(
                Genie.ControlUtils.ButtonSize.Small,
                Genie.ControlUtils.CreateRunIcon(delegate { main.Run(job, null); }, "Run this job"),
                Genie.ControlUtils.CreateCopyIcon(delegate { return CopyText.Of(job); }, "Copy this job"), _paste,
                Genie.ControlUtils.CreateDeleteIcon(delegate { main.DeleteJob(job); }, "Delete this job"));
            strip.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(strip, Dock.Right);
            settings.Children.Add(strip);

            TextBox          name     = Genie.ControlUtils.CreateTextBox(job.Name);
            GenieProblemText problem  = Genie.ControlUtils.CreateProblemText(name);
            name.TextChanged         += delegate {
                problem.Problem = Store.CheckJobName(name.Text, job);
                if (problem.Problem != null) return; // kept as it was until it's free
                job.Name = name.Text.Trim();
                Store.Save(job);
            };
            GenieFieldGroup nameGroup = Genie.ControlUtils.CreateFieldGroup("Job name", name);
            nameGroup.Width           = 260;

            const string   StartupTip = "Run it unattended when the editor opens, in Order";
            CheckBox       startup = Genie.ControlUtils.CreateCheckBox("Run on startup", job.RunOnStartup, StartupTip);
            GenieNumberBox order = Genie.ControlUtils.CreateNumberBox(job.Order, 1, 999, delegate(int? v) {
                if (!v.HasValue) return;
                job.Order = v.Value;
                Store.Save(job);
            }, null, false, true, 1, "Startup jobs run lowest first; ties run by name");
            // Order greys out while Run on startup is off, keeping its value.
            GenieFieldGroup startupGroup  = Genie.ControlUtils.CreateFieldGroup(startup, "Order", order);
            startup.Checked              += delegate {
                job.RunOnStartup = true;
                Store.Save(job);
            };
            startup.Unchecked += delegate {
                job.RunOnStartup = false;
                Store.Save(job);
            };
            CheckBox stop  = Genie.ControlUtils.CreateCheckBox("Stop at first failure", job.StopAtFirstFailure,
                                                               "A failed substep skips the rest of this job");
            stop.Checked  += delegate {
                job.StopAtFirstFailure = true;
                Store.Save(job);
            };
            stop.Unchecked += delegate {
                job.StopAtFirstFailure = false;
                Store.Save(job);
            };

            WrapPanel left = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (FrameworkElement fe in new FrameworkElement[] { nameGroup, startupGroup, stop }) {
                fe.Margin            = new Thickness(0, 0, 12, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
                left.Children.Add(fe);
            }
            settings.Children.Add(left);
            all.Children.Add(settings);
            all.Children.Add(problem);

            _box        = new StepsBox(main, job, null);
            _box.Margin = new Thickness(0, 10, 0, 0);
            all.Children.Add(_box);
            all.Children.Add(MainPanel.UnderBox(null, _updated));
            MainPanel.ShowUpdated(_updated, job.Updated);

            Content = new ScrollViewer {
                Content                       = all,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _lock = all;
            UpdatePaste();
        }

        public void Refresh() {
            _box.Refresh();
            MainPanel.ShowUpdated(_updated, _job.Updated);
        }

        public int Failing {
            get { return _box.FailingCount(); }
        }

        public void SetLocked(bool locked) {
            Genie.ControlUtils.SetLocked(_lock, locked);
        }

        public void UpdatePaste() {
            _main.UpdatePaste(_paste, c => c.Kind == CopyKind.Job ? null : MainPanel.Where(c));
            _box.UpdatePaste();
        }
    }

    // [Template name | box] ...... [run|copy|paste|trash], the substeps box, then USED BY: Job › 3. Step links.
    internal sealed class TemplateView : UserControl, IView {
        private readonly Template         _template;
        private readonly StepsBox         _box;
        private readonly TextBlock        _updated = MainPanel.CreateUpdated();
        private readonly StackPanel       _uses = new StackPanel();
        private readonly MainPanel        _main;
        private readonly Border           _paste;
        private readonly FrameworkElement _lock;

        public TemplateView(MainPanel main, Template t) {
            _main          = main;
            _template      = t;
            StackPanel all = new StackPanel { Margin = Genie.Theme.PaneInset };

            // Its strip's right edge on the steps' strips: their rows' right padding (10) and the box's border (1).
            DockPanel settings = new DockPanel { Margin = new Thickness(0, 0, 11, 0) };
            _paste             = Genie.ControlUtils.CreatePasteIcon(delegate {
                main.PasteTemplate(t);
            }, "Paste the copied template\u2019s substeps over " + "these; the name stays");
            Border strip       = Genie.ControlUtils.CreateIconGroup(
                Genie.ControlUtils.ButtonSize.Small,
                Genie.ControlUtils.CreateRunIcon(delegate { main.Run(t, null); }, "Run this template"),
                Genie.ControlUtils.CreateCopyIcon(delegate { return CopyText.Of(t); }, "Copy this template"), _paste,
                Genie.ControlUtils.CreateDeleteIcon(delegate { main.DeleteTemplate(t); }, "Delete this template"));
            DockPanel.SetDock(strip, Dock.Right);
            settings.Children.Add(strip);
            TextBox          name     = Genie.ControlUtils.CreateTextBox(t.Name);
            GenieProblemText problem  = Genie.ControlUtils.CreateProblemText(name);
            name.TextChanged         += delegate {
                problem.Problem = Store.CheckTemplateName(name.Text, t);
                if (problem.Problem != null) return; // kept as it was until it's free
                t.Name = name.Text.Trim();
                Store.Save(t);
            };
            GenieFieldGroup nameGroup     = Genie.ControlUtils.CreateFieldGroup("Template name", name);
            nameGroup.Width               = 260;
            nameGroup.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Children.Add(nameGroup);
            all.Children.Add(settings);
            all.Children.Add(problem);

            _box        = new StepsBox(main, null, t);
            _box.Margin = new Thickness(0, 10, 0, 0);
            all.Children.Add(_box);
            MainPanel.ShowUpdated(_updated, t.Updated);

            all.Children.Add(MainPanel.UnderBox(Genie.ControlUtils.CreateCaption("Used by"), _updated));
            all.Children.Add(_uses);
            Content = new ScrollViewer {
                Content                       = all,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _lock = all;
            FillUses();
            UpdatePaste();
        }

        private void FillUses() {
            _uses.Children.Clear();
            List<KeyValuePair<Job, Step>> uses = Store.UsesOf(_template);
            if (uses.Count == 0) _uses.Children.Add(Genie.ControlUtils.CreateNoteText("No step uses it yet."));
            foreach (KeyValuePair<Job, Step> u in uses) {
                Job       job  = u.Key;
                string    step = (job.Steps.IndexOf(u.Value) + 1) + ". " + u.Value.Name;
                TextBlock link = Genie.ControlUtils.CreateLink(job.Name + " \u203a " + step,
                                                               delegate { _main.Select(job); }, "Open the job");
                link.Margin    = new Thickness(0, 0, 0, 3);
                _uses.Children.Add(link);
            }
        }

        public void Refresh() {
            _box.Refresh();
            MainPanel.ShowUpdated(_updated, _template.Updated);
            FillUses();
        }

        public int Failing {
            get { return _box.FailingCount(); }
        }

        public void SetLocked(bool locked) {
            Genie.ControlUtils.SetLocked(_lock, locked);
        }

        public void UpdatePaste() {
            _main.UpdatePaste(_paste, c => c.Kind == CopyKind.Template ? null : MainPanel.Where(c));
            _box.UpdatePaste();
        }
    }

    // == Run history ==================================================================================
    // Runs newest first, a section per editor session: this session's from memory (live while one runs), earlier
    // ones read back from their log files. A run shows a header row per job, then a line per step heading its
    // substeps, and under a substep its script output or load errors.

    internal sealed class HistoryPanel : UserControl {
        private enum Expand { Never, FailedSteps, AllSteps }

        private const string ExpandKey = "AutoExpand";

        private readonly GenieHeader     _header;
        private readonly GenieCountPill  _when, _done, _failed, _took;
        private readonly ComboBox        _expand;
        private readonly CheckBox        _errorsOnly;
        private readonly GenieListPane   _pane;
        private readonly ScrollViewer    _scroll;
        private readonly Border          _logFind;
        private readonly GenieStatusPill _toast;
        private readonly GenieNumberBox  _keepBox;

        private readonly GenieListSection _thisSession;
        private readonly Dictionary<RunResult, ListBoxItem> _items = new Dictionary<RunResult, ListBoxItem>();
        private RunResult                                   _shown;
        private object                                      _focus; // a step's or substep's result to open
        private bool                                        _filling;
        private int                                         _listed;
        private bool                                        _loadingPast = true;

        // Opened or closed by a click, for the session, keyed by run, job and step (and substep for output).
        // Auto-expand and failures only set how they start.
        private static readonly Dictionary<string, bool> StepOpen   = new Dictionary<string, bool>();
        private static readonly Dictionary<string, bool> OutputOpen = new Dictionary<string, bool>();

        public HistoryPanel(GenieWindow window) {
            window.ShowAddonInfo = false; // a subwindow
            Grid root            = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // RUN title [when] [done] [failed] [took] ... Auto-expand [▾] [x] Show errors only [Copy] [Open folder]
            // gear
            _header = Genie.ControlUtils.CreatePanelHeader(window, "RUN", Genie.ControlUtils.WindowControl.Settings);
            _when   = Pill(Genie.ControlUtils.TagTone.Neutral);
            _done   = Pill(Genie.ControlUtils.TagTone.Neutral);
            _failed = Pill(Genie.ControlUtils.TagTone.Danger);
            _took   = Pill(Genie.ControlUtils.TagTone.Neutral);
            Genie.ControlUtils.SetToolTip(_took, "How long the run took");

            const string ExpandTip = "Which steps start open when a run is shown. Open or close any step by its arrow.";
            _expand = Genie.ControlUtils.CreateDropdown(new[] { "Never", "Failed steps", "All steps" }, null, ExpandTip,
                                                        Genie.ControlUtils.ButtonSize.Regular);
            _expand.SelectedIndex     = (int)SavedExpand();
            _expand.SelectionChanged += delegate {
                Genie.ConfigUtils.SetEnum(ExpandKey, (Expand)_expand.SelectedIndex, RunLog.Section);
                StepOpen.Clear(); // as reopening the window would draw it
                Render();
            };
            TextBlock expandLabel         = Genie.ControlUtils.CreateFieldLabel("Auto-expand");
            expandLabel.Margin            = new Thickness(0, 0, 6, 0);
            expandLabel.VerticalAlignment = VerticalAlignment.Center;
            Genie.ControlUtils.SetToolTip(expandLabel, ExpandTip);
            _errorsOnly        = Genie.ControlUtils.CreateCheckBox("Show errors only", false,
                                                                   "Show only substeps that failed, and their output");
            _errorsOnly.Margin = new Thickness(12, 0, 10, 0);
            _errorsOnly.VerticalAlignment  = VerticalAlignment.Center;
            _errorsOnly.Checked           += delegate {
                Render();
            };
            _errorsOnly.Unchecked += delegate {
                Render();
            };
            Button copy = Genie.ControlUtils.CreateButton("Copy", tooltip: "Copy this run as text", onClick: CopyRun);
            Button open = Genie.ControlUtils.CreateButton(
                "Open folder", tooltip: "Open the folder of log files, one per editor session",
                onClick: delegate { Genie.FileUtils.OpenFolder(RunLog.Folder, true); });
            _header.AddAction(expandLabel);
            _header.AddAction(_expand);
            _header.AddAction(_errorsOnly);
            _header.AddAction(copy);
            _header.AddAction(open);
            root.Children.Add(_header);

            // Runs | the run shown
            _pane                        = Genie.ControlUtils.CreateListPane("Runs", true);
            _pane.Margin                 = new Thickness(10, 10, 0, 10);
            _pane.List.SelectionChanged += delegate {
                if (_filling) return;
                RunResult run = _pane.List.SelectedTag<RunResult>();
                if (run != null && run != _shown) Show(run);
            };
            // With the session's id, to find its lines in addons.log.
            _thisSession           = _pane.List.AddSection("This session \u00b7 " + Genie.GetSessionId());
            _thisSession.EmptyText = "No runs yet.";

            _scroll = new ScrollViewer {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding                       = Genie.Theme.PaneInset,
                Focusable                     = false,
            };
            Grid right = new Grid();
            right.Children.Add(_scroll);
            _toast = Genie.ControlUtils.CreateStatusPill();
            right.Children.Add(_toast);
            Grid body = Genie.ControlUtils.CreateSplitPanel(_pane, right, 0.27, 200, 420, "RunHistorySplit");
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // Keep the last [300] sessions [find the shown run's log file]
            GenieNumberBox keepBox = _keepBox = Genie.ControlUtils.CreateNumberBox(
                RunLog.Keep, 1, 9999, delegate(int? n) {
                if (n.HasValue) RunLog.Keep = n.Value;
                }, null, false, true, 1, "1 to 9999");
            keepBox.RevertInvalid = true;
            keepBox.Margin        = new Thickness(6, 0, 6, 0);
            StackPanel keep = new StackPanel { Orientation = Orientation.Horizontal };
            keep.Children.Add(FooterText("Keep the last"));
            keep.Children.Add(keepBox);
            keep.Children.Add(FooterText("sessions"));
            Genie.ControlUtils.SetToolTip(keep, "A session is one editor launch, with its own log file. Older ones " +
                                                    "are deleted when the editor opens.");
            _logFind = Genie.ControlUtils.CreateFindIcon(
                delegate {
                    if (_shown != null) Genie.FileUtils.ShowInExplorer(_shown.LogFile);
                },
                "Show this run\u2019s log file in File Explorer");
            _logFind.Margin            = new Thickness(8, 0, 0, 0);
            _logFind.VerticalAlignment = VerticalAlignment.Center;
            StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(keep);
            bar.Children.Add(_logFind);
            Border footer = Genie.ControlUtils.CreateFooter(bar, new Thickness(12, 6, 12, 6));
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;

            FillThisSession();
            string folder = RunLog.Folder;
            Genie.WorkUtils.RunInBackground(() => RunLog.PastSessions(folder), AddPastSessions, delegate(Exception ex) {
                Genie.Log("Couldn't read the earlier run logs: " + ex.Message);
                AddPastSessions(new List<SessionLog>());
            });
            Genie.ControlUtils.WhileLoaded(this,
                                           delegate {
                                               Runner.Changed       += OnRunChanged;
                                               Store.IgnoresChanged += RenderInPlace;
                                               Store.Reloaded       += OnReloaded;
                                           },
                                           delegate {
                                               Runner.Changed       -= OnRunChanged;
                                               Store.IgnoresChanged -= RenderInPlace;
                                               Store.Reloaded       -= OnReloaded;
                                           });
        }

        /// <summary>Shows a run (null: the latest), its focus's step open and in view.</summary>
        public void Open(RunResult run, bool errorsOnly, object focus) {
            _focus = focus;
            if (errorsOnly && _errorsOnly.IsChecked != true) _errorsOnly.IsChecked = true; // renders
            Show(run ?? Latest());
        }

        private RunResult Latest() {
            if (Runner.History.Count > 0) return Runner.History[Runner.History.Count - 1];
            foreach (GenieListSection s in _pane.List.Sections) {
                if (s.Rows.Count > 0) return GenieList.TagOf<RunResult>(s.Rows[0]);
            }
            return null;
        }

        private void Show(RunResult run) {
            _shown = run;
            ListBoxItem li;
            _filling                = true;
            _pane.List.SelectedItem = run != null && _items.TryGetValue(run, out li) ? li : null;
            if (_pane.List.SelectedItem != null) _pane.List.ScrollIntoView(_pane.List.SelectedItem);
            _filling = false;
            _scroll.ScrollToTop();
            Render();
        }

        // They arrive after the window opens. A run it was opened at, from an earlier log, gets its row now.
        private void AddPastSessions(List<SessionLog> past) {
            _loadingPast = false;
            _filling     = true;
            foreach (SessionLog s in past) {
                bool   same = past.Exists(o => o != s && o.Start.Date == s.Start.Date);
                string name = s.Start.ToString(same ? "MMM d, HH:mm" : "MMM d", CultureInfo.CurrentCulture);
                string id   = s.Runs[0].Session;
                AddRuns(_pane.List.AddSection(id != null ? name + " \u00b7 " + id : name), s.Runs);
            }
            ListBoxItem selected;
            if (_shown != null && _items.TryGetValue(_shown, out selected)) {
                _pane.List.SelectedItem = selected;
                _pane.List.ScrollIntoView(selected);
            }
            _filling = false;
            if (_shown == null) Show(Latest());
        }

        // This session's runs, newest first. Only when one is added; otherwise each row updates in place.
        private void FillThisSession() {
            _filling = true;
            foreach (ListBoxItem li in _thisSession.Rows) _items.Remove(GenieList.TagOf<RunResult>(li));
            _thisSession.ClearItems();
            AddRuns(_thisSession, Runner.History);
            _listed = Runner.History.Count;
            ListBoxItem selected;
            _pane.List.SelectedItem = _shown != null && _items.TryGetValue(_shown, out selected) ? selected : null;
            _filling                = false;
        }

        private void AddRuns(GenieListSection section, List<RunResult> runs) {
            for (int i = runs.Count - 1; i >= 0; i--)
                _items[runs[i]] = section.AddItem(new RunListRow(runs[i]), runs[i]);
        }

        private void OnRunChanged() {
            if (Runner.History.Count != _listed) {
                FillThisSession();
            } else if (Runner.Current != null) {
                ListBoxItem li;
                if (_items.TryGetValue(Runner.Current, out li)) ((RunListRow)li.Content).Update();
            }
            if (_shown == null || _shown == Runner.Current) {
                if (_shown == null) _shown = Latest();
                RenderInPlace(); // a running run redraws as it goes
            }
        }

        // A restore can change its two settings.
        private void OnReloaded() {
            _expand.SelectedIndex = (int)SavedExpand();
            _keepBox.Value        = RunLog.Keep;
        }

        private void RenderInPlace() {
            double offset = _scroll.VerticalOffset;
            Render();
            _scroll.ScrollToVerticalOffset(offset);
        }

        private void CopyRun() {
            if (_shown != null && Genie.ClipboardUtils.SetText(RunLog.Text(_shown)))
                _toast.ShowDone("Copied the run as text.");
        }

        // == Drawing ==================================================================================

        private void Render() {
            UpdateHeader();
            RunResult run = _shown;
            if (run == null && _loadingPast) {
                _scroll.Content = null;
                return;
            }
            if (run == null) {
                FrameworkElement empty = Genie.ControlUtils.CreateEmptyState(
                    "No runs yet", "Each run of a job shows here: what each substep did, and a script\u2019s output.",
                    "Runs are kept in a log file per editor session.");
                _scroll.Content = empty;
                return;
            }
            bool         errorsOnly = _errorsOnly.IsChecked == true;
            Expand       expand     = (Expand)Math.Max(0, _expand.SelectedIndex);
            GenieRowList list       = Genie.ControlUtils.CreateRowList();
            list.SetColumns(GenieColumn.Fixed(18), GenieColumn.Fixed(46), GenieColumn.Fixed(210), GenieColumn.Fill,
                            GenieColumn.Auto);
            list.KeepExpanderSpace    = true;
            list.EmptyText            = errorsOnly ? "No failures in this run." : "Nothing ran.";
            FrameworkElement focusRow = null;

            foreach (JobResult job in run.Jobs) {
                List<StepResult> steps =
                    job.Steps.FindAll(s => !errorsOnly || s.Substeps.Exists(r => r.IsProblem));
                if (errorsOnly && steps.Count == 0) continue; // else shown even with no steps: it did start
                int      failed = job.Count(Outcome.Failed);
                GenieRow head   = list.AddRow(
                    null, Genie.ControlUtils.CreateMutedText((job.IsTemplate ? "Template " : "Job ") + job.Number),
                    Genie.ControlUtils.CreateBoldText(job.Name),
                    Right(Genie.ControlUtils.CreateMutedText(Genie.TextUtils.Count(job.Steps.Count, "step") + ", " +
                                                               Genie.TextUtils.Count(job.All().Count, "substep") +
                                                               (failed > 0 ? ", " + failed + " failed" : "") +
                                                               Took(run, job))),
                    null);
                head.Fill       = Genie.Theme.PanelBackground;

                foreach (StepResult step in steps) {
                    List<SubstepResult> subs = step.Substeps.FindAll(r => !errorsOnly || r.IsProblem);
                    int                 bad  = step.Substeps.FindAll(r => r.Outcome == Outcome.Failed).Count;
                    TextBlock           summary =
                        Genie.ControlUtils.CreateMutedText(Genie.TextUtils.Count(step.Substeps.Count, "substep") +
                                                           (bad > 0 ? ", " + bad + " failed" : ""));
                    GenieRow row =
                        list.AddRow(StepsBox.Mark(step.Outcome, Tip(step.Outcome)),
                                    Genie.ControlUtils.CreateMutedText("Step " + step.Number), StepTitle(step),
                                    Right(summary), Time(subs.Count > 0 ? subs[0].Start : job.Start));
                    row.ExpandToolTip   = "Show or hide this step\u2019s substeps";
                    row.ShowBranchLines = true;
                    foreach (SubstepResult r in subs) {
                        GenieRow sr = AddSubstep(row, run, job, r);
                        if (_focus == r) focusRow = sr;
                    }
                    string key = Key(run, job) + "/" + step.Number;
                    bool   open;
                    // A step running now opens, and stays open once it's done, so its output can be watched.
                    if (step.Outcome == Outcome.Running && !StepOpen.ContainsKey(key)) StepOpen[key] = true;
                    if (!StepOpen.TryGetValue(key, out open))
                        open = expand == Expand.AllSteps ||
                               expand == Expand.FailedSteps && step.Substeps.Exists(r => r.IsProblem);
                    if (_focus == step || _focus is SubstepResult && step.Substeps.Contains((SubstepResult)_focus)) {
                        open = true;
                        if (focusRow == null) focusRow = row;
                    }
                    row.IsExpanded       = open;
                    summary.Visibility   = open ? Visibility.Collapsed : Visibility.Visible;
                    row.ExpandedChanged += delegate(bool isOpen) {
                        StepOpen[key]      = isOpen;
                        summary.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
                    };
                }
            }
            _scroll.Content = list;
            _focus          = null;
            if (focusRow != null)
                Dispatcher.BeginInvoke(new Action(focusRow.BringIntoView),
                                       System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // The substep a failed load's errors can be ignored on, as it is now. A run is a record: ignoring changes the
        // next run, not this one.
        private sealed class IgnoreTarget {
            public Substep       Substep;
            public List<Substep> List;
        }

        private static IgnoreTarget TargetOf(SubstepResult r) {
            if (r.Kind != SubstepKind.Package || r.Outcome != Outcome.Failed) return null;
            List<Substep> list;
            Substep       s = Store.FindSubstep(r.SubstepId, out list);
            if (s == null || s.Kind != SubstepKind.Package || !Genie.FileUtils.IsSamePath(s.File, r.File)) return null;
            return new IgnoreTarget { Substep = s, List = list };
        }

        private void ChangeIgnores(IgnoreTarget target, Action<Substep> change) {
            if (Runner.IsLocked(target.List)) {
                _toast.ShowInfo("It\u2019s running or waiting to run: try again once it has run.");
                return;
            }
            change(target.Substep);
            Store.SaveIgnores(target.List); // redraws this window
        }

        // At the message's right end: how many were ignored, or Ignore these N.
        private FrameworkElement MessageEnd(SubstepResult r, IgnoreTarget target) {
            if (r.Ignored.Count > 0)
                return Genie.ControlUtils.CreateTag(Genie.TextUtils.Count(r.Ignored.Count, "error") + " ignored",
                                                    Genie.ControlUtils.TagTone.Neutral,
                                                    "Load errors this substep is set to ignore", null, null,
                                                    Genie.ControlUtils.TagSize.Small);
            if (target == null) return null;
            List<string> open = new List<string>();
            foreach (Genie.ProcessUtils.ProcessLine l in r.Lines) {
                if (l.IsError && !target.Substep.Ignores(l.Text)) open.Add(l.Text);
            }
            if (open.Count < 2) return null;
            return Genie.ControlUtils.CreateButton(
                "Ignore these " + open.Count, size: Genie.ControlUtils.ButtonSize.Small,
                tooltip: "Stop these failing this substep from the next run. Any other error still fails it.",
                onClick: delegate { ChangeIgnores(target, s => s.Ignore(open)); });
        }

        // [mark] 1.2 file.upk  message ............ time, opening to its output; a failure's opens by itself.
        private GenieRow AddSubstep(GenieRow step, RunResult run, JobResult job, SubstepResult r) {
            TextBlock message  = Genie.ControlUtils.CreateMutedText(r.Message);
            message.Foreground = r.IsProblem ? Genie.Theme.DangerTagText : Genie.Theme.TextSecondary;
            Genie.ControlUtils.SetToolTip(message, r.Message.Length > 0 ? r.Message : null);
            GenieFileNameText file = Genie.ControlUtils.CreateFileNameText(r.File, "No file");
            file.VerticalAlignment = VerticalAlignment.Center;
            IgnoreTarget     target = TargetOf(r);
            DockPanel        cell   = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            FrameworkElement end    = MessageEnd(r, target);
            if (end != null) {
                end.Margin            = new Thickness(6, 0, 6, 0);
                end.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(end, Dock.Right);
                cell.Children.Add(end);
            }
            cell.Children.Add(message);
            GenieRow row = step.AddRow(StepsBox.Mark(r.Outcome, Tip(r.Outcome)),
                                       Genie.ControlUtils.CreateMutedText(r.Label), file, cell, Time(r.Start));
            if (r.Lines.Count == 0 && r.Settings.Count == 0 && r.Ignored.Count == 0) return row;
            row.AddRow(Output(r, target));
            row.ExpandToolTip =
                r.Kind == SubstepKind.Script ? "Show or hide the script\u2019s output" : "Show or hide the load errors";
            string key = Key(run, job) + "/" + r.Label;
            bool   open;
            if (r.Outcome == Outcome.Running && !OutputOpen.ContainsKey(key)) OutputOpen[key] = true; // as for steps
            row.IsExpanded       = OutputOpen.TryGetValue(key, out open) ? open : r.Outcome == Outcome.Failed;
            row.ExpandedChanged += delegate(bool isOpen) {
                OutputOpen[key] = isOpen;
            };
            return row;
        }

        // A script's settings at the run (muted), then its output, errors red, then load errors it ignored (muted).
        private UIElement Output(SubstepResult r, IgnoreTarget target) {
            StackPanel lines = new StackPanel();
            foreach (string s in r.Settings) lines.Children.Add(OutputLine(s, Genie.Theme.TextSecondary));
            foreach (Genie.ProcessUtils.ProcessLine l in r.Lines) {
                if (l.IsError && target != null)
                    lines.Children.Add(ErrorLine(l.Text, target));
                else
                    lines.Children.Add(
                        OutputLine(l.Text, l.IsError ? Genie.Theme.DangerTagText : Genie.Theme.TextPrimary));
            }
            foreach (string e in r.Ignored)
                lines.Children.Add(WithEnd(OutputLine(e, Genie.Theme.TextSecondary), SmallTag("Ignored")));
            if (r.Kind == SubstepKind.Script && r.Lines.Count == 0)
                lines.Children.Add(OutputLine("(no output)", Genie.Theme.TextSecondary));
            return new Border {
                Child        = lines,
                Background   = Genie.Theme.FieldLabelBackground,
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(8, 4, 8, 4),
                Margin       = new Thickness(0, 0, 0, 2),
            };
        }

        // A load error of a failed run: [Ignore], or the tag and Undo once it's ignored from the next run.
        private UIElement ErrorLine(string error, IgnoreTarget target) {
            StackPanel end = new StackPanel { Orientation = Orientation.Horizontal };
            bool ignored   = target.Substep.Ignores(error);
            if (!ignored) {
                end.Children.Add(Genie.ControlUtils.CreateButton(
                    "Ignore", size: Genie.ControlUtils.ButtonSize.Small,
                    tooltip: "Stop this error failing this substep from the next run",
                    onClick: delegate { ChangeIgnores(target, s => s.Ignore(new[] { error })); }));
            } else {
                end.Children.Add(SmallTag("Ignored from the next run"));
                if (target.Substep.LoadErrors == LoadErrorMode.IgnoreListed) {
                    TextBlock undo = Genie.ControlUtils.CreateLink("Undo", delegate {
                        ChangeIgnores(target, s => s.StopIgnoring(error));
                    }, "Let this error fail the substep again");
                    undo.Margin            = new Thickness(6, 0, 0, 0);
                    undo.VerticalAlignment = VerticalAlignment.Center;
                    end.Children.Add(undo);
                }
            }
            return WithEnd(OutputLine(error, ignored ? Genie.Theme.TextSecondary : Genie.Theme.DangerTagText), end);
        }

        private static DockPanel WithEnd(TextBlock line, FrameworkElement end) {
            DockPanel row         = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            end.Margin            = new Thickness(8, 0, 0, 0);
            end.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(end, Dock.Right);
            row.Children.Add(end);
            line.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(line);
            return row;
        }

        private static GenieTag SmallTag(string text) {
            return Genie.ControlUtils.CreateTag(text, Genie.ControlUtils.TagTone.Neutral, null, null, null,
                                                Genie.ControlUtils.TagSize.Small);
        }

        private static TextBlock OutputLine(string text, Brush ink) {
            TextBlock t    = Genie.ControlUtils.CreateMonoText(text);
            t.Foreground   = ink;
            t.TextWrapping = TextWrapping.Wrap;
            return t;
        }

        private void UpdateHeader() {
            RunResult  run   = _shown;
            Visibility shown = run != null ? Visibility.Visible : Visibility.Collapsed;
            _when.Visibility = _done.Visibility = _took.Visibility = shown;
            if (run == null) {
                _header.Title      = "No runs yet";
                _failed.Visibility = Visibility.Collapsed;
                Genie.ControlUtils.Block(_logFind, "No runs yet");
                return;
            }
            int failed    = run.Count(Outcome.Failed);
            _header.Title = run.Title;
            _when.Text = (run.Start.Date == DateTime.Today ? "Today"
                                                           : run.Start.ToString("MMMM d", CultureInfo.CurrentCulture)) +
                         " at " + run.Start.ToString("HH:mm", CultureInfo.CurrentCulture);
            _done.Text = run.Count(Outcome.Done) + " done";
            _failed.Text       = failed + " failed";
            _failed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            _took.Text         = run.IsFinished          ? Genie.TextUtils.ShortDuration(run.End - run.Start)
                                 : run == Runner.Current ? "Running"
                                                         : "Didn\u2019t finish";
            if (File.Exists(run.LogFile))
                Genie.ControlUtils.Unblock(_logFind);
            else
                Genie.ControlUtils.Block(_logFind, "Its log file is gone");
        }

        // == Bits =====================================================================================

        private static Expand SavedExpand() {
            return Genie.ConfigUtils.GetEnum(ExpandKey, Expand.FailedSteps, RunLog.Section);
        }

        // ", took 9.1 s" once the job has ended, so a run of several jobs says where its time went. A run that never
        // ended has no end for its last job, only its last substep's.
        private static string Took(RunResult run, JobResult job) {
            if (job.End == DateTime.MinValue || job.End < job.Start) return "";
            if (!run.IsFinished && job == run.Jobs[run.Jobs.Count - 1]) return "";
            return ", took " + Genie.TextUtils.ShortDuration(job.End - job.Start);
        }

        private static string Key(RunResult run, JobResult job) {
            return run.Start.Ticks + "/" + job.Number;
        }

        private static string Tip(Outcome o) {
            switch (o) {
                case Outcome.Done:
                    return "Done";
                case Outcome.Failed:
                    return "Failed";
                case Outcome.Running:
                    return "Running";
                case Outcome.Skipped:
                    return "Skipped";
                case Outcome.Unfinished:
                    return "Started and never ended";
                default:
                    return "Not run yet";
            }
        }

        private GenieCountPill Pill(Genie.ControlUtils.TagTone tone) {
            GenieCountPill pill = Genie.ControlUtils.CreateCountPill("", tone);
            _header.AddPill(pill);
            return pill;
        }

        private static TextBlock FooterText(string text) {
            TextBlock t         = Genie.ControlUtils.CreateFieldLabel(text);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        // The step's name, and its template as it was at the run.
        private static UIElement StepTitle(StepResult step) {
            StackPanel title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(Genie.ControlUtils.CreateBoldText(step.Name));
            if (step.TemplateName != null) {
                GenieTag tag = Genie.ControlUtils.CreateTag(
                    step.TemplateName, Genie.ControlUtils.TagTone.Type, "Template, as it was at this run", null, null,
                    Genie.ControlUtils.TagSize.Small, false, Genie.ControlUtils.CreateLayersIcon(null, 11));
                tag.Margin            = new Thickness(6, 0, 0, 0);
                tag.VerticalAlignment = VerticalAlignment.Center;
                title.Children.Add(tag);
            }
            return title;
        }

        private static TextBlock Right(TextBlock text) {
            text.HorizontalAlignment = HorizontalAlignment.Right;
            return text;
        }

        // Blank for a substep that hasn't started.
        private static TextBlock Time(DateTime t) {
            TextBlock time = Genie.ControlUtils.CreateMonoText(
                t == DateTime.MinValue ? "" : t.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture));
            time.Foreground        = Genie.Theme.TextDisabled;
            time.VerticalAlignment = VerticalAlignment.Center;
            return time;
        }
    }

    // dot  title                time
    //      2 jobs · 1 failed
    internal sealed class RunListRow : DockPanel {
        private readonly RunResult _run;
        private readonly Border    _dot =
            new Border { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _title, _sub, _time;

        public RunListRow(RunResult run) {
            _run   = run;
            Margin = new Thickness(2, 4, 2, 4);
            DockPanel.SetDock(_dot, Dock.Left);
            Children.Add(_dot);
            _time = new TextBlock {
                FontSize          = Genie.Theme.FontSizeTag,
                Foreground        = Genie.Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin            = new Thickness(6, 0, 2, 0),
            };
            DockPanel.SetDock(_time, Dock.Right);
            Children.Add(_time);
            StackPanel lines = new StackPanel();
            _title =
                new TextBlock { FontSize = Genie.Theme.FontSizeBody, TextTrimming = TextTrimming.CharacterEllipsis };
            _sub = new TextBlock { FontSize = Genie.Theme.FontSizeTag, Foreground = Genie.Theme.TextSecondary };
            lines.Children.Add(_title);
            lines.Children.Add(_sub);
            Children.Add(lines);
            Update();
        }

        public void Update() {
            int failed  = _run.Count(Outcome.Failed);
            _title.Text = _run.Title;
            _sub.Text =
                Genie.TextUtils.Count(_run.Jobs.Count, "job") + (failed > 0 ? " \u00b7 " + failed + " failed" : "");
            _time.Text = _run.Start.ToString("HH:mm", CultureInfo.CurrentCulture);
            Genie.ControlUtils.SetToolTip(_time, Genie.TextUtils.WhenInFull(_run.Start));
            bool running = _run == Runner.Current && !_run.IsFinished;
            _dot.Child =
                Genie.ControlUtils.CreateDot(running      ? Genie.ControlUtils.TagTone.Info
                                             : failed > 0 ? Genie.ControlUtils.TagTone.Danger
                                             : !_run.IsFinished ? Genie.ControlUtils.TagTone.Warning
                                                          : Genie.ControlUtils.TagTone.Success,
                                             running      ? "Running"
                                             : failed > 0 ? Genie.TextUtils.Count(failed, "substep") + " failed"
                                             : !_run.IsFinished ? "Didn\u2019t finish"
                                                          : "No failures");
        }
    }
}
