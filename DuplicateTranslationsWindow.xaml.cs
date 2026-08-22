using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Articy.Api;

namespace Kurisu.VoiceOverTools
{
    public partial class DuplicateTranslationsWindow : Window
    {
        // Row models — discriminated union, rendered via RowTemplateSelector.

        public sealed class GroupRow
        {
            public int GroupIndex { get; set; }
            public string LanguageCode { get; set; }
            public int MemberCount { get; set; }
            public string SampleText { get; set; }
            public bool AllShareReferenceText { get; set; }
            public string MemberCountLabel => $"{MemberCount} fragments share this translation";
        }

        public sealed class MemberRow
        {
            public int GroupIndex { get; set; }
            public int MemberIndex { get; set; }
            public string LanguageCode { get; set; }
            public string LocalIdHex { get; set; }
            public string SpeakerName { get; set; }
            public string FragmentPath { get; set; }
            public string PropertyLabel { get; set; }
            public string ReferenceText { get; set; }
            public string OffendingText { get; set; }
            // Internal pointer to the (group, member) plugin-side records — passed back to action callbacks.
            public object Tag { get; set; }
        }

        private sealed class RowTemplateSelector : DataTemplateSelector
        {
            public DataTemplate GroupTemplate { get; set; }
            public DataTemplate MemberTemplate { get; set; }
            public override DataTemplate SelectTemplate(object item, DependencyObject container)
            {
                return item switch
                {
                    GroupRow => GroupTemplate,
                    MemberRow => MemberTemplate,
                    _ => null
                };
            }
        }

        private const string AnyLanguageOption = "(any language)";
        private const string AllCharactersOption = "(all characters)";
        private const string AllPathsOption = "(all paths)";

        private Action<Plugin.DuplicateScanOptions> _rebuild;
        private Action<object> _clearOne;
        private Action<object> _clearOthers;
        private Func<IReadOnlyList<object>, int> _clearAllFiltered;
        private Action<ObjectProxy> _navigateFragment;

        private List<object> _allRows = new();   // GroupRow + MemberRow interleaved
        private string _selectedSpeaker = AllCharactersOption;
        private string _selectedPath = AllPathsOption;
        private string _selectedOffendingLang = AnyLanguageOption;

        // Suppress filter/rescan side effects during ItemsSource population.
        private bool _suspendEvents;

        public DuplicateTranslationsWindow()
        {
            InitializeComponent();

            ResultsListBox.ItemTemplateSelector = new RowTemplateSelector
            {
                GroupTemplate = (DataTemplate)Resources["GroupRowTemplate"],
                MemberTemplate = (DataTemplate)Resources["MemberRowTemplate"]
            };

            Loaded += (_, _) => Activate();
        }

        public void SetSession(
            IReadOnlyList<string> textLanguages,
            string defaultReferenceLanguage,
            Action<Plugin.DuplicateScanOptions> rebuild,
            Action<object> clearOne,
            Action<object> clearOthers,
            Func<IReadOnlyList<object>, int> clearAllFiltered,
            Action<ObjectProxy> navigateFragment)
        {
            _rebuild = rebuild;
            _clearOne = clearOne;
            _clearOthers = clearOthers;
            _clearAllFiltered = clearAllFiltered;
            _navigateFragment = navigateFragment;

            _suspendEvents = true;
            try
            {
                ReferenceLanguageCombo.ItemsSource = textLanguages;
                ReferenceLanguageCombo.SelectedItem = defaultReferenceLanguage;

                var offending = new List<string> { AnyLanguageOption };
                offending.AddRange(textLanguages.Where(l => !string.Equals(l, defaultReferenceLanguage, StringComparison.OrdinalIgnoreCase)));
                OffendingLanguageCombo.ItemsSource = offending;
                OffendingLanguageCombo.SelectedIndex = 0;
            }
            finally
            {
                _suspendEvents = false;
            }
        }

