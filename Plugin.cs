using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Articy.Api;
using Articy.Api.Data;
using Articy.Api.Plugins;
using Articy.ModelFramework;
using Articy.ModelFramework.Localization;
using Articy.Models.Assets;
using Articy.Models.Story;
using Articy.Utils.StaticUtils;
using LIds.Articy.Editor.Aspects.LocationEditor;
using LIds.Articy.Editor.Filter;
using LIds.Articy.Models.General.Link;
using LIds.Articy.ModelsEx.TraitValues.Properties;
using Texts = LIds.Kurisu.VoiceOverTools;

namespace Kurisu.VoiceOverTools
{
	/// <summary>
	/// public implementation part of plugin code, contains all overrides of the plugin class.
	/// </summary>
	public partial class Plugin : MacroPlugin
	{
		public override string DisplayName
		{
			get { return LocalizeStringNoFormat(Texts.Plugin.DisplayName); }
		}

		public override string ContextName
		{
			get { return LocalizeStringNoFormat(Texts.Plugin.ContextName); }
		}

		// Articy uses BACKSLASH ('\') as the submenu separator inside CaptionLid — see
		// https://www.articy.com/adxdevkit/html/writing_a_plugin.htm. Prefixing every command
		// with "Kurisu\" groups them under a single Kurisu submenu in the ribbon and the
		// right-click menu. (Forward slash is rendered literally by Articy, which is why an
		// earlier attempt with "Kurisu/" produced a flat command name.)
		private const string MenuPrefix = "Kurisu\\";

		public override List<MacroCommandDescriptor> GetMenuEntries(List<ObjectProxy> aSelectedObjects, ContextMenuContext aContext )
		{
			var result = new List<MacroCommandDescriptor>();

            var selectMasterFlowObject = new MacroCommandDescriptor
            {
                CaptionLid = MenuPrefix + "Select Flow Object",
                ModifiesData = true,
                Execute = SelectFlowObject
            };

            var renameAllVoiceOvers = new MacroCommandDescriptor
            {
                CaptionLid = MenuPrefix + "Rename All Voice-Overs",
                ModifiesData = true,
                Execute = RenameAllVoiceOvers
            };

            var renameSelectedVoiceOvers = new MacroCommandDescriptor
            {
                CaptionLid = MenuPrefix + "Rename Selected Voice-Overs",
                ModifiesData = true,
                Execute = RenameSelectedVoiceOver,
                UserData = aSelectedObjects
            };

            var displaySelectedProperties = new MacroCommandDescriptor
            {
                CaptionLid = MenuPrefix + "Display Selected Properties",
                ModifiesData = true,
                Execute = DisplaySelectedObjectProperties,
                UserData = aSelectedObjects
            };


            var removeTranslationsAndVoiceOvers = new MacroCommandDescriptor
            {
                CaptionLid  = MenuPrefix + "Remove Translations and Voice-Overs",
                ModifiesData = true,
                Execute      = RemoveTranslationsAndVoiceOvers,
                UserData     = aSelectedObjects      // keeps current selection
            };

            var cleanUpOrphanedAudioAssets = new MacroCommandDescriptor
            {
                CaptionLid   = MenuPrefix + "Clean Up Orphaned Voice-Overs",
                ModifiesData = true,
                Execute      = CleanUpOrphanedAudioAssets
            };

            var auditVoiceOvers = new MacroCommandDescriptor
            {
                CaptionLid   = MenuPrefix + "Audit Voice-Overs",
                ModifiesData = false,
                Execute      = AuditVoiceOvers
            };

            var findDuplicateTranslations = new MacroCommandDescriptor
            {
                CaptionLid   = MenuPrefix + "Find Duplicate Translations",
                ModifiesData = true,
                Execute      = FindDuplicateTranslations
            };

            var findMultiBoundVoiceOvers = new MacroCommandDescriptor
            {
                CaptionLid   = MenuPrefix + "Find Multi-bound Voice-Overs",
                ModifiesData = true,
                Execute      = FindMultiBoundVoiceOvers
            };

            switch ( aContext )
            {
                case ContextMenuContext.Global:
                    // entries for the "global" commands of the ribbon menu are requested
                    //result.Add(selectMasterFlowObject);
                    result.Add(renameAllVoiceOvers);
                    result.Add(cleanUpOrphanedAudioAssets);
                    result.Add(auditVoiceOvers);
                    result.Add(findDuplicateTranslations);
                    result.Add(findMultiBoundVoiceOvers);
                    return result;

                default:
                    // normal context menu when working in the content area, navigator, search
                    if ( aSelectedObjects.Count >= 1)
                    {
                        //result.Add(displaySelectedProperties);
                        result.Add(renameSelectedVoiceOvers);
                        result.Add(removeTranslationsAndVoiceOvers);
                        return result;
                    }
                    return result;
            }
        }

