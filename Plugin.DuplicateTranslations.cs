using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Articy.Api;
using Articy.Api.Plugins;
using Articy.ModelFramework;

namespace Kurisu.VoiceOverTools
{
    public partial class Plugin
    {
        internal sealed class DuplicateMember
        {
            public ObjectProxy Fragment;
            public string PropertyName;
            public string LocalIdHex;
            public string SpeakerName;
            public string FragmentPath;
            public string ReferenceText;
            public string OffendingText;
        }

        internal sealed class DuplicateGroup
        {
            public string LanguageCode;
            public string NormalizedKey;
            public string SampleText;
            public List<DuplicateMember> Members;
            public bool AllShareReferenceText;
        }

        public sealed class DuplicateScanOptions
        {
            public string ReferenceLanguage;
            public bool CaseInsensitive;
            public bool CollapseWhitespace;
            public bool IncludeRefIdenticalGroups;
        }

        private List<ObjectProxy> _duplicateScanScope;

        private void FindDuplicateTranslations(MacroCommandDescriptor aDescriptor, List<ObjectProxy> aSelectedobjects)
        {
            var fragments = CollectAllDialogueFragments();
            if (fragments == null || fragments.Count == 0)
            {
                var empty = new MessageWindow { Title = "Duplicate Translations" };
                empty.MessageTextBlock.Text = "No DialogueFragments found in the project.";
                Session.ShowDialog(empty);
                return;
            }

            _duplicateScanScope = fragments;

            var textLanguages = Session.GetTextLanguages();
            if (textLanguages == null || textLanguages.Count < 2)
            {
                var empty = new MessageWindow { Title = "Duplicate Translations" };
                empty.MessageTextBlock.Text =
                    "This tool needs at least two text languages configured in the project " +
                    "(one reference, one or more translation languages).";
                Session.ShowDialog(empty);
                return;
            }

            var defaultRef = Session.GetPrimaryLanguage()?.CultureName ?? textLanguages[0].CultureName;

            var window = new DuplicateTranslationsWindow();
            window.SetSession(
                textLanguages.Select(l => l.CultureName).ToList(),
                defaultRef,
                rebuild: opts => RescanDuplicates(window, opts),
                clearOne: ClearDuplicateOne,
                clearOthers: ClearDuplicateOthers,
                clearAllFiltered: ClearDuplicateAllFiltered,
                navigateFragment: NavigateToObject);

            // initial scan with defaults
            RescanDuplicates(window, new DuplicateScanOptions
            {
                ReferenceLanguage = defaultRef,
                CaseInsensitive = false,
                CollapseWhitespace = false,
                IncludeRefIdenticalGroups = false
            });

            Session.ShowDialog(window);
        }

        private void RescanDuplicates(DuplicateTranslationsWindow window, DuplicateScanOptions opts)
        {
            var groups = BuildDuplicateGroups(_duplicateScanScope, opts);
            var rows = FlattenDuplicateGroups(groups);

            // Speaker + path lists for filter widgets — derived from the surviving rows.
            var speakers = rows.OfType<DuplicateTranslationsWindow.MemberRow>()
                .Select(r => r.SpeakerName ?? "(no speaker)")
                .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in rows.OfType<DuplicateTranslationsWindow.MemberRow>())
            {
                if (string.IsNullOrEmpty(member.FragmentPath)) continue;
                var segs = member.FragmentPath.Split(new[] { " / " }, StringSplitOptions.None);
                var built = "";
                for (int i = 0; i < segs.Length; i++)
                {
                    built = i == 0 ? segs[0] : built + " / " + segs[i];
                    paths.Add(built);
                }
            }
            var sortedPaths = paths.OrderBy(p => p, StringComparer.Ordinal).ToList();

            var languages = Session.GetTextLanguages().Select(l => l.CultureName).ToList();

            window.Populate(
                BuildDuplicateScopeSummary(_duplicateScanScope.Count, groups, opts),
                rows,
                groups.Count,
                groups.Sum(g => g.Members.Count),
                speakers,
                sortedPaths,
                languages);
        }

        private static string BuildDuplicateScopeSummary(int fragmentCount, List<DuplicateGroup> groups, DuplicateScanOptions opts)
        {
            int memberCount = groups.Sum(g => g.Members.Count);
            return
                $"Reference: {opts.ReferenceLanguage}  |  " +
                $"{fragmentCount} DialogueFragment(s) scanned  |  " +
                $"{groups.Count} duplicate-translation group(s), {memberCount} fragment line(s) total";
        }