        public void Populate(
            string scopeSummary,
            IReadOnlyList<object> rows,
            int groupCount,
            int memberCount,
            IReadOnlyList<string> speakers,
            IReadOnlyList<string> pathPrefixes,
            IReadOnlyList<string> textLanguages)
        {
            _suspendEvents = true;
            try
            {
                ScopeTextBlock.Text = scopeSummary;
                _allRows = new List<object>(rows);

                // Refresh character + path filters every rescan (their domains shrink as data is cleared).
                var prevSpeaker = _selectedSpeaker;
                var characters = new List<string> { AllCharactersOption };
                characters.AddRange(speakers);
                CharacterFilterCombo.ItemsSource = characters;
                CharacterFilterCombo.SelectedItem = characters.Contains(prevSpeaker) ? prevSpeaker : AllCharactersOption;
                _selectedSpeaker = (string)CharacterFilterCombo.SelectedItem;

                BuildPathContextMenu(pathPrefixes);
                if (!pathPrefixes.Contains(_selectedPath) && _selectedPath != AllPathsOption)
                {
                    _selectedPath = AllPathsOption;
                    PathDropdownLabel.Text = AllPathsOption;
                }

                // Sync offending-lang options against current reference (exclude the ref language).
                var refLang = ReferenceLanguageCombo.SelectedItem as string;
                var prevOff = _selectedOffendingLang;
                var offending = new List<string> { AnyLanguageOption };
                offending.AddRange(textLanguages.Where(l => !string.Equals(l, refLang, StringComparison.OrdinalIgnoreCase)));
                OffendingLanguageCombo.ItemsSource = offending;
                OffendingLanguageCombo.SelectedItem = offending.Contains(prevOff) ? prevOff : AnyLanguageOption;
                _selectedOffendingLang = (string)OffendingLanguageCombo.SelectedItem;
            }
            finally
            {
                _suspendEvents = false;
            }

            ApplyViewFilter();
        }

        // ─── Detection-side settings (trigger a full rescan) ───────────────────────────────

