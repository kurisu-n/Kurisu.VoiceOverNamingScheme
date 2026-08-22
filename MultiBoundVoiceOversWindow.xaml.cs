using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Articy.Api;

namespace Kurisu.VoiceOverTools
{
    public partial class MultiBoundVoiceOversWindow : Window
    {
        public sealed class AssetGroupRow
        {
            public int GroupIndex { get; set; }
            public string AssetDisplayName { get; set; }
            public string FileName { get; set; }
            public string AbsolutePath { get; set; }
            public string FileSizeText { get; set; }
            public int BindingCount { get; set; }
            public ObjectProxy AssetTag { get; set; }
            public string BindingCountLabel => $"{BindingCount} bindings";
        }

        public sealed class BindingRow
        {
            public int GroupIndex { get; set; }
            public int BindingIndex { get; set; }
            public string Culture { get; set; }
            public string LocalIdHex { get; set; }
            public string SpeakerName { get; set; }
            public string FragmentPath { get; set; }
            public string PropertyLabel { get; set; }
            public string LineText { get; set; }
            public object Tag { get; set; }
        }

        private sealed class RowTemplateSelector : DataTemplateSelector
        {
            public DataTemplate AssetTemplate { get; set; }
            public DataTemplate BindingTemplate { get; set; }
            public override DataTemplate SelectTemplate(object item, DependencyObject container) => item switch
            {
                AssetGroupRow => AssetTemplate,
                BindingRow => BindingTemplate,
                _ => null
            };
        }

        private const string AnyLanguageOption = "(any language)";
        private const string AllCharactersOption = "(all characters)";
        private const string AllPathsOption = "(all paths)";

        private Action<Plugin.MultiBoundScanOptions> _rebuild;
        private Action<object> _clearOne;
        private Action<object> _clearOthers;
        private Action<ObjectProxy> _navigateFragment;
        private Action<ObjectProxy> _navigateAsset;

        private List<object> _allRows = new();
        private string _selectedSpeaker = AllCharactersOption;
        private string _selectedPath = AllPathsOption;
        private string _selectedLanguage = AnyLanguageOption;
        private bool _suspendEvents;

        // Single shared MediaPlayer; Stop/Open/Play on each request to avoid overlap.
        private readonly MediaPlayer _mediaPlayer = new();

        public MultiBoundVoiceOversWindow()
        {
            InitializeComponent();

            ResultsListBox.ItemTemplateSelector = new RowTemplateSelector
            {
                AssetTemplate = (DataTemplate)Resources["AssetGroupTemplate"],
                BindingTemplate = (DataTemplate)Resources["BindingRowTemplate"]
            };

            _mediaPlayer.MediaEnded += (_, _) => StopPlaybackButton.IsEnabled = false;
            _mediaPlayer.MediaFailed += (_, _) => StopPlaybackButton.IsEnabled = false;

            Closed += (_, _) => { try { _mediaPlayer.Stop(); _mediaPlayer.Close(); } catch { /* shutdown */ } };

            Loaded += (_, _) => Activate();
        }

        public void SetSession(
            Action<Plugin.MultiBoundScanOptions> rebuild,
            Action<object> clearOne,
            Action<object> clearOthers,
            Action<ObjectProxy> navigateFragment,
            Action<ObjectProxy> navigateAsset)
        {
            _rebuild = rebuild;
            _clearOne = clearOne;
            _clearOthers = clearOthers;
            _navigateFragment = navigateFragment;
            _navigateAsset = navigateAsset;
        }

        public void Populate(
            string scopeSummary,
            IReadOnlyList<object> rows,
            int groupCount,
            int bindingCount,
            IReadOnlyList<string> speakers,
            IReadOnlyList<string> pathPrefixes,
            IReadOnlyList<string> languages)
        {
            _suspendEvents = true;
            try
            {
                ScopeTextBlock.Text = scopeSummary;
                _allRows = new List<object>(rows);

                var prevSpeaker = _selectedSpeaker;
                var characters = new List<string> { AllCharactersOption };
                characters.AddRange(speakers);
                CharacterFilterCombo.ItemsSource = characters;
                CharacterFilterCombo.SelectedItem = characters.Contains(prevSpeaker) ? prevSpeaker : AllCharactersOption;
                _selectedSpeaker = (string)CharacterFilterCombo.SelectedItem;

                var prevLang = _selectedLanguage;
                var langs = new List<string> { AnyLanguageOption };
                langs.AddRange(languages);
                LanguageFilterCombo.ItemsSource = langs;
                LanguageFilterCombo.SelectedItem = langs.Contains(prevLang) ? prevLang : AnyLanguageOption;
                _selectedLanguage = (string)LanguageFilterCombo.SelectedItem;

                BuildPathContextMenu(pathPrefixes);
                if (!pathPrefixes.Contains(_selectedPath) && _selectedPath != AllPathsOption)
                {
                    _selectedPath = AllPathsOption;
                    PathDropdownLabel.Text = AllPathsOption;
                }
            }
            finally
            {
                _suspendEvents = false;
            }

            ApplyViewFilter();
        }

