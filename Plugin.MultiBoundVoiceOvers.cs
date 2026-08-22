using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Articy.Api;
using Articy.Api.Plugins;
using Articy.ModelFramework;

namespace Kurisu.VoiceOverTools
{
    public partial class Plugin
    {
        internal sealed class MultiBoundBinding
        {
            public ObjectProxy Fragment;
            public string PropertyName;
            public string Culture;
            public string LocalIdHex;
            public string SpeakerName;
            public string FragmentPath;
            public string LineText;
        }

        internal sealed class MultiBoundAssetGroup
        {
            public ObjectProxy Asset;
            public string DisplayName;
            public string FileName;
            public string AbsolutePath;
            public long FileSizeBytes;
            public List<MultiBoundBinding> Bindings;
        }

        public sealed class MultiBoundScanOptions
        {
            public bool IncludeIntraFragment;  // default false → fragment-distinct, like the existing audit
        }

        private List<ObjectProxy> _multiBoundScanScope;

        private void FindMultiBoundVoiceOvers(MacroCommandDescriptor aDescriptor, List<ObjectProxy> aSelectedobjects)
        {
            var fragments = CollectAllDialogueFragments();
            if (fragments == null || fragments.Count == 0)
            {
                var empty = new MessageWindow { Title = "Multi-bound Voice-Overs" };
                empty.MessageTextBlock.Text = "No DialogueFragments found in the project.";
                Session.ShowDialog(empty);
                return;
            }

            _multiBoundScanScope = fragments;

            var window = new MultiBoundVoiceOversWindow();
            window.SetSession(
                rebuild: opts => RescanMultiBound(window, opts),
                clearOne: ClearMultiBoundOne,
                clearOthers: ClearMultiBoundOthers,
                navigateFragment: NavigateToObject,
                navigateAsset: NavigateToObject);

            RescanMultiBound(window, new MultiBoundScanOptions { IncludeIntraFragment = false });
            Session.ShowDialog(window);
        }

        private void RescanMultiBound(MultiBoundVoiceOversWindow window, MultiBoundScanOptions opts)
        {
            var groups = BuildMultiBoundGroups(_multiBoundScanScope, opts);
            var rows = FlattenMultiBoundGroups(groups);

            var speakers = rows.OfType<MultiBoundVoiceOversWindow.BindingRow>()
                .Select(r => r.SpeakerName ?? "(no speaker)")
                .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in rows.OfType<MultiBoundVoiceOversWindow.BindingRow>())
            {
                if (string.IsNullOrEmpty(b.FragmentPath)) continue;
                var segs = b.FragmentPath.Split(new[] { " / " }, StringSplitOptions.None);
                var built = "";
                for (int i = 0; i < segs.Length; i++)
                {
                    built = i == 0 ? segs[0] : built + " / " + segs[i];
                    paths.Add(built);
                }
            }
            var sortedPaths = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();

            var languages = rows.OfType<MultiBoundVoiceOversWindow.BindingRow>()
                .Select(b => b.Culture)
                .Where(l => !string.IsNullOrEmpty(l))
                .Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();

            window.Populate(
                BuildMultiBoundScopeSummary(_multiBoundScanScope.Count, groups),
                rows,
                groups.Count,
                groups.Sum(g => g.Bindings.Count),
                speakers,
                sortedPaths,
                languages);
        }

        private static string BuildMultiBoundScopeSummary(int fragmentCount, List<MultiBoundAssetGroup> groups)
        {
            int bindingCount = groups.Sum(g => g.Bindings.Count);
            return
                $"{fragmentCount} DialogueFragment(s) scanned  |  " +
                $"{groups.Count} multi-bound asset(s), {bindingCount} binding(s) total";
        }