        private void DetectionSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendEvents || _rebuild == null) return;
            TriggerRebuild();
        }

        private void TriggerRebuild()
        {
            var refLang = ReferenceLanguageCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(refLang)) return;
            _rebuild(new Plugin.DuplicateScanOptions
            {
                ReferenceLanguage = refLang,
                CaseInsensitive = CaseInsensitiveCheckBox.IsChecked == true,
                CollapseWhitespace = CollapseWhitespaceCheckBox.IsChecked == true,
                IncludeRefIdenticalGroups = IncludeRefIdenticalCheckBox.IsChecked == true
            });
        }

        // ─── View-side filters (no rescan) ─────────────────────────────────────────────────

        private void ViewFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendEvents) return;
            if (CharacterFilterCombo.SelectedItem is string c) _selectedSpeaker = c;
            if (OffendingLanguageCombo.SelectedItem is string l) _selectedOffendingLang = l;
            ApplyViewFilter();
        }

        private void ApplyViewFilter()
        {
            // Filter members, then keep only group headers whose group still has visible members.
            var memberPredicate = (Func<MemberRow, bool>)(m =>
            {
                if (_selectedSpeaker != AllCharactersOption &&
                    !string.Equals(m.SpeakerName, _selectedSpeaker, StringComparison.Ordinal))
                    return false;

                if (_selectedOffendingLang != AnyLanguageOption &&
                    !string.Equals(m.LanguageCode, _selectedOffendingLang, StringComparison.Ordinal))
                    return false;

                if (_selectedPath != AllPathsOption)
                {
                    var prefixWithSep = _selectedPath + " / ";
                    if (string.IsNullOrEmpty(m.FragmentPath)) return false;
                    if (m.FragmentPath != _selectedPath && !m.FragmentPath.StartsWith(prefixWithSep, StringComparison.Ordinal))
                        return false;
                }
                return true;
            });

            // Two-pass: collect surviving member rows by GroupIndex; then keep group headers with >=2 surviving members.
            var byGroup = new Dictionary<int, List<MemberRow>>();
            foreach (var row in _allRows.OfType<MemberRow>())
            {
                if (!memberPredicate(row)) continue;
                if (!byGroup.TryGetValue(row.GroupIndex, out var list))
                    byGroup[row.GroupIndex] = list = new List<MemberRow>();
                list.Add(row);
            }

            var output = new List<object>();
            int visibleGroups = 0;
            int visibleMembers = 0;
            foreach (var row in _allRows)
            {
                if (row is GroupRow gr)
                {
                    if (!byGroup.TryGetValue(gr.GroupIndex, out var members)) continue;
                    if (members.Count < 2) continue; // a group with 1 surviving member is no longer a duplicate
                    output.Add(gr);
                    output.AddRange(members);
                    visibleGroups++;
                    visibleMembers += members.Count;
                }
            }

            ResultsListBox.ItemsSource = output;
            StatusTextBlock.Text = $"Showing {visibleGroups} group(s), {visibleMembers} fragment line(s).";
            ClearAllFilteredButton.IsEnabled = visibleMembers > 0;
        }

        // ─── Action handlers ───────────────────────────────────────────────────────────────

        private void MemberClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not MemberRow row) return;
            _clearOne?.Invoke(row.Tag);
            TriggerRebuild();
        }

        private void MemberClearOthersButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not MemberRow row) return;

            // Confirm — this can clear many entries at once.
            var visibleGroup = ResultsListBox.ItemsSource as IEnumerable<object>;
            int otherCount = visibleGroup?
                .OfType<MemberRow>()
                .Count(r => r.GroupIndex == row.GroupIndex && r.MemberIndex != row.MemberIndex) ?? 0;

            var result = MessageBox.Show(
                $"Clear the offending {row.LanguageCode} translation from {otherCount} other fragment(s) in this group, " +
                $"keeping the one for {row.SpeakerName} ({row.LocalIdHex})?",
                "Confirm bulk clear",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            _clearOthers?.Invoke(row.Tag);
            TriggerRebuild();
        }

        private void ClearAllFilteredButton_Click(object sender, RoutedEventArgs e)
        {
            var visible = (ResultsListBox.ItemsSource as IEnumerable<object>)?.OfType<MemberRow>().ToList()
                          ?? new List<MemberRow>();
            if (visible.Count == 0) return;

            var byLang = visible.GroupBy(r => r.LanguageCode)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"  • {g.Key}: {g.Count()}");
            var breakdown = string.Join("\n", byLang);

            var result = MessageBox.Show(
                $"Clear the offending translation in every visible row?\n\n" +
                $"{visible.Count} entry(ies) total:\n{breakdown}\n\n" +
                $"Reference-language texts are not touched. This cannot be undone via this tool " +
                $"(use Articy's Undo for that, before closing the project).",
                "Confirm clear-all",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            var tags = visible.Select(r => r.Tag).ToList();
            _clearAllFiltered?.Invoke(tags);
            TriggerRebuild();
        }

        // ─── Selection / navigation ────────────────────────────────────────────────────────

        private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowFragmentButton.IsEnabled = ResultsListBox.SelectedItem is MemberRow && _navigateFragment != null;
        }

        private void ShowFragmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsListBox.SelectedItem is not MemberRow row) return;
            if (row.Tag is not ValueTuple<Plugin.DuplicateGroup, Plugin.DuplicateMember> tuple) return;
            _navigateFragment?.Invoke(tuple.Item2.Fragment);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ─── Path picker (hierarchical ContextMenu) — same pattern as VoiceOverAuditWindow ─

        private sealed class PathNode
        {
            public string Segment;
            public string FullPath;
            public List<PathNode> Children = new();
        }

        private void BuildPathContextMenu(IReadOnlyList<string> pathPrefixes)
        {
            var menu = new ContextMenu();
            foreach (var dict in this.Resources.MergedDictionaries)
                menu.Resources.MergedDictionaries.Add(dict);
            ApplyThemedTemplate(menu);

            var allItem = new MenuItem { Header = AllPathsOption };
            ApplyThemedTemplate(allItem);
            allItem.Click += (_, _) => SelectPath(AllPathsOption);
            menu.Items.Add(allItem);

            var sep = new Separator();
            if (TryFindResource(typeof(Separator)) is Style sepStyle) sep.Style = sepStyle;
            menu.Items.Add(sep);

            var root = new PathNode();
            if (pathPrefixes != null)
            {
                foreach (var prefix in pathPrefixes)
                {
                    if (string.IsNullOrEmpty(prefix)) continue;
                    var segs = prefix.Split(new[] { " / " }, StringSplitOptions.None);
                    var current = root;
                    var built = "";
                    for (int i = 0; i < segs.Length; i++)
                    {
                        built = i == 0 ? segs[0] : built + " / " + segs[i];
                        var existing = current.Children.FirstOrDefault(c => c.Segment == segs[i]);
                        if (existing == null)
                        {
                            existing = new PathNode { Segment = segs[i], FullPath = built };
                            current.Children.Add(existing);
                        }
                        current = existing;
                    }
                }
            }
            SortTree(root);
            foreach (var node in root.Children)
                menu.Items.Add(BuildMenuItem(node));

            PathDropdownButton.ContextMenu = menu;
        }

        private void ApplyThemedTemplate(ContextMenu menu)
        {
            menu.Background = (Brush)FindResource("DarkBgBrush");
            menu.Foreground = (Brush)FindResource("TextPrimaryBrush");
            menu.BorderBrush = (Brush)FindResource("BorderDarkBrush");
            menu.BorderThickness = new Thickness(1);
            menu.HasDropShadow = false;
            if (TryFindResource("ThemedContextMenuControlTemplate") is ControlTemplate tpl) menu.Template = tpl;
        }

        private void ApplyThemedTemplate(MenuItem item)
        {
            item.Background = Brushes.Transparent;
            item.Foreground = (Brush)FindResource("TextPrimaryBrush");
            item.BorderBrush = Brushes.Transparent;
            item.Padding = new Thickness(10, 5, 10, 5);
            if (TryFindResource("ThemedMenuItemControlTemplate") is ControlTemplate tpl) item.Template = tpl;
        }

        private static void SortTree(PathNode node)
        {
            node.Children.Sort((a, b) => StringComparer.Ordinal.Compare(a.Segment, b.Segment));
            foreach (var c in node.Children) SortTree(c);
        }

        private MenuItem BuildMenuItem(PathNode node)
        {
            var item = new MenuItem { Header = node.Segment };
            ApplyThemedTemplate(item);
            if (node.Children.Count > 0)
            {
                var selfItem = new MenuItem { Header = "↩ " + node.Segment + " (this folder)" };
                ApplyThemedTemplate(selfItem);
                selfItem.Click += (_, _) => SelectPath(node.FullPath);
                item.Items.Add(selfItem);

                var sep = new Separator();
                if (TryFindResource(typeof(Separator)) is Style sepStyle) sep.Style = sepStyle;
                item.Items.Add(sep);

                foreach (var child in node.Children)
                    item.Items.Add(BuildMenuItem(child));
            }
            else
            {
                item.Click += (_, _) => SelectPath(node.FullPath);
            }
            return item;
        }

        private void SelectPath(string path)
        {
            _selectedPath = path;
            PathDropdownLabel.Text = string.IsNullOrEmpty(path) ? AllPathsOption : path;
            ApplyViewFilter();
        }

        private void PathDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            if (PathDropdownButton.ContextMenu == null) return;
            PathDropdownButton.ContextMenu.PlacementTarget = PathDropdownButton;
            PathDropdownButton.ContextMenu.Placement = PlacementMode.Bottom;
            PathDropdownButton.ContextMenu.MinWidth = PathDropdownButton.ActualWidth;
            PathDropdownButton.ContextMenu.IsOpen = true;
        }
    }
}
