using System.Collections.Generic;
using Articy.Api;
using Articy.ModelFramework;

namespace Kurisu.VoiceOverTools
{
    public partial class Plugin
    {
        // VO-binding clear via the API-supported null path. Confirmed against the live
        // QueryLayer.dll (TextProxy.SetVoiceOverReference + LocalizationHelper.SetAudioSample
        // both have explicit null branches). The placeholder-asset workaround used in
        // RemoveVoiceOvers is kept for now until null-clearing is verified in practice.
        private void ClearVoiceOverBinding(ObjectProxy fragment, string propertyName, string culture)
        {
            if (fragment == null || !fragment.IsValid) return;
            if (fragment[propertyName] is not TextProxy text) return;

            Session.SuspendLocaStateHandling();
            try
            {
                text.VoiceOverReferences[culture] = null;
            }
            finally
            {
                Session.ResumeLocaStateHandling();
            }
        }

        private static void ClearTranslationText(TextProxy text, string culture)
        {
            if (text == null || string.IsNullOrEmpty(culture)) return;
            text.Texts[culture] = string.Empty;
        }

        // BBCodeText and MarkupText are calculated views over the same underlying Text content
        // (registered as CustomProperty with CalculatedModelGetter — see QueryLayer's
        // AddCustomProperty calls). Including either as a separate "text property" produces
        // phantom rows that mirror .Text. Articy's own internal code groups them together for
        // exclusion, so we do the same.
        private static bool IsCalculatedTextView(string propertyName)
        {
            return string.Equals(propertyName, "BBCodeText", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "MarkupText", System.StringComparison.OrdinalIgnoreCase);
        }

        // Walks the fragment + its VO-enabled text properties + every VO/Text language and
        // invokes the callback once per (fragment, property, culture). Used by the new tools
        // to centralize the iteration shape used in both Audit and the rename planner.
        private static IEnumerable<(string property, TextProxy text)> EnumerateVoiceOverTextProxies(ObjectProxy fragment)
        {
            if (fragment == null || fragment.ObjectType != ObjectType.DialogueFragment) yield break;
            var properties = fragment.GetAvailableProperties();
            if (properties == null) yield break;
            foreach (var prop in properties)
            {
                if (IsCalculatedTextView(prop)) continue;
                var info = fragment.GetPropertyInfo(prop);
                if (info is not { HasVoiceOver: true }) continue;
                if (fragment[prop] is not TextProxy text) continue;
                yield return (prop, text);
            }
        }

        // Same shape but for any text property (VO-enabled or not). The duplicate-translation
        // detector wants every localized text on the fragment, regardless of VO flag.
        private static IEnumerable<(string property, TextProxy text)> EnumerateAllTextProxies(ObjectProxy fragment)
        {
            if (fragment == null || fragment.ObjectType != ObjectType.DialogueFragment) yield break;
            var properties = fragment.GetAvailableProperties();
            if (properties == null) yield break;
            foreach (var prop in properties)
            {
                if (IsCalculatedTextView(prop)) continue;
                var info = fragment.GetPropertyInfo(prop);
                if (info == null || info.DataType != PropertyDataType.Text) continue;
                if (fragment[prop] is not TextProxy text) continue;
                yield return (prop, text);
            }
        }
    }
}