        // ─── Detection-side ────────────────────────────────────────────────────────────────

        private void DetectionSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendEvents || _rebuild == null) return;
            TriggerRebuild();
        }

        private void TriggerRebuild()
        {
            _rebuild(new Plugin.MultiBoundScanOptions
            {
                IncludeIntraFragment = IncludeIntraFragmentCheckBox.IsChecked == true
            });
        }

        // ─── View-side ─────────────────────────────────────────────────────────────────────

        private void ViewFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendEvents) return;
            if (CharacterFilterCombo.SelectedItem is string c) _selectedSpeaker = c;
            if (LanguageFilterCombo.SelectedItem is string l) _selectedLanguage = l;
            ApplyViewFilter();
        }

        private void ApplyViewFilter()
        {
            var bindingPredicate = (Func<BindingRow, bool>)(b =>
            {
                if (_selectedSpeaker != AllCharactersOption &&
                    !string.Equals(b.SpeakerName, _selectedSpeaker, StringComparison.Ordinal))
                    return false;
                if (_selectedLanguage != AnyLanguageOption &&
                    !string.Equals(b.Culture, _selectedLanguage, StringComparison.Ordinal))
                    return false;
                if (_selectedPath != AllPathsOption)
                {
                    var prefixWithSep = _selectedPath + " / ";
                    if (string.IsNullOrEmpty(b.FragmentPath)) return false;
                    if (b.FragmentPath != _selectedPath && !b.FragmentPath.StartsWith(prefixWithSep, StringComparison.Ordinal))
                        return false;
                }
                return true;
            });

            var byGroup = new Dictionary<int, List<BindingRow>>();
            foreach (var b in _allRows.OfType<BindingRow>())
            {
                if (!bindingPredicate(b)) continue;
                if (!byGroup.TryGetValue(b.GroupIndex, out var list))
                    byGroup[b.GroupIndex] = list = new List<BindingRow>();
                list.Add(b);
            }

            var output = new List<object>();
            int visibleGroups = 0;
            int visibleBindings = 0;
            foreach (var row in _allRows)
            {
                if (row is AssetGroupRow gr)
                {
                    if (!byGroup.TryGetValue(gr.GroupIndex, out var members)) continue;
                    if (members.Count < 2) continue;
                    output.Add(gr);
                    output.AddRange(members);
                    visibleGroups++;
                    visibleBindings += members.Count;
                }
            }

            ResultsListBox.ItemsSource = output;
            StatusTextBlock.Text = $"Showing {visibleGroups} asset(s), {visibleBindings} binding(s).";
        }

        // ─── Action handlers ───────────────────────────────────────────────────────────────

        private void BindingClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not BindingRow row) return;
            _clearOne?.Invoke(row.Tag);
            TriggerRebuild();
        }

        private void BindingClearOthersButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not BindingRow row) return;

            int otherCount = (ResultsListBox.ItemsSource as IEnumerable<object>)?
                .OfType<BindingRow>()
                .Count(r => r.GroupIndex == row.GroupIndex && r.BindingIndex != row.BindingIndex) ?? 0;

            var result = MessageBox.Show(
                $"Clear the VO binding from {otherCount} other fragment binding(s) of this asset, " +
                $"keeping the one for {row.SpeakerName} ({row.LocalIdHex} [{row.Culture}])?",
                "Confirm bulk clear",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            _clearOthers?.Invoke(row.Tag);
            TriggerRebuild();
        }

        // ─── Playback ──────────────────────────────────────────────────────────────────────

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not AssetGroupRow row) return;
            var path = row.AbsolutePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show($"Audio file not available on disk:\n{path}",
                    "Cannot play", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Open(new Uri(path, UriKind.Absolute));
                _mediaPlayer.Play();
                StopPlaybackButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Playback failed:\n{ex.Message}",
                    "Playback error", MessageBoxButton.OK, MessageBoxImage.Warning);
                StopPlaybackButton.IsEnabled = false;
            }
        }

        private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
        {
            try { _mediaPlayer.Stop(); } catch { /* ignore */ }
            StopPlaybackButton.IsEnabled = false;
        }

        // ─── Selection / navigation ────────────────────────────────────────────────────────

        private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowFragmentButton.IsEnabled = ResultsListBox.SelectedItem is BindingRow && _navigateFragment != null;
        }

        private void ShowFragmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsListBox.SelectedItem is not BindingRow row) return;
            if (row.Tag is not ValueTuple<Plugin.MultiBoundAssetGroup, Plugin.MultiBoundBinding> tuple) return;
            _navigateFragment?.Invoke(tuple.Item2.Fragment);
        }

        private void ShowAssetButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not AssetGroupRow row) return;
            _navigateAsset?.Invoke(row.AssetTag);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ─── Path picker ───────────────────────────────────────────────────────────────────

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