        private ObjectProxy dummyVO;

        private void RemoveTranslationsAndVoiceOvers(MacroCommandDescriptor d, List<ObjectProxy> aSelectedobjects)
        {
            if (aSelectedobjects == null || aSelectedobjects.Count == 0) return;

            // primary language is always the first one in the list
            var mainCulture = Session.GetTextLanguages()[0].CultureName;
            dummyVO = GetOrCreateSilentAsset(Session);

            foreach (var selectedObject in aSelectedobjects)
            {
                foreach (var fragment in TraverseDialogueFragments(selectedObject))
                    RenameSingleVoiceOver(fragment);
                ProcessObjectForRemoval(selectedObject, mainCulture);
            }

            // Capture the disk path of the placeholder before deleting its Articy entry.
            string dummyPath = null;
            if (dummyVO != null && dummyVO.IsValid && dummyVO.HasProperty(ObjectPropertyNames.AbsoluteFilePath))
                dummyPath = dummyVO[ObjectPropertyNames.AbsoluteFilePath] as string;

            Session.DeleteObject(dummyVO);

            // let Articy process the changes
            Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());

            // Remove the placeholder WAV from disk so it doesn't accumulate or show up as an orphaned asset.
            if (!string.IsNullOrEmpty(dummyPath) && File.Exists(dummyPath))
            {
                try { File.Delete(dummyPath); }
                catch { /* non-fatal — Articy will clean it on session close anyway */ }
            }
        }

        private void ProcessObjectForRemoval(ObjectProxy obj, string mainCulture)
        {
            if (obj != null && obj.ObjectType is ObjectType.DialogueFragment)
            {
                // 1) strip translations + voice-overs from every text property
                var properties = obj.GetAvailableProperties();
                if (properties != null)
                {
                    foreach (var prop in properties)
                    {
                        var info = obj.GetPropertyInfo(prop);
                        if (info.DataType == PropertyDataType.Text && obj[prop] is TextProxy txt)
                        {
                            RemoveTranslations(txt, mainCulture);
                            RemoveVoiceOvers(obj, txt);
                        }
                    }
                }
            }

            var children = obj.GetChildren();
            if (children != null)
            {
                // 2) recurse through any children (covers FlowFragments, Dialogues, etc.)
                foreach (var child in children)
                    ProcessObjectForRemoval(child, mainCulture);
            }
        }
        
        private void RemoveTranslations(TextProxy text, string mainCulture)
        {
            // Enumerate every language registered in the project
            var textLanguages = Session.GetTextLanguages();
            foreach (var lang in textLanguages) 
            {
                var culture = lang.CultureName;       

                if (culture == null)
                    continue;

                // 1) wipe any translation that isn't the main language
                if (!culture.Equals(mainCulture, StringComparison.OrdinalIgnoreCase))
                    text.Texts[culture] = string.Empty;    
            }
        }

        private void RemoveVoiceOvers(ObjectProxy dialogueFragment, TextProxy text)
        {
            Session.SuspendLocaStateHandling();
            try
            {
                var voiceOverLanguages = Session.GetVoiceOverLanguages();
                foreach (var voLang in voiceOverLanguages)
                {
                    var culture = voLang.CultureName;
                    var vor = text.VoiceOverReferences[culture];
                    bool hasVo = vor != null && vor.IsValid;

                    if (hasVo)
                    {
                        text.VoiceOverReferences[culture] = dummyVO;
                    }
                }
            }
            finally
            {
                Session.ResumeLocaStateHandling();
            }
        }



        private void RenameSelectedVoiceOver(MacroCommandDescriptor aDescriptor, List<ObjectProxy> aSelectedobjects)
        {
            var fragments = FlattenToDialogueFragments(aSelectedobjects);
            var label = aSelectedobjects == null || aSelectedobjects.Count == 0
                ? "Selected (empty)"
                : $"Selected ({aSelectedobjects.Count} root object(s))";
            RunRenamePlanner(fragments, label);
        }

