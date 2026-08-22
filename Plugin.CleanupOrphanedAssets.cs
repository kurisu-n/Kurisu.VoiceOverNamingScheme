using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Articy.Api;
using Articy.Api.Plugins;
using Articy.ModelFramework;

namespace Kurisu.VoiceOverTools
{
    public partial class Plugin
    {
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".opus", ".wma", ".aiff", ".aif"
        };

        private sealed class OrphanInfo
        {
            public ObjectProxy Asset;
            public string DisplayName;
            public string AbsolutePath;
            public long SizeBytes;
        }

        private void CleanUpOrphanedAudioAssets(MacroCommandDescriptor aDescriptor, List<ObjectProxy> aSelectedobjects)
        {
            var referencedIds = CollectReferencedVoiceOverAssetIds();
            var orphans = FindOrphanedAudioAssets(referencedIds);

            long totalBytes = orphans.Sum(o => o.SizeBytes);
            var rows = orphans
                .Select(o =>
                {
                    var pathDisplay = string.IsNullOrEmpty(o.AbsolutePath) ? "(no path)" : o.AbsolutePath;
                    return $"{o.DisplayName}  |  {FormatSize(o.SizeBytes),10}  |  {pathDisplay}";
                })
                .ToList();

            var window = new CleanupOrphanedAssetsWindow();
            window.Populate(rows, orphans.Count, totalBytes, idx => NavigateToAsset(orphans[idx].Asset));
            Session.ShowDialog(window);

            if (!window.Confirmed || orphans.Count == 0)
                return;

            switch (window.SelectedAction)
            {
                case CleanupOrphanedAssetsWindow.ActionChoice.DryRun:
                    ShowReport("Dry Run Complete",
                        $"Dry run complete. No changes were made.\n\n" +
                        $"Orphaned audio assets found: {orphans.Count}\n" +
                        $"Total on-disk size:          {FormatSize(totalBytes)}\n\n" +
                        $"Re-run and select a delete option to remove them.");
                    return;

                case CleanupOrphanedAssetsWindow.ActionChoice.DeleteArticyOnly:
                    ExecuteDeletion(orphans, deleteDiskFiles: false);
                    return;

                case CleanupOrphanedAssetsWindow.ActionChoice.DeleteArticyAndDisk:
                    if (!ConfirmImmediateDiskDeletion(orphans.Count, totalBytes)) return;
                    ExecuteDeletion(orphans, deleteDiskFiles: true);
                    return;
            }
        }

        private static bool ConfirmImmediateDiskDeletion(int orphanCount, long totalBytes)
        {
            var message =
                $"Are you really sure you want to delete the files on disk right now?\n\n" +
                $"{orphanCount} file(s), {FormatSize(totalBytes)} total.\n\n" +
                $"Articy would have deleted these files for you when the session is closed, " +
                $"and you would have kept the ability to Undo the Articy-side removal until then.\n\n" +
                $"Choosing this option deletes the files immediately with NO Undo.\n\n" +
                $"Proceed?";

            var result = MessageBox.Show(
                message,
                "Confirm immediate disk deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            return result == MessageBoxResult.Yes;
        }

        private HashSet<ulong> CollectReferencedVoiceOverAssetIds()
        {
            var referenced = new HashSet<ulong>();
            var voLanguages = Session.GetVoiceOverLanguages();
            if (voLanguages == null || voLanguages.Count == 0)
                return referenced;

            var flowFolder = Session.GetSystemFolder(SystemFolderNames.Flow);
            const string query = "SELECT * FROM Flow WHERE ObjectType = DialogueFragment";
            var result = Session.RunQuery(query, flowFolder);
            if (result?.Rows == null) return referenced;

            foreach (var fragment in result.Rows)
            {
                if (fragment == null || fragment.ObjectType != ObjectType.DialogueFragment)
                    continue;

                var properties = fragment.GetAvailableProperties();
                if (properties == null) continue;

                foreach (var prop in properties)
                {
                    var info = fragment.GetPropertyInfo(prop);
                    if (info is not { HasVoiceOver: true }) continue;
                    if (fragment[prop] is not TextProxy text) continue;

                    foreach (var lang in voLanguages)
                    {
                        var culture = lang.CultureName;
                        if (string.IsNullOrEmpty(culture)) continue;

                        var vo = text.VoiceOverReferences[culture];
                        if (vo != null && vo.IsValid && vo.ObjectType == ObjectType.Asset)
                            referenced.Add(vo.Id);
                    }
                }
            }

            return referenced;
        }

        private List<OrphanInfo> FindOrphanedAudioAssets(HashSet<ulong> referencedIds)
        {
            var orphans = new List<OrphanInfo>();
            var allAssets = Session.GetObjectsByType(ObjectType.Asset);
            if (allAssets == null) return orphans;

            foreach (var asset in allAssets)
            {
                if (asset == null) continue;
                if (referencedIds.Contains(asset.Id)) continue;
                if (!asset.HasProperty(ObjectPropertyNames.AbsoluteFilePath)) continue;

                var path = asset[ObjectPropertyNames.AbsoluteFilePath] as string;
                if (string.IsNullOrEmpty(path)) continue;

                var ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext) || !AudioExtensions.Contains(ext)) continue;

                long size = 0;
                if (File.Exists(path))
                {
                    try { size = new FileInfo(path).Length; }
                    catch { size = 0; }
                }

                orphans.Add(new OrphanInfo
                {
                    Asset = asset,
                    DisplayName = asset.GetDisplayName() ?? "(unnamed)",
                    AbsolutePath = path,
                    SizeBytes = size
                });
            }

