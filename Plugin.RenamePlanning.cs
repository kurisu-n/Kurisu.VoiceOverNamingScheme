using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Articy.Api;
using Articy.Api.Plugins;
using Articy.ModelFramework;

namespace Kurisu.VoiceOverTools
{
    public partial class Plugin
    {
        private enum RenameCategory
        {
            AlreadyCorrect,
            WillRename,
            DisplayOnly,
            TargetExists,
            SourceMissing
        }

        private sealed class RenamePlanEntry
        {
            public ObjectProxy Fragment;
            public ObjectProxy Asset;
            public string LanguageCode;
            public string LocalIdHex;
            public string CurrentFileName;
            public string PlannedFileName;
            public string CurrentDisplayName;
            public string PlannedDisplayName;
            public string SourcePath;
            public RenameCategory Category;
            public bool IsMultiBound;
        }

        private void RunRenamePlanner(IReadOnlyList<ObjectProxy> fragments, string scopeLabel)
        {
            if (fragments == null || fragments.Count == 0)
            {
                var empty = new MessageWindow { Title = "Voice-Over Rename" };
                empty.MessageTextBlock.Text = "No DialogueFragments found in the selected scope.";
                Session.ShowDialog(empty);
                return;
            }

            var plan = BuildRenamePlan(fragments);

            int alreadyCorrect = plan.Count(p => p.Category == RenameCategory.AlreadyCorrect);
            int willRename     = plan.Count(p => p.Category == RenameCategory.WillRename);
            int displayOnly    = plan.Count(p => p.Category == RenameCategory.DisplayOnly);
            int targetExists   = plan.Count(p => p.Category == RenameCategory.TargetExists);
            int sourceMissing  = plan.Count(p => p.Category == RenameCategory.SourceMissing);
            int multiBound     = plan.Count(p => p.IsMultiBound);

            var rows = plan.Select(BuildRow).ToList();

            var window = new VoiceOverRenameWindow();
            var scopeSummary =
                $"Scope: {scopeLabel}  |  " +
                $"{fragments.Count} DialogueFragment(s) scanned  |  " +
                $"{plan.Count} voice-over reference(s) found";

            window.Populate(
                scopeSummary,
                rows,
                alreadyCorrect, willRename, displayOnly, targetExists, sourceMissing, multiBound,
                onNavigateFragment: idx => NavigateToObject(plan[idx].Fragment),
                onNavigateAsset:    idx => NavigateToObject(plan[idx].Asset));

            Session.ShowDialog(window);

            if (!window.Confirmed) return;

            if (window.SelectedAction == VoiceOverRenameWindow.ActionChoice.DryRun)
            {
                ShowReport("Dry Run Complete",
                    $"Dry run complete. No changes were made.\n\n" +
                    $"Already correct:    {alreadyCorrect}\n" +
                    $"Will rename:        {willRename}\n" +
                    $"Display only:       {displayOnly}\n" +
                    $"Target exists:      {targetExists}\n" +
                    $"Source missing:     {sourceMissing}");
                return;
            }

            // Pre-execute warning: if the actionable plan contains any multi-bound VO assets,
            // surface the failure mode before we mutate anything. The rename pass renames
            // assets in place — when N fragments share an asset, each pass overwrites the
            // file's name + display + ExternalId based on the *processing* fragment, so the
            // last one to be touched wins and every other fragment ends up referencing an
            // asset whose metadata reflects a different fragment than its own.
            int actionableMultiBound = plan.Count(p =>
                p.IsMultiBound &&
                (p.Category == RenameCategory.WillRename || p.Category == RenameCategory.DisplayOnly));

            if (actionableMultiBound > 0)
            {
                if (!ConfirmMultiBoundRename(actionableMultiBound)) return;
            }

            int executedRenames = 0;
            int executedDisplayOnly = 0;
            var touchedFragments = new HashSet<ulong>();

            foreach (var entry in plan)
            {
                if (entry.Category != RenameCategory.WillRename && entry.Category != RenameCategory.DisplayOnly)
                    continue;

                if (entry.Fragment == null || !entry.Fragment.IsValid) continue;

                if (touchedFragments.Add(entry.Fragment.Id))
                {
                    RenameSingleVoiceOver(entry.Fragment);
                }

                if (entry.Category == RenameCategory.WillRename) executedRenames++;
                else executedDisplayOnly++;
            }

            Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());