        private void SelectFlowObject(MacroCommandDescriptor aDescriptor, List<ObjectProxy> aSelectedobjects)
        {
            Session.BringObjectIntoView(Session.GetSystemFolder(SystemFolderNames.Flow),
                ApiSession.FocusWindow.Main, ApiSession.FocusPane.First, ApiSession.FocusTab.Current, false);
        }

        private void RenameAllVoiceOvers(MacroCommandDescriptor aDescriptor, List<ObjectProxy> aSelectedobjects)
        {
            var fragments = CollectAllDialogueFragments();
            RunRenamePlanner(fragments, "All DialogueFragments");
        }

        private void RenameSingleVoiceOver(ObjectProxy aSelectedobject)
        {
            if (aSelectedobject ==  null)
                return;

            var localId = DecimalToHex(aSelectedobject.Id.ToString());

            var properties = aSelectedobject.GetAvailableProperties();

            var languages = Session.GetVoiceOverLanguages();
            if (languages == null)
                return;

            if (properties != null)
            {
                foreach (var property in properties)
                {
                    if (property == "BBCodeText")
                        continue;

                    var propertyInfo = aSelectedobject.GetPropertyInfo(property);
                    if (propertyInfo is {HasVoiceOver: true} && aSelectedobject[property] is TextProxy text)
                    {
                        foreach (var apiLanguageInfo in languages)
                        {

                            var audioVoiceOverAssetOriginal = text.VoiceOverReferences[apiLanguageInfo.CultureName];
                            
                            if (audioVoiceOverAssetOriginal != null && audioVoiceOverAssetOriginal.ObjectType is ObjectType.Asset)
                            {
                                var absoluteFilePathOriginal = audioVoiceOverAssetOriginal[ObjectPropertyNames.AbsoluteFilePath] as string;
                                
                                var fileNameOriginal = Path.GetFileNameWithoutExtension(absoluteFilePathOriginal);

                                var fileNameNew = $"{localId}_{apiLanguageInfo.CultureName}";

                                var fullDescription = text.Texts[apiLanguageInfo.CultureName];
                                var shortDescription = TruncateText(fullDescription, 3);
                                
                                // Use GetDisplayName() — matches what Articy shows in the UI, which may synthesize
                                // from filename when the raw DisplayName property isn't populated.
                                var displayNameOriginal = audioVoiceOverAssetOriginal.GetDisplayName() ?? "";
                                var displayNameNew = GetAssetDisplayName(aSelectedobject, apiLanguageInfo.CultureName, fileNameNew, shortDescription);

                                if (fileNameOriginal != null && fileNameOriginal.Contains(fileNameNew))
                                {
                                    // Tolerate trailing whitespace so historical display names with an accidental
                                    // trailing space don't get re-written unnecessarily.
                                    if (!string.Equals(displayNameOriginal.TrimEnd(), (displayNameNew ?? "").TrimEnd(), StringComparison.Ordinal))
                                        SetAudioAssetText(audioVoiceOverAssetOriginal, displayNameNew, fullDescription);
                                    continue;
                                }

                                if (!File.Exists(absoluteFilePathOriginal) || CopyExists(absoluteFilePathOriginal, fileNameNew))
                                    continue;

                                var absoluteFilePathNew = CopyFile(absoluteFilePathOriginal, fileNameNew);
                                if (absoluteFilePathNew == null)
                                    continue;

                                var originalDisplayName = audioVoiceOverAssetOriginal[ObjectPropertyNames.DisplayName] as string;

                                //ChangeAssetImport(text, apiLanguageInfo.CultureName, audioVoiceOverAssetOriginal, absoluteFilePathNew, displayNameNew, fullDescription);
                                ChangeAssetInPlace(audioVoiceOverAssetOriginal, absoluteFilePathNew, displayNameNew, fullDescription, originalDisplayName);
                            }
                        }
                    }
                }
            }
        }

        private void ChangeAssetImport(TextProxy targetTextProxy, string languageCode, ObjectProxy audioVoiceAsset, string absoluteFilePathNew,
            string displayName,
            string description)
        {
            var parentObject = audioVoiceAsset.GetParent();

            var audioVoiceOverAssetNew = Session.ImportAsset(parentObject, displayName,
                absoluteFilePathNew);

            Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());