        private List<DuplicateGroup> BuildDuplicateGroups(IReadOnlyList<ObjectProxy> fragments, DuplicateScanOptions opts)
        {
            var refLang = opts.ReferenceLanguage;
            var textLanguages = Session.GetTextLanguages();
            if (textLanguages == null || string.IsNullOrEmpty(refLang)) return new List<DuplicateGroup>();

            // Bucket: (offendingCulture, normalizedText) -> list of members
            var buckets = new Dictionary<(string lang, string key), List<DuplicateMember>>();

            foreach (var fragment in fragments)
            {
                if (fragment == null || fragment.ObjectType != ObjectType.DialogueFragment) continue;

                var localId = DecimalToHex(fragment.Id.ToString());
                var speaker = TryGetSpeakerName(fragment) ?? "(no speaker)";
                var path = TryGetFragmentPath(fragment);

                foreach (var (propName, text) in EnumerateAllTextProxies(fragment))
                {
                    var refText = text.Texts[refLang] ?? "";
                    foreach (var lang in textLanguages)
                    {
                        var culture = lang.CultureName;
                        if (string.IsNullOrEmpty(culture)) continue;
                        if (string.Equals(culture, refLang, StringComparison.OrdinalIgnoreCase)) continue;

                        var raw = text.Texts[culture] ?? "";
                        if (string.IsNullOrWhiteSpace(raw)) continue;

                        var key = NormalizeForCompare(raw, opts);
                        if (string.IsNullOrEmpty(key)) continue;

                        var bucketKey = (culture, key);
                        if (!buckets.TryGetValue(bucketKey, out var list))
                            buckets[bucketKey] = list = new List<DuplicateMember>();

                        list.Add(new DuplicateMember
                        {
                            Fragment = fragment,
                            PropertyName = propName,
                            LocalIdHex = localId,
                            SpeakerName = speaker,
                            FragmentPath = path,
                            ReferenceText = refText,
                            OffendingText = raw
                        });
                    }
                }
            }

            var groups = new List<DuplicateGroup>();
            foreach (var kvp in buckets)
            {
                var members = kvp.Value;
                // Distinct fragments — multiple text properties on the same fragment with the same
                // translation aren't a "duplicate across fragments." Need at least two distinct frags.
                var distinctFragments = members.Select(m => m.Fragment.Id).Distinct().Count();
                if (distinctFragments < 2) continue;

                var refKeys = new HashSet<string>(
                    members.Select(m => NormalizeForCompare(m.ReferenceText ?? "", opts)),
                    StringComparer.Ordinal);
                bool allShareRef = refKeys.Count == 1 && !string.IsNullOrEmpty(refKeys.First());

                if (!opts.IncludeRefIdenticalGroups && allShareRef) continue;

                groups.Add(new DuplicateGroup
                {
                    LanguageCode = kvp.Key.lang,
                    NormalizedKey = kvp.Key.key,
                    SampleText = members[0].OffendingText,
                    Members = members.OrderBy(m => m.SpeakerName, StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(m => m.FragmentPath, StringComparer.Ordinal)
                                     .ToList(),
                    AllShareReferenceText = allShareRef
                });
            }

            // Stable order: by language, then by member-count desc, then by sample text.
            return groups
                .OrderBy(g => g.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(g => g.Members.Count)
                .ThenBy(g => g.SampleText, StringComparer.Ordinal)
                .ToList();
        }

        private static string NormalizeForCompare(string s, DuplicateScanOptions opts)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var trimmed = s.Trim();
            if (opts.CollapseWhitespace) trimmed = Regex.Replace(trimmed, @"\s+", " ");
            if (opts.CaseInsensitive) trimmed = trimmed.ToLowerInvariant();
            return trimmed;
        }

        private List<object> FlattenDuplicateGroups(List<DuplicateGroup> groups)
        {
            var rows = new List<object>();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                rows.Add(new DuplicateTranslationsWindow.GroupRow
                {
                    GroupIndex = gi,
                    LanguageCode = g.LanguageCode,
                    MemberCount = g.Members.Count,
                    SampleText = g.SampleText,
                    AllShareReferenceText = g.AllShareReferenceText
                });
                for (int mi = 0; mi < g.Members.Count; mi++)
                {
                    var m = g.Members[mi];
                    rows.Add(new DuplicateTranslationsWindow.MemberRow
                    {
                        GroupIndex = gi,
                        MemberIndex = mi,
                        LanguageCode = g.LanguageCode,
                        LocalIdHex = m.LocalIdHex,
                        SpeakerName = m.SpeakerName,
                        FragmentPath = m.FragmentPath,
                        PropertyLabel = "." + m.PropertyName,
                        ReferenceText = m.ReferenceText,
                        OffendingText = m.OffendingText,
                        Tag = (g, m)
                    });
                }
            }
            return rows;
        }

        // ─── Action callbacks ──────────────────────────────────────────────────────────────

        private void ClearDuplicateOne(object memberTag)
        {
            if (memberTag is not ValueTuple<DuplicateGroup, DuplicateMember> tuple) return;
            var (group, member) = tuple;
            ApplyTextClear(member.Fragment, member.PropertyName, group.LanguageCode);
        }

        private void ClearDuplicateOthers(object memberTag)
        {
            if (memberTag is not ValueTuple<DuplicateGroup, DuplicateMember> tuple) return;
            var (group, keep) = tuple;
            foreach (var m in group.Members)
            {
                if (ReferenceEquals(m, keep)) continue;
                ApplyTextClear(m.Fragment, m.PropertyName, group.LanguageCode);
            }
        }

        private int ClearDuplicateAllFiltered(IReadOnlyList<object> filteredMemberTags)
        {
            int count = 0;
            foreach (var tag in filteredMemberTags)
            {
                if (tag is not ValueTuple<DuplicateGroup, DuplicateMember> tuple) continue;
                var (group, member) = tuple;
                ApplyTextClear(member.Fragment, member.PropertyName, group.LanguageCode);
                count++;
            }
            return count;
        }

        private void ApplyTextClear(ObjectProxy fragment, string propertyName, string culture)
        {
            if (fragment == null || !fragment.IsValid) return;
            if (fragment[propertyName] is not TextProxy text) return;
            ClearTranslationText(text, culture);
        }
    }
}