        private List<MultiBoundAssetGroup> BuildMultiBoundGroups(IReadOnlyList<ObjectProxy> fragments, MultiBoundScanOptions opts)
        {
            var voLanguages = Session.GetVoiceOverLanguages();
            if (voLanguages == null || voLanguages.Count == 0) return new List<MultiBoundAssetGroup>();

            // assetId -> bindings
            var byAsset = new Dictionary<ulong, (ObjectProxy asset, List<MultiBoundBinding> bindings)>();

            foreach (var fragment in fragments)
            {
                if (fragment == null || fragment.ObjectType != ObjectType.DialogueFragment) continue;
                var localId = DecimalToHex(fragment.Id.ToString());
                var speaker = TryGetSpeakerName(fragment) ?? "(no speaker)";
                var path = TryGetFragmentPath(fragment);

                foreach (var (propName, text) in EnumerateVoiceOverTextProxies(fragment))
                {
                    foreach (var lang in voLanguages)
                    {
                        var culture = lang.CultureName;
                        if (string.IsNullOrEmpty(culture)) continue;

                        var asset = text.VoiceOverReferences[culture];
                        if (asset == null || !asset.IsValid || asset.ObjectType != ObjectType.Asset) continue;

                        if (!byAsset.TryGetValue(asset.Id, out var entry))
                            byAsset[asset.Id] = entry = (asset, new List<MultiBoundBinding>());

                        entry.bindings.Add(new MultiBoundBinding
                        {
                            Fragment = fragment,
                            PropertyName = propName,
                            Culture = culture,
                            LocalIdHex = localId,
                            SpeakerName = speaker,
                            FragmentPath = path,
                            LineText = text.Texts[culture] ?? ""
                        });
                    }
                }
            }

            var groups = new List<MultiBoundAssetGroup>();
            foreach (var kvp in byAsset)
            {
                var (asset, bindings) = kvp.Value;
                int distinct = opts.IncludeIntraFragment
                    ? bindings.Count
                    : bindings.Select(b => b.Fragment.Id).Distinct().Count();
                if (distinct < 2) continue;

                var path = asset[ObjectPropertyNames.AbsoluteFilePath] as string;
                long size = 0;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try { size = new FileInfo(path).Length; } catch { /* unreadable, leave 0 */ }
                }

                groups.Add(new MultiBoundAssetGroup
                {
                    Asset = asset,
                    DisplayName = asset.GetDisplayName() ?? "(unnamed)",
                    FileName = string.IsNullOrEmpty(path) ? "(no path)" : Path.GetFileName(path),
                    AbsolutePath = path ?? "",
                    FileSizeBytes = size,
                    Bindings = bindings
                        .OrderBy(b => b.SpeakerName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(b => b.FragmentPath, StringComparer.Ordinal)
                        .ToList()
                });
            }

            return groups
                .OrderByDescending(g => g.Bindings.Count)
                .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<object> FlattenMultiBoundGroups(List<MultiBoundAssetGroup> groups)
        {
            var rows = new List<object>();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                rows.Add(new MultiBoundVoiceOversWindow.AssetGroupRow
                {
                    GroupIndex = gi,
                    AssetDisplayName = g.DisplayName,
                    FileName = g.FileName,
                    AbsolutePath = g.AbsolutePath,
                    FileSizeText = g.FileSizeBytes > 0 ? FormatSize(g.FileSizeBytes) : "(missing on disk)",
                    BindingCount = g.Bindings.Count,
                    AssetTag = g.Asset
                });
                for (int bi = 0; bi < g.Bindings.Count; bi++)
                {
                    var b = g.Bindings[bi];
                    rows.Add(new MultiBoundVoiceOversWindow.BindingRow
                    {
                        GroupIndex = gi,
                        BindingIndex = bi,
                        Culture = b.Culture,
                        LocalIdHex = b.LocalIdHex,
                        SpeakerName = b.SpeakerName,
                        FragmentPath = b.FragmentPath,
                        PropertyLabel = "." + b.PropertyName,
                        LineText = b.LineText,
                        Tag = (g, b)
                    });
                }
            }
            return rows;
        }

        // ─── Action callbacks ──────────────────────────────────────────────────────────────

        private void ClearMultiBoundOne(object bindingTag)
        {
            if (bindingTag is not ValueTuple<MultiBoundAssetGroup, MultiBoundBinding> tuple) return;
            var (_, binding) = tuple;
            ClearVoiceOverBinding(binding.Fragment, binding.PropertyName, binding.Culture);
            Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());
        }

        private void ClearMultiBoundOthers(object bindingTag)
        {
            if (bindingTag is not ValueTuple<MultiBoundAssetGroup, MultiBoundBinding> tuple) return;
            var (group, keep) = tuple;
            foreach (var b in group.Bindings)
            {
                if (ReferenceEquals(b, keep)) continue;
                ClearVoiceOverBinding(b.Fragment, b.PropertyName, b.Culture);
            }
            Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());
        }
    }
}