            return orphans;
        }

        private void ExecuteDeletion(List<OrphanInfo> orphans, bool deleteDiskFiles)
        {
            int articyDeleted = 0;
            int articyFailed = 0;
            int diskDeleted = 0;
            int diskMissing = 0;
            int diskFailed = 0;
            var errors = new List<string>();

            foreach (var info in orphans)
            {
                var capturedPath = info.AbsolutePath;

                try
                {
                    Session.DeleteObject(info.Asset);
                    articyDeleted++;
                }
                catch (Exception ex)
                {
                    articyFailed++;
                    errors.Add($"[Articy] {info.DisplayName}: {ex.Message}");
                    continue;
                }

                if (!deleteDiskFiles) continue;

                if (string.IsNullOrEmpty(capturedPath) || !File.Exists(capturedPath))
                {
                    diskMissing++;
                    continue;
                }

                try
                {
                    File.Delete(capturedPath);
                    diskDeleted++;
                }
                catch (Exception ex)
                {
                    diskFailed++;
                    errors.Add($"[Disk] {capturedPath}: {ex.Message}");
                }
            }

            Session.WaitForAssetProcessing(new WaitForAssetProcessingArgs());

            var report = $"Articy entries deleted: {articyDeleted}";
            if (articyFailed > 0) report += $"  (failed: {articyFailed})";

            if (deleteDiskFiles)
            {
                report += $"\nDisk files deleted:     {diskDeleted}";
                if (diskMissing > 0) report += $"  (missing on disk: {diskMissing})";
                if (diskFailed > 0) report += $"  (failed: {diskFailed})";
            }
            else
            {
                report += "\n\nDisk files were NOT touched by this plugin. Articy will delete them automatically when the session is closed. " +
                          "Until then the Articy-side removal remains reversible via Undo.";
            }

            if (errors.Count > 0)
            {
                report += "\n\nErrors:\n" + string.Join("\n", errors);
            }

            ShowReport("Cleanup Complete", report);
        }

        private void ShowReport(string title, string message)
        {
            // Drain pending dispatcher work before opening the report.
            //
            // Why: Session.ChangeAsset (used by the rename pass) ends up in
            // AssetManager.ReimportAssetsWithUserNotification, which when running interactively
            // without a custom progress handler creates its own ProgressDialogWrapper and shows
            // it. The async worker closes that dialog on completion via a UI-thread-queued event.
            //
            // Session.WaitForAssetProcessing waits for assets to be clean but does NOT guarantee
            // those queued progress-dialog close events have fired. If we call ShowDialog while
            // a progress dialog is still in the visual tree and holds keyboard focus, Articy's
            // MessageBoxEx.GetTargetWindow() returns the dying progress dialog as our Owner —
            // when it finishes closing, WPF closes our owned report with it, and Articy's
            // modal-stack accounting gets scrambled, leaving the UI in a stuck-modal state.
            //
            // Invoke at ApplicationIdle drains every higher-priority queued item (including the
            // progress-dialog close) before returning, so by the time we ask for GetTargetWindow
            // the focus has settled back on the main Articy window.
            DrainDispatcher();

            var msg = new MessageWindow { Title = title };
            msg.MessageTextBlock.Text = message;
            Session.ShowDialog(msg);
        }

        private static void DrainDispatcher()
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            try
            {
                app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch
            {
                // Non-fatal — worst case the report inherits a slightly stale owner.
            }
        }

        private void NavigateToAsset(ObjectProxy asset)
        {
            if (asset == null || !asset.IsValid) return;
            Session.BringObjectIntoView(
                asset,
                ApiSession.FocusWindow.Main,
                ApiSession.FocusPane.First,
                ApiSession.FocusTab.Current,
                false);
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int u = 0;
            while (size >= 1024 && u < units.Length - 1)
            {
                size /= 1024;
                u++;
            }
            return $"{size:0.##} {units[u]}";
        }
    }
}