            ShowReport("Rename Complete",
                $"Rename pass complete.\n\n" +
                $"Fragments touched:   {touchedFragments.Count}\n" +
                $"Full renames:        {executedRenames}\n" +
                $"Display-only:        {executedDisplayOnly}\n" +
                $"Skipped (already correct / target exists / source missing): " +
                $"{alreadyCorrect + targetExists + sourceMissing}");
        }

        private static bool ConfirmMultiBoundRename(int actionableCount)
        {
            var message =
                $"Heads up — {actionableCount} of the planned renames target voice-over " +
                $"assets that are bound to MORE THAN ONE DialogueFragment.\n\n" +
                $"The rename pass renames each asset's file in place and rewrites its display " +
                $"name and External ID per the *processing* fragment. When several fragments " +
                $"share one asset, the last fragment to be processed wins — every other " +
                $"fragment ends up pointing at an asset whose filename, display, and " +
                $"External ID reflect a different fragment. Effectively, this clobbers the " +
                $"naming for every fragment except the last.\n\n" +
                $"Recommended: cancel and run \"Find Multi-bound Voice-Overs\" first to " +
                $"clean up the overlaps (split or unbind), then re-run the rename.\n\n" +
                $"Proceed anyway?";

            var result = System.Windows.MessageBox.Show(
                message,
                "Confirm rename of multi-bound VOs",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            return result == System.Windows.MessageBoxResult.Yes;
        }

        // Computes the set of asset.Id values that are bound to two or more distinct
        // DialogueFragments project-wide (not limited to the rename scope). Multi-binding
        // is a property of the asset's full reference graph — fragments outside the rename
        // selection still share the same on-disk file, and the rename mutates that file.
        private HashSet<ulong> ComputeMultiBoundAssetIds()
        {
            var result = new HashSet<ulong>();
            var voLanguages = Session.GetVoiceOverLanguages();
            if (voLanguages == null || voLanguages.Count == 0) return result;

            var allFragments = CollectAllDialogueFragments();
            var assetFragmentSet = new Dictionary<ulong, HashSet<ulong>>();

            foreach (var fragment in allFragments)
            {
                if (fragment == null || fragment.ObjectType != ObjectType.DialogueFragment) continue;
                foreach (var (_, text) in EnumerateVoiceOverTextProxies(fragment))
                {
                    foreach (var lang in voLanguages)
                    {
                        var culture = lang.CultureName;
                        if (string.IsNullOrEmpty(culture)) continue;
                        var asset = text.VoiceOverReferences[culture];
                        if (asset == null || !asset.IsValid || asset.ObjectType != ObjectType.Asset) continue;
                        if (!assetFragmentSet.TryGetValue(asset.Id, out var fragSet))
                            assetFragmentSet[asset.Id] = fragSet = new HashSet<ulong>();
                        fragSet.Add(fragment.Id);
                    }
                }
            }

            foreach (var kvp in assetFragmentSet)
                if (kvp.Value.Count >= 2) result.Add(kvp.Key);

            return result;
        }

        private List<RenamePlanEntry> BuildRenamePlan(IReadOnlyList<ObjectProxy> fragments)
        {
            var plan = new List<RenamePlanEntry>();

            var voLanguages = Session.GetVoiceOverLanguages();
            if (voLanguages == null || voLanguages.Count == 0) return plan;

            // Project-wide multi-bound asset detection. Done once up front so each plan entry
            // can be tagged in O(1) below.
            var multiBoundAssets = ComputeMultiBoundAssetIds();

            foreach (var fragment in fragments)
            {
                if (fragment == null || fragment.ObjectType != ObjectType.DialogueFragment) continue;

                var localId = DecimalToHex(fragment.Id.ToString());
                var properties = fragment.GetAvailableProperties();
                if (properties == null) continue;

                foreach (var prop in properties)
                {
                    if (prop == "BBCodeText") continue;

                    var info = fragment.GetPropertyInfo(prop);
                    if (info is not { HasVoiceOver: true }) continue;
                    if (fragment[prop] is not TextProxy text) continue;

                    foreach (var lang in voLanguages)
                    {
                        var culture = lang.CultureName;
                        if (string.IsNullOrEmpty(culture)) continue;

                        var asset = text.VoiceOverReferences[culture];
                        if (asset == null || !asset.IsValid || asset.ObjectType != ObjectType.Asset) continue;

                        var srcPath = asset[ObjectPropertyNames.AbsoluteFilePath] as string;
                        var currentFileName = Path.GetFileNameWithoutExtension(srcPath) ?? "";
                        var plannedFileName = $"{localId}_{culture}";

                        var fullDescription = text.Texts[culture];
                        var shortDescription = TruncateText(fullDescription, 3);

                        // Use GetDisplayName() — it returns what Articy actually shows in the UI for this asset,
                        // which may be the filename or a synthesized string when the raw DisplayName property
                        // isn't populated. Reading the raw property directly returned empty values on audio assets.
                        var currentDisplay = asset.GetDisplayName() ?? "";
                        var plannedDisplay = GetAssetDisplayName(fragment, culture, plannedFileName, shortDescription);

                        RenameCategory cat;

                        if (!string.IsNullOrEmpty(currentFileName) && currentFileName.Contains(plannedFileName))
                        {
                            var curT = (currentDisplay ?? "").TrimEnd();
                            var plnT = (plannedDisplay ?? "").TrimEnd();
                            cat = string.Equals(curT, plnT, StringComparison.Ordinal)
                                ? RenameCategory.AlreadyCorrect
                                : RenameCategory.DisplayOnly;
                        }
                        else if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
                        {
                            cat = RenameCategory.SourceMissing;
                        }
                        else if (CopyExists(srcPath, plannedFileName))
                        {
                            cat = RenameCategory.TargetExists;
                        }
                        else
                        {
                            cat = RenameCategory.WillRename;
                        }

                        plan.Add(new RenamePlanEntry
                        {
                            Fragment = fragment,
                            Asset = asset,
                            LanguageCode = culture,
                            LocalIdHex = localId,
                            CurrentFileName = currentFileName,
                            PlannedFileName = plannedFileName,
                            CurrentDisplayName = currentDisplay,
                            PlannedDisplayName = plannedDisplay,
                            SourcePath = srcPath,
                            Category = cat,
                            IsMultiBound = multiBoundAssets.Contains(asset.Id)
                        });
                    }
                }
            }

            return plan;
        }

        private static readonly Brush IconOkBrush     = new SolidColorBrush(Color.FromRgb(0x7C, 0xB9, 0x75));
        private static readonly Brush IconAccentBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x45));
        private static readonly Brush IconWarnBrush   = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x55));
        private static readonly Brush IconDangerBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x50));

        private static VoiceOverRenameWindow.Row BuildRow(RenamePlanEntry entry)
        {
            string icon;
            Brush brush;
            string categoryLabel;
            switch (entry.Category)
            {
                case RenameCategory.AlreadyCorrect: icon = "\u2713"; brush = IconOkBrush;     categoryLabel = "Already correct";            break;
                case RenameCategory.WillRename:     icon = "\u00BB"; brush = IconAccentBrush; categoryLabel = "Will rename";                break;
                case RenameCategory.DisplayOnly:    icon = "\u25CB"; brush = IconWarnBrush;   categoryLabel = "Display name update";        break;
                case RenameCategory.TargetExists:   icon = "\u26A0"; brush = IconWarnBrush;   categoryLabel = "Target file exists (skip)";  break;
                case RenameCategory.SourceMissing:  icon = "\u2717"; brush = IconDangerBrush; categoryLabel = "Source file missing (skip)"; break;
                default:                            icon = "?";      brush = Brushes.Gray;   categoryLabel = "Unknown";                     break;
            }

            var header = $"{entry.LocalIdHex}  [{entry.LanguageCode}]  \u2014  {categoryLabel}";

            string fileLine;
            string displayLine;
            bool hasFileLine = true;
            bool hasDisplayLine = true;

            const string arrow = "  \u2192  ";

            switch (entry.Category)
            {
                case RenameCategory.AlreadyCorrect:
                    fileLine = "(unchanged)";
                    displayLine = "(unchanged)";
                    break;

                case RenameCategory.WillRename:
                    fileLine = $"{SafeText(entry.CurrentFileName)}{arrow}{SafeText(entry.PlannedFileName)}";
                    displayLine = $"{SafeText(entry.CurrentDisplayName)}{arrow}{SafeText(entry.PlannedDisplayName)}";
                    break;

                case RenameCategory.DisplayOnly:
                    fileLine = "(unchanged)";
                    displayLine = $"{SafeText(entry.CurrentDisplayName)}{arrow}{SafeText(entry.PlannedDisplayName)}";
                    break;

                case RenameCategory.TargetExists:
                    fileLine = $"source: {SafeText(entry.CurrentFileName)}   target \"{entry.PlannedFileName}\" already exists on disk";
                    displayLine = $"planned: {SafeText(entry.PlannedDisplayName)}";
                    break;

                case RenameCategory.SourceMissing:
                    var missing = string.IsNullOrEmpty(entry.SourcePath) ? "(no path)" : Path.GetFileName(entry.SourcePath);
                    fileLine = $"source file missing on disk: {missing}";
                    displayLine = "";
                    hasDisplayLine = false;
                    break;

                default:
                    fileLine = "";
                    displayLine = "";
                    hasFileLine = false;
                    hasDisplayLine = false;
                    break;
            }

            return new VoiceOverRenameWindow.Row
            {
                Icon = icon,
                IconBrush = brush,
                Header = header,
                FileLine = fileLine,
                DisplayLine = displayLine,
                HasFileLine = hasFileLine,
                HasDisplayLine = hasDisplayLine,
                CategoryKey = (int)entry.Category,
                IsMultiBound = entry.IsMultiBound
            };
        }

        private static string SafeText(string s) => string.IsNullOrEmpty(s) ? "(empty)" : s;

        private void NavigateToObject(ObjectProxy target)
        {
            if (target == null || !target.IsValid) return;
            Session.BringObjectIntoView(
                target,
                ApiSession.FocusWindow.Main,
                ApiSession.FocusPane.First,
                ApiSession.FocusTab.Current,
                false);
        }

        private List<ObjectProxy> FlattenToDialogueFragments(IEnumerable<ObjectProxy> roots)
        {
            var list = new List<ObjectProxy>();
            if (roots == null) return list;
            foreach (var root in roots)
                foreach (var df in TraverseDialogueFragments(root))
                    list.Add(df);
            return list;
        }

        private List<ObjectProxy> CollectAllDialogueFragments()
        {
            var flowFolder = Session.GetSystemFolder(SystemFolderNames.Flow);
            const string query = "SELECT * FROM Flow WHERE ObjectType = DialogueFragment";
            var result = Session.RunQuery(query, flowFolder);
            var list = new List<ObjectProxy>();
            if (result?.Rows == null) return list;
            foreach (var row in result.Rows)
                if (row != null && row.ObjectType == ObjectType.DialogueFragment)
                    list.Add(row);
            return list;
        }
    }
}
