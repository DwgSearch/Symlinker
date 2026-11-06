namespace Symlinker
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;

    using Res = Symlinker.Properties.Resources;

    /// <summary>A value with its display text, for combo boxes.</summary>
    public sealed record Choice<T>(T Value, string Text);

    /// <summary>One row of the plan grid.</summary>
    public sealed class PlanRow : INotifyPropertyChanged
    {
        private string resultText = string.Empty;

        public PlanRow(PlanEntry entry)
        {
            Entry = entry;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public PlanEntry Entry { get; }

        public string LinkPath => Entry.LinkPath.Length == 0 ? "-" : Entry.LinkPath;

        public string Source => Entry.Source;

        public string KindText => Entry.Kind switch
        {
            LinkKind.Symbolic => Res.KindSymbolic,
            LinkKind.Junction => Res.KindJunction,
            _ => Res.KindHard,
        };

        public string StatusText => MainWindow.DescribeStatus(Entry.Status);

        public bool IsBlocked => !Entry.IsRunnable;

        public string ResultText
        {
            get => resultText;
            set
            {
                resultText = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultText)));
            }
        }
    }

    /// <summary>
    /// Two lists: the folders where links are created (left) and the real files/folders (right).
    /// In "all to all" mode running links every item on the right into every folder on the left;
    /// in "manual" mode only the pairs the user picked are linked.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string AppTitle = "Symlinker";

        private sealed class Pane
        {
            public string Name { get; init; }

            public TreeView Tree { get; init; }

            public bool FoldersOnly { get; init; }

            public List<string> Paths { get; set; } = new();
        }

        private readonly Pane leftPane;
        private readonly Pane rightPane;
        private readonly Dictionary<string, Pane> panes;
        private readonly ObservableCollection<PlanRow> rows = new();
        private List<LinkPair> pairs = new();
        private readonly ObservableCollection<LinkSet> sets;
        private readonly bool initialized;
        private bool suppressSetLoad;

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (sender, e) => TitleBarTheme.Apply(this);
            PathGroup.RootLabel = Res.RootGroup;

            leftPane = new Pane { Name = "left", Tree = leftTree, FoldersOnly = true };
            rightPane = new Pane { Name = "right", Tree = rightTree, FoldersOnly = false };
            panes = new Dictionary<string, Pane> { ["left"] = leftPane, ["right"] = rightPane };

            pairingCombo.ItemsSource = new List<Choice<PairingMode>>
            {
                new(PairingMode.All, Res.PairingAll),
                new(PairingMode.Manual, Res.PairingManual),
            };
            pairingCombo.SelectedValue = PairingMode.All;

            modeCombo.ItemsSource = new List<Choice<LinkMode>>
            {
                new(LinkMode.Symbolic, Res.ModeSymbolic),
                new(LinkMode.JunctionAndHardLink, Res.ModeJunctionHard),
            };
            modeCombo.SelectedValue = LinkMode.Symbolic;

            policyCombo.ItemsSource = new List<Choice<ConflictPolicy>>
            {
                new(ConflictPolicy.Skip, Res.PolicySkip),
                new(ConflictPolicy.Overwrite, Res.PolicyOverwrite),
                new(ConflictPolicy.Backup, Res.PolicyBackup),
            };
            policyCombo.SelectedValue = ConflictPolicy.Skip;

            planGrid.ItemsSource = rows;

            sets = new ObservableCollection<LinkSet>(LinkSetStore.Load());
            setCombo.ItemsSource = sets;

            initialized = true;
            UpdatePairingUi();
            RebuildAll();

            Loaded += async (sender, e) => await CheckForUpdatesAtStartupAsync();
        }

        private PairingMode CurrentPairing => pairingCombo.SelectedValue is PairingMode pairing ? pairing : PairingMode.All;

        private bool IsManual => CurrentPairing == PairingMode.Manual;

        private LinkMode CurrentMode => modeCombo.SelectedValue is LinkMode mode ? mode : LinkMode.Symbolic;

        private ConflictPolicy CurrentPolicy => policyCombo.SelectedValue is ConflictPolicy policy ? policy : ConflictPolicy.Skip;

        private Pane PaneOf(object sender) =>
            (sender as FrameworkElement)?.Tag is string name && panes.TryGetValue(name, out var pane) ? pane : null;

        private void Info(string message) =>
            MessageBox.Show(this, message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);

        private bool Confirm(string message) =>
            MessageBox.Show(this, message, AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        /// <summary>Asks for one line of text; null means cancelled.</summary>
        private string Prompt(string message, string defaultText) =>
            InputDialog.Show(this, message, defaultText);

        // ---------- rebuilding the views ----------

        private void RebuildAll()
        {
            foreach (var pane in panes.Values)
            {
                pane.Paths = PathGrouping.Unique(pane.Paths);
                pane.Tree.ItemsSource = PathGrouping.Group(pane.Paths);
            }

            PrunePairs();
            RefreshPlan();
        }

        private void RefreshPlan()
        {
            if (!initialized)
            {
                return;
            }

            var plan = IsManual
                ? LinkPlanner.Build(pairs.Select(pair => (pair.Destination, pair.Source)), CurrentMode)
                : LinkPlanner.Build(leftPane.Paths, rightPane.Paths, CurrentMode);
            rows.Clear();
            foreach (var entry in plan)
            {
                rows.Add(new PlanRow(entry));
            }

            var ready = plan.Count(e => e.Status == PlanStatus.Ready);
            var existing = plan.Count(e => e.Status is PlanStatus.Exists or PlanStatus.AlreadyLinked);
            var blocked = plan.Count(e => !e.IsRunnable);
            planSummary.Text = IsManual
                ? string.Format(CultureInfo.CurrentUICulture, Res.PlanSummaryManual, plan.Count, ready, existing, blocked)
                : string.Format(
                    CultureInfo.CurrentUICulture,
                    Res.PlanSummary,
                    plan.Count, rightPane.Paths.Count, leftPane.Paths.Count, ready, existing, blocked);
        }

        /// <summary>Keeps only pairs whose folder and item are still in the two lists; cleans and de-duplicates them.</summary>
        private void PrunePairs()
        {
            var left = new HashSet<string>(leftPane.Paths, PathGrouping.Comparer);
            var right = new HashSet<string>(rightPane.Paths, PathGrouping.Comparer);
            var seen = new HashSet<(string, string)>(new PairKeyComparer());
            var kept = new List<LinkPair>();
            foreach (var pair in pairs)
            {
                var destination = PathGrouping.Clean(pair.Destination);
                var source = PathGrouping.Clean(pair.Source);
                if (left.Contains(destination) && right.Contains(source) && seen.Add((destination, source)))
                {
                    kept.Add(new LinkPair { Destination = destination, Source = source });
                }
            }

            pairs = kept;
        }

        private sealed class PairKeyComparer : IEqualityComparer<(string, string)>
        {
            public bool Equals((string, string) x, (string, string) y) =>
                PathGrouping.Comparer.Equals(x.Item1, y.Item1) && PathGrouping.Comparer.Equals(x.Item2, y.Item2);

            public int GetHashCode((string, string) obj) =>
                HashCode.Combine(PathGrouping.Comparer.GetHashCode(obj.Item1), PathGrouping.Comparer.GetHashCode(obj.Item2));
        }

        /// <summary>Renames paths on one side and keeps the pairs pointing at the renamed paths.</summary>
        private void ApplyMap(Pane pane, Func<string, string> map)
        {
            pane.Paths = pane.Paths.Select(map).ToList();
            foreach (var pair in pairs)
            {
                if (pane == leftPane)
                {
                    pair.Destination = map(pair.Destination);
                }
                else
                {
                    pair.Source = map(pair.Source);
                }
            }
        }

        private void UpdatePairingUi()
        {
            pairBar.Visibility = IsManual ? Visibility.Visible : Visibility.Collapsed;
            hintText.Text = IsManual ? Res.HintManual : Res.Hint;
        }

        private void Pairing_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!initialized)
            {
                return;
            }

            UpdatePairingUi();
            summaryText.Text = string.Empty;
            RefreshPlan();
        }

        private static List<string> LeavesOf(object item) => item switch
        {
            PathLeaf leaf => new List<string> { leaf.FullPath },
            PathGroup group => group.Children.Select(c => c.FullPath).ToList(),
            _ => new List<string>(),
        };

        private void PairSelected_Click(object sender, RoutedEventArgs e)
        {
            var destinations = LeavesOf(leftTree.SelectedItem);
            var sources = LeavesOf(rightTree.SelectedItem);
            if (destinations.Count == 0 || sources.Count == 0)
            {
                Info(Res.PairSelectBoth);
                return;
            }

            var existing = new HashSet<(string, string)>(
                pairs.Select(pair => (pair.Destination, pair.Source)), new PairKeyComparer());
            var added = 0;
            foreach (var destination in destinations)
            {
                foreach (var source in sources)
                {
                    if (existing.Add((destination, source)))
                    {
                        pairs.Add(new LinkPair { Destination = destination, Source = source });
                        added++;
                    }
                }
            }

            RefreshPlan();
            summaryText.Text = string.Format(CultureInfo.CurrentUICulture, Res.PairsAdded, added);
        }

        private void PairRemove_Click(object sender, RoutedEventArgs e)
        {
            var selected = planGrid.SelectedItems.Cast<PlanRow>()
                .Select(row => (row.Entry.Destination, row.Entry.Source))
                .ToHashSet(new PairKeyComparer());
            if (selected.Count == 0)
            {
                return;
            }

            pairs = pairs.Where(pair => !selected.Contains((pair.Destination, pair.Source))).ToList();
            RefreshPlan();
        }

        /// <summary>Shows the red dot next to About while a newer version is known.</summary>
        private void RefreshUpdateBadge()
        {
            var settings = AppSettings.Load();
            var current = AppInfo.CurrentVersion;
            var hasUpdate = settings.HasUpdate(current);
            updateBadge.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
            aboutButton.ToolTip = hasUpdate
                ? string.Format(
                    CultureInfo.CurrentUICulture, Res.UpdateAvailable, settings.LatestKnownTag,
                    $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}")
                : null;
        }

        /// <summary>
        /// At startup: looks for a newer release (at most once a day) and remembers the result for the red dot.
        /// Then, if a newer version is known and the user was not told about it yet, shows a message once
        /// per version. This does not depend on when the last check ran, so a version found earlier
        /// (for example by the About box) is still announced once. Any network failure is ignored silently.
        /// </summary>
        private async Task CheckForUpdatesAtStartupAsync()
        {
            RefreshUpdateBadge();

            var settings = AppSettings.Load();
            if (settings.IsCheckDue(DateTime.UtcNow))
            {
                var result = await UpdateChecker.CheckAsync();
                if (result.State != UpdateState.Failed)
                {
                    settings.Record(result, DateTime.UtcNow);
                    settings.Save();
                    RefreshUpdateBadge();
                }
            }

            var current = AppInfo.CurrentVersion;
            if (!settings.ShouldAnnounce(current))
            {
                return;
            }

            settings.LastAnnouncedVersion = settings.LatestKnownTag;
            settings.Save();
            var currentText = $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}";
            if (Confirm(string.Format(CultureInfo.CurrentUICulture, Res.UpdatePromptOpen, settings.LatestKnownTag, currentText))
                && Uri.TryCreate(settings.LatestKnownUrl, UriKind.Absolute, out var uri))
            {
                AboutWindow.OpenPage(uri);
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
            RefreshUpdateBadge();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RebuildAll();

        private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPlan();

        // ---------- adding paths ----------

        private void AddFolders_Click(object sender, RoutedEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane == null)
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
            if (dialog.ShowDialog(this) == true)
            {
                AddPaths(pane, dialog.FolderNames);
            }
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane == null)
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
            if (dialog.ShowDialog(this) == true)
            {
                AddPaths(pane, dialog.FileNames);
            }
        }

        private void Pane_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Pane_Drop(object sender, DragEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane != null && e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddPaths(pane, paths);
            }
        }

        /// <summary>
        /// Adds paths as single items. A folder is one item: its contents are never read. The left side
        /// only accepts existing folders (link destinations); the right side accepts existing files and folders.
        /// </summary>
        private void AddPaths(Pane pane, IEnumerable<string> rawPaths)
        {
            var rejected = 0;
            var list = new List<string>(pane.Paths);
            foreach (var raw in rawPaths)
            {
                var full = PathGrouping.Clean(raw);
                var kind = full.Length == 0 ? PathKind.Missing : PathGrouping.KindOf(full);
                if (kind == PathKind.Missing || (pane.FoldersOnly && kind != PathKind.Folder))
                {
                    rejected++;
                    continue;
                }

                list.Add(full);
            }

            pane.Paths = list;
            RebuildAll();
            summaryText.Text = rejected == 0
                ? string.Empty
                : string.Format(CultureInfo.CurrentUICulture, Res.RejectedItems, rejected);
        }

        // ---------- editing ----------

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane != null)
            {
                Edit(pane);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane != null)
            {
                DeleteSelected(pane);
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane != null)
            {
                pane.Paths = new List<string>();
                RebuildAll();
            }
        }

        private void Tree_KeyDown(object sender, KeyEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane == null)
            {
                return;
            }

            if (e.Key == Key.F2)
            {
                Edit(pane);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected(pane);
                e.Handled = true;
            }
        }

        private void Edit(Pane pane)
        {
            switch (pane.Tree.SelectedItem)
            {
                case PathLeaf leaf:
                {
                    var text = Prompt(Res.EditPathPrompt, leaf.FullPath);
                    if (text == null)
                    {
                        return;
                    }

                    var newPath = PathGrouping.Clean(text);
                    if (newPath.Length == 0)
                    {
                        Info(Res.InvalidFullPath);
                        return;
                    }

                    var oldPath = leaf.FullPath;
                    ApplyMap(pane, p => PathGrouping.Comparer.Equals(p, oldPath) ? newPath : p);
                    RebuildAll();
                    break;
                }

                case PathGroup group:
                {
                    if (group.Header.Length == 0)
                    {
                        Info(Res.RootGroupNotEditable);
                        return;
                    }

                    var text = Prompt(Res.EditGroupPrompt, group.Header);
                    if (text == null)
                    {
                        return;
                    }

                    var newHeader = PathGrouping.Clean(text);
                    if (newHeader.Length == 0)
                    {
                        Info(Res.InvalidFullPath);
                        return;
                    }

                    var moved = group.Children.ToDictionary(c => c.FullPath, c => Path.Combine(newHeader, c.Name), PathGrouping.Comparer);
                    ApplyMap(pane, p => moved.TryGetValue(p, out var target) ? target : p);
                    RebuildAll();
                    break;
                }

                default:
                    Info(Res.SelectItemFirst);
                    break;
            }
        }

        private void DeleteSelected(Pane pane)
        {
            HashSet<string> remove;
            switch (pane.Tree.SelectedItem)
            {
                case PathLeaf leaf:
                    remove = new HashSet<string>(PathGrouping.Comparer) { leaf.FullPath };
                    break;
                case PathGroup group:
                    remove = new HashSet<string>(group.Children.Select(c => c.FullPath), PathGrouping.Comparer);
                    break;
                default:
                    return;
            }

            pane.Paths = pane.Paths.Where(p => !remove.Contains(p)).ToList();
            RebuildAll();
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            var pane = PaneOf(sender);
            if (pane == null)
            {
                return;
            }

            var find = Prompt(Res.ReplaceFindPrompt, string.Empty);
            if (string.IsNullOrEmpty(find))
            {
                return;
            }

            var replacement = Prompt(string.Format(CultureInfo.CurrentUICulture, Res.ReplaceWithPrompt, find), string.Empty);
            if (replacement == null)
            {
                return;
            }

            var pattern = Regex.Escape(find);
            var safeReplacement = replacement.Replace("$", "$$");
            const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var changed = pane.Paths.Count(p => !string.Equals(Regex.Replace(p, pattern, safeReplacement, Options), p, StringComparison.Ordinal));
            ApplyMap(pane, p => Regex.Replace(p, pattern, safeReplacement, Options));
            RebuildAll();
            summaryText.Text = string.Format(CultureInfo.CurrentUICulture, Res.ReplacedCount, changed);
        }

        // ---------- favorites ----------

        private void SetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!suppressSetLoad && setCombo.SelectedItem is LinkSet set)
            {
                LoadSet(set);
            }
        }

        private void LoadSet(LinkSet set)
        {
            leftPane.Paths = new List<string>(set.Destinations);
            rightPane.Paths = new List<string>(set.Sources);
            pairs = (set.Pairs ?? new List<LinkPair>())
                .Select(pair => new LinkPair { Destination = pair.Destination, Source = pair.Source })
                .ToList();
            pairingCombo.SelectedValue = set.Pairing;
            modeCombo.SelectedValue = set.Mode;
            policyCombo.SelectedValue = set.Policy;
            summaryText.Text = string.Empty;
            UpdatePairingUi();
            RebuildAll();
        }

        private LinkSet BuildSet(string name) => new()
        {
            Name = name,
            Destinations = new List<string>(leftPane.Paths),
            Sources = new List<string>(rightPane.Paths),
            Mode = CurrentMode,
            Pairing = CurrentPairing,
            Pairs = pairs.Select(pair => new LinkPair { Destination = pair.Destination, Source = pair.Source }).ToList(),
            Policy = CurrentPolicy,
        };

        private void SaveSet_Click(object sender, RoutedEventArgs e)
        {
            var name = (setCombo.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                Info(Res.NeedFavoriteName);
                return;
            }

            var set = BuildSet(name);
            var existing = sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));

            suppressSetLoad = true;
            try
            {
                if (existing != null)
                {
                    sets[sets.IndexOf(existing)] = set;
                }
                else
                {
                    sets.Add(set);
                }

                setCombo.SelectedItem = set;
            }
            finally
            {
                suppressSetLoad = false;
            }

            PersistSets();
        }

        private void DeleteSet_Click(object sender, RoutedEventArgs e)
        {
            var name = (setCombo.Text ?? string.Empty).Trim();
            var set = setCombo.SelectedItem as LinkSet
                ?? sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (set == null)
            {
                return;
            }

            if (!Confirm(string.Format(CultureInfo.CurrentUICulture, Res.ConfirmDeleteFavorite, set.Name)))
            {
                return;
            }

            suppressSetLoad = true;
            try
            {
                sets.Remove(set);
                setCombo.SelectedItem = null;
                setCombo.Text = string.Empty;
            }
            finally
            {
                suppressSetLoad = false;
            }

            PersistSets();
        }

        private void PersistSets()
        {
            try
            {
                LinkSetStore.Save(sets.ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Info(string.Format(CultureInfo.CurrentUICulture, Res.SaveFavoriteFailed, ex.Message));
            }
        }

        // ---------- run ----------

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            if (rows.Count == 0)
            {
                Info(IsManual ? Res.NothingToRunManual : Res.NothingToRunEmpty);
                return;
            }

            if (!rows.Any(r => r.Entry.IsRunnable))
            {
                Info(Res.NoRunnable);
                return;
            }

            var policy = CurrentPolicy;
            if (policy == ConflictPolicy.Overwrite)
            {
                if (!Confirm(Res.ConfirmOverwrite))
                {
                    return;
                }
            }

            foreach (var row in rows)
            {
                row.ResultText = string.Empty;
            }

            int created = 0, skipped = 0, failed = 0;
            var needPrivilege = false;
            contentGrid.IsEnabled = false;
            try
            {
                foreach (var row in rows)
                {
                    var entry = row.Entry;
                    var result = await Task.Run(() => LinkPlanner.Run(entry, policy));
                    row.ResultText = Describe(result, entry);

                    if (result.IsCreated)
                    {
                        created++;
                    }
                    else if (result.IsSkipped)
                    {
                        skipped++;
                    }
                    else
                    {
                        failed++;
                        needPrivilege |= result.Outcome == LinkOutcome.NeedPrivilege;
                    }
                }
            }
            finally
            {
                contentGrid.IsEnabled = true;
            }

            var summary = string.Format(CultureInfo.CurrentUICulture, Res.RunSummary, created, skipped, failed);
            if (needPrivilege)
            {
                summary += Res.NeedPrivilegeHint;
            }

            summaryText.Text = summary;
        }

        internal static string DescribeStatus(PlanStatus status) => status switch
        {
            PlanStatus.Ready => Res.StatusReady,
            PlanStatus.AlreadyLinked => Res.StatusAlreadyLinked,
            PlanStatus.Exists => Res.StatusExists,
            PlanStatus.SourceMissing => Res.StatusSourceMissing,
            PlanStatus.DestinationMissing => Res.StatusDestinationMissing,
            PlanStatus.NameConflict => Res.StatusNameConflict,
            PlanStatus.Overlap => Res.StatusOverlap,
            PlanStatus.HardLinkCrossVolume => Res.StatusHardLinkCrossVolume,
            _ => Res.StatusInvalidPath,
        };

        private static string Describe(LinkResult result, PlanEntry entry) => result.Outcome switch
        {
            LinkOutcome.Created => Res.ResCreated,
            LinkOutcome.CreatedAfterBackup => string.Format(CultureInfo.CurrentUICulture, Res.ResCreatedBackup, Path.GetFileName(result.Detail)),
            LinkOutcome.CreatedAfterOverwrite => Res.ResCreatedOverwrite,
            LinkOutcome.SkippedExists => Res.ResSkippedExists,
            LinkOutcome.SkippedAlreadyLinked => Res.ResSkippedSame,
            LinkOutcome.TargetMissing => Res.ResTargetMissing,
            LinkOutcome.InvalidKind => Res.ResInvalidKind,
            LinkOutcome.PathsOverlap => Res.ResOverlap,
            LinkOutcome.RefusedNonEmptyDirectory => Res.ResRefusedDirectory,
            LinkOutcome.NeedPrivilege => Res.ResNeedPrivilege,
            LinkOutcome.Blocked => string.Format(CultureInfo.CurrentUICulture, Res.ResBlocked, DescribeStatus(entry.Status)),
            _ => string.Format(CultureInfo.CurrentUICulture, Res.ResFailed, result.Detail),
        };
    }
}