            if (audioVoiceOverAssetNew.HasProperty(ObjectPropertyNames.Text))
            {
                audioVoiceOverAssetNew[ObjectPropertyNames.Text] = description;
            }

            targetTextProxy.VoiceOverReferences[languageCode] = audioVoiceOverAssetNew;

            Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());

            //Session.DeleteObject(audioVoiceAsset);

            //Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());

            //DeleteFile(absoluteFilePathNew);

            SetAudioAssetText(audioVoiceAsset,displayName,description);
        }

        private void ChangeAssetInPlace(ObjectProxy audioVoiceAsset, string absoluteFilePathNew,
            string displayName,
            string description,
            string externalID)
        {
            Session.ChangeAsset(audioVoiceAsset, absoluteFilePathNew);
            //Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());

            SetAudioAssetText(audioVoiceAsset,displayName,description);
            SetAudioAssetExternalID(audioVoiceAsset, externalID);
        }

        private void SetAudioAssetText(ObjectProxy audioAsset, string displayName, string description)
        {
            if (audioAsset.HasProperty(ObjectPropertyNames.DisplayName))
                audioAsset[ObjectPropertyNames.DisplayName] = displayName;

            if (audioAsset.HasProperty(ObjectPropertyNames.Text))
                audioAsset[ObjectPropertyNames.Text] = description;
        }

        private void SetAudioAssetExternalID(ObjectProxy audioAsset, string externalID)
        {
            if (audioAsset.HasProperty(ObjectPropertyNames.ExternalId))
                audioAsset[ObjectPropertyNames.ExternalId] = externalID;
        }

        // Articy's display name limit is 128 chars. We compose the display so the trailing
        // [fileName] stays intact and drop path segments from the middle with "..." when the
        // whole string would exceed the budget.
        private const int MAX_DISPLAY_NAME_LENGTH = 128;

        private string GetAssetDisplayName(ObjectProxy aSelectedobject, string languageCode, string fileName, string shortDescription)
        {
            var trimmedPath = string.Join(" / ", aSelectedobject.GetObjectPath().Split(" / ")[1..]);
            var fragmentIndex = aSelectedobject.GetParent().GetSortedChildren()
                .Where(o => o.ObjectType is ObjectType.DialogueFragment)
                .ToList().IndexOf(aSelectedobject);

            var suffix = $" - [{fileName}]";
            var tail = $" / {fragmentIndex + 1} - \"{shortDescription}\"";

            var full = trimmedPath + tail + suffix;
            if (full.Length <= MAX_DISPLAY_NAME_LENGTH)
                return full;

            return TruncatePathMiddle(trimmedPath, tail, suffix, MAX_DISPLAY_NAME_LENGTH);
        }

        /// <summary>
        /// Shorten <paramref name="path"/> so that path + tail + suffix fits within
        /// <paramref name="budget"/>. Replaces the middle path segment with "..." and drops
        /// neighbours of the ellipsis one at a time, preferring to preserve the leaf (end)
        /// because that's usually the most specific to the fragment. Falls back to a
        /// character-level middle truncation if even leaf + ellipsis won't fit.
        /// </summary>
        private static string TruncatePathMiddle(string path, string tail, string suffix, int budget)
        {
            const string ELLIPSIS = "...";
            var pathBudget = budget - tail.Length - suffix.Length;

            var segments = path.Split(new[] { " / " }, StringSplitOptions.None).ToList();

            if (segments.Count >= 3)
            {
                // Replace the middle segment with "...", then drop neighbours until it fits.
                segments[segments.Count / 2] = ELLIPSIS;

                while (string.Join(" / ", segments).Length > pathBudget)
                {
                    var ei = segments.IndexOf(ELLIPSIS);
                    if (ei < 0) break;

                    // Prefer dropping the left neighbour so the leaf (end) stays visible.
                    if (ei > 0) segments.RemoveAt(ei - 1);
                    else if (ei < segments.Count - 1) segments.RemoveAt(ei + 1);
                    else break;
                }

                var joined = string.Join(" / ", segments);
                if (joined.Length <= pathBudget)
                    return joined + tail + suffix;
            }

            // Fallback: character-level middle truncation of the raw path.
            const string MID = " ... ";
            var keep = Math.Max(4, pathBudget - MID.Length);
            var startChars = keep / 2;
            var endChars = keep - startChars;
            startChars = Math.Min(startChars, path.Length);
            endChars = Math.Min(endChars, Math.Max(0, path.Length - startChars));
            var hardTrunc = path.Substring(0, startChars) + MID + path.Substring(path.Length - endChars);
            return hardTrunc + tail + suffix;
        }

        private void DisplaySelectedObjectProperties(MacroCommandDescriptor aDescriptor,
            List<ObjectProxy> aSelectedobjects)
        {

            if (aSelectedobjects != null)
            {
                foreach (var aSelectedobject in aSelectedobjects)
                {
                    DisplaySingleObjectProperties(aSelectedobject);
                }
            }
        }

        private void DisplaySingleObjectProperties(ObjectProxy aSelectedobject)
        {
            MessageWindow window = new MessageWindow();

            var localId = DecimalToHex(aSelectedobject.Id.ToString());

            window.Title = localId;

            var properties = aSelectedobject.GetAvailableProperties();

            if (properties != null)
            {
                window.MessageTextBlock.Text = $"There are {properties.Count} properties.\n";
                foreach (var property in properties)
                {
                    var propertyInfo = aSelectedobject.GetPropertyInfo(property);
                    window.MessageTextBlock.Text += $"\n -> {property} || {propertyInfo.DataType}";

                    if (propertyInfo.DataType == PropertyDataType.Text)
                    {
                        var text = string.Empty;
                        if (aSelectedobject[property] is TextProxy textP)
                        {
                            text = $" || {textP.Texts[Session.GetTextLanguages()[0].CultureName]}";
                            text = string.IsNullOrWhiteSpace(text) ? " || None" : text;    
                        }
                        else if  (aSelectedobject[property] is string objectProperty)
                        {

                            text += $" {objectProperty}";
                        }
                        
                        window.MessageTextBlock.Text += $": {text}";
                    }
                }
            }

            Session.ShowDialog(window);
        }


        public static string DecimalToHex(long decimalNumber)
        {
            var hexString = decimalNumber.ToString("X");
            hexString = "0x0" + hexString;
            // Convert the decimal number to a hexadecimal string
            return hexString;
        }

        public static string DecimalToHex(string decimalString)
        {
            // Try to parse the string into a long integer
            if (long.TryParse(decimalString, out long decimalNumber))
            {
                return DecimalToHex(decimalNumber);
            }
            else
            {
                // Handle invalid input
                throw new ArgumentException("Invalid decimal number string.");
            }
        }


        public static bool CopyExists(string originalFilePath, string newFileName)
        {
            try
            {
                // Get the directory of the original file
                string directory = Path.GetDirectoryName(originalFilePath);

                // Get the file extension of the original file
                string fileExtension = Path.GetExtension(originalFilePath);

                // Combine the new file name with the original file extension
                string newFileNameWithExtension = newFileName + fileExtension;

                // Combine the directory with the new file name to create the destination path
                string newFilePath = Path.Combine(directory, newFileNameWithExtension);

                return File.Exists(newFilePath);
            }
            catch
            {
                return false;
            }
        }

        public static string CopyFile(string originalFilePath, string newFileName, string newDirectory = "")
        {
            try
            {
                // Get the directory of the original file
                string directory = newDirectory == "" ? Path.GetDirectoryName(originalFilePath) : newDirectory;

                // Get the file extension of the original file
                string fileExtension = Path.GetExtension(originalFilePath);

                // Combine the new file name with the original file extension
                string newFileNameWithExtension = newFileName + fileExtension;

                // Combine the directory with the new file name to create the destination path
                string newFilePath = Path.Combine(directory, newFileNameWithExtension);

                // Copy the original file to the new file path
                if (!File.Exists(newFilePath))
                {
                    File.Copy(originalFilePath, newFilePath);
                    Console.WriteLine($"File copied successfully to: {newFilePath}");
                }
                else
                {
                    Console.WriteLine($"File already existed: {newFilePath}");
                }

                // Return true if the file was copied successfully
                return newFilePath;
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the copy process
                Console.WriteLine($"Error copying file: {ex.Message}");
                return null;
            }
        }

        public static string TruncateText(string text, int wordCount)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Split the text into words
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Check if the text has fewer words than the desired count
            if (words.Length <= wordCount)
            {
                return text; // No need to truncate, return the original text
            }

            // Take the first X words and join them back into a string
            var truncatedWords = words.Take(wordCount);

            // Combine the truncated words with an ellipsis
            return string.Join(" ", truncatedWords) + "...";
        }


        public override Brush GetIcon(string aIconName)
        {
            switch (aIconName)
            {                
                case "SetColor":
                    return Session.CreateBrushFromFile(Manifest.ManifestPath + "Resources\\SetColor.png");
                case "Red":
                    return new SolidColorBrush(Colors.Red);
            }
            return null;
        }

        /// <summary>
        /// Recursively enumerates all DialogueFragments that live under <paramref name="root"/>.
        /// </summary>
        private static IEnumerable<ObjectProxy> TraverseDialogueFragments(ObjectProxy root)
        {
            if (root == null) yield break;

            // 1) Is the current node itself a DialogueFragment?
            if (root.ObjectType == ObjectType.DialogueFragment)                 // API docs use this same check :contentReference[oaicite:0]{index=0}
                yield return root;

            // 2) Recurse through children (if any)
            var kids = root.GetChildren();                                      // every ObjectProxy exposes GetChildren() :contentReference[oaicite:1]{index=1}
            if (kids == null) yield break;

            foreach (var child in kids)
            {
                foreach (var df in TraverseDialogueFragments(child))
                    yield return df;
            }
        }


        private static ObjectProxy GetOrCreateSilentAsset(ApiSession session)
        {
            const string PLACEHOLDER_NAME = "VO_SILENCE_PLACEHOLDER.wav";

            //------------------------------------------------------------------
            // 1)  Look through all asset objects that already exist
            //------------------------------------------------------------------
            var allAssets = session.GetObjectsByType(ObjectType.Asset);               // full asset list :contentReference[oaicite:0]{index=0}
            var existing  = allAssets.FirstOrDefault(a =>
                string.Equals((string)a[ObjectPropertyNames.DisplayName],
                              PLACEHOLDER_NAME,
                              StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing;                                                      // reuse – nothing to import

            //------------------------------------------------------------------
            // 2)  File isn’t there yet → generate silent WAV on disk
            //------------------------------------------------------------------
            string assetsDir = Path.Combine(session.GetProjectFolderPath(),           // project root path :contentReference[oaicite:1]{index=1}
                                            "Assets");
            Directory.CreateDirectory(assetsDir);

            string fullPath = Path.Combine(assetsDir, PLACEHOLDER_NAME);
            if (!File.Exists(fullPath))
                WriteMinimalWav(fullPath);                                            // your 44-byte header helper

            //------------------------------------------------------------------
            // 3)  Import the file as a new asset under the “Assets” root folder
            //------------------------------------------------------------------
            ObjectProxy assetsFolder = session.GetSystemFolder(SystemFolderNames.Assets);  // navigator’s Assets node :contentReference[oaicite:2]{index=2}

            // ImportAsset(parentFolderProxy, displayName, sourceFilePath)
            ObjectProxy newAsset = session.ImportAsset(assetsFolder,                  // valid overload :contentReference[oaicite:3]{index=3}
                                                       PLACEHOLDER_NAME,
                                                       fullPath);                     // local WAV file

            // wait until the background asset job finishes
            session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());         // block until indexed :contentReference[oaicite:4]{index=4}

            return newAsset;
        }


        private static void WriteMinimalWav(string path,
            int sampleRate = 48000,
            short channels = 1,
            short bits     = 16)
        {
            int bytesPerSample = bits / 8;
            short blockAlign   = (short)(channels * bytesPerSample);
            int byteRate       = sampleRate * blockAlign;
            int dataBytes      = 0;                   // silence ⇒ no sample data
            int riffSize       = 36 + dataBytes;      // RIFF chunk size field

            using var bw = new BinaryWriter(File.Create(path));

            // ---- RIFF header ----
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(riffSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // ---- fmt  sub-chunk ----
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                 // Sub-chunk size (PCM)
            bw.Write((short)1);           // AudioFormat = 1 (PCM)
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bits);

            // ---- data sub-chunk ----
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);          // 0 bytes of sample data
        }
    }
}
