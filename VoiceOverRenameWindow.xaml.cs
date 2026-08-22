using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Kurisu.VoiceOverTools
{
    public partial class VoiceOverRenameWindow : Window
    {
        public enum ActionChoice
        {
            DryRun,
            Execute
        }

        public sealed class Row
        {
            public string Icon { get; set; }
            public Brush IconBrush { get; set; }
            public string Header { get; set; }
            public string FileLine { get; set; }
            public bool HasFileLine { get; set; }
            public string DisplayLine { get; set; }
            public bool HasDisplayLine { get; set; }
            public int CategoryKey { get; set; }
            // Informational: this entry's VO asset is bound to >1 distinct DialogueFragment.
            // The XAML data-trigger uses this to tint the row background; the rename pass
            // itself doesn't behave any differently — the user gets a separate confirmation
            // dialog before any actionable multi-bound entries are processed.
            public bool IsMultiBound { get; set; }
        }

        public ActionChoice SelectedAction { get; private set; } = ActionChoice.DryRun;
        public bool Confirmed { get; private set; }

        private Action<int> _onNavigateFragment;
        private Action<int> _onNavigateAsset;

        private IReadOnlyList<Row> _allRows = new List<Row>();
        // AlreadyCorrect (0) is off by default — most entries usually fall here after a
        // successful rename pass, and users want to see what's actionable.
        private readonly HashSet<int> _visibleCategories = new() { 1, 2, 3, 4 };
        // Multi-bound rows are visible by default; toggling the pill off hides them.
        private bool _includeMultiBound = true;
        private int _totalCount;

        public VoiceOverRenameWindow()
        {
            InitializeComponent();

            // Articy's modal ShowDialog sometimes opens without giving us keyboard/mouse focus,
            // which made the first click on an interactive element (e.g. "Execute rename") appear
            // to do nothing. Activating on Loaded removes the ghost first-click.
            Loaded += (_, _) =>
            {
                Activate();
                MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
            };
        }

        public void Populate(
            string scopeSummary,
            IReadOnlyList<Row> rows,
            int alreadyCorrect,
            int willRename,
            int displayOnly,
            int targetExists,
            int sourceMissing,
            int multiBound,
            Action<int> onNavigateFragment,
            Action<int> onNavigateAsset)
        {
            _onNavigateFragment = onNavigateFragment;
            _onNavigateAsset = onNavigateAsset;

            _allRows = rows;
            _totalCount = rows.Count;

            ScopeTextBlock.Text = scopeSummary;
            AlreadyCorrectCount.Text = alreadyCorrect.ToString();
            WillRenameCount.Text = willRename.ToString();
            DisplayOnlyCount.Text = displayOnly.ToString();
            TargetExistsCount.Text = targetExists.ToString();
            SourceMissingCount.Text = sourceMissing.ToString();
            MultiBoundCount.Text = multiBound.ToString();

            ApplyFilter();

            // ExecuteRadio stays enabled even when no rows are actionable: with the dark theme's
            // subtle disabled state it just looked like clicks weren't registering. Running an
            // Execute pass on a zero-actionable plan is harmless — it produces a report of zeros.
            RunButton.IsEnabled = rows.Count > 0;
        }

        private void CategoryFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb) return;
            var tag = tb.Tag as string;
            if (!int.TryParse(tag, out var key)) return;

            if (tb.IsChecked == true) _visibleCategories.Add(key);
            else _visibleCategories.Remove(key);

            ApplyFilter();
        }

        private void MultiBoundFilter_Click(object sender, RoutedEventArgs e)
        {
            _includeMultiBound = MultiBoundToggle.IsChecked == true;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            IEnumerable<Row> filteredEnum = _allRows.Where(r => _visibleCategories.Contains(r.CategoryKey));
            if (!_includeMultiBound) filteredEnum = filteredEnum.Where(r => !r.IsMultiBound);
            var filtered = filteredEnum.ToList();
            PlanListBox.ItemsSource = filtered;

            if (filtered.Count == _totalCount)
                FilterStatusTextBlock.Text = "Click a category to toggle it on/off.";
            else
                FilterStatusTextBlock.Text = $"Showing {filtered.Count} of {_totalCount}.  Click a category to toggle.";
        }

        private void PlanListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var hasSelection = PlanListBox.SelectedIndex >= 0;
            ShowFragmentButton.IsEnabled = hasSelection && _onNavigateFragment != null;
            ShowAssetButton.IsEnabled = hasSelection && _onNavigateAsset != null;
        }

        private void ShowFragmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlanListBox.SelectedItem is not Row row || _onNavigateFragment == null) return;
            var idx = _allRows.ToList().IndexOf(row);
            if (idx < 0) return;
            _onNavigateFragment(idx);
            // Intentionally stay open so the user can navigate multiple entries without re-scanning.
        }

        private void ShowAssetButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlanListBox.SelectedItem is not Row row || _onNavigateAsset == null) return;
            var idx = _allRows.ToList().IndexOf(row);
            if (idx < 0) return;
            _onNavigateAsset(idx);
            // Intentionally stay open — see above.
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = ExecuteRadio.IsChecked == true ? ActionChoice.Execute : ActionChoice.DryRun;
            Confirmed = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }
    }
}
