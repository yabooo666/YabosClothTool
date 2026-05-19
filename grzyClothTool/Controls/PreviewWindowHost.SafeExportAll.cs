using grzyClothTool.Helpers;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Models.Texture;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace grzyClothTool.Controls
{
    public partial class PreviewWindowHost
    {
        private const long ExportRamLimitBytes = 2500L * 1024L * 1024L;
        private const int ExportPauseEveryItems = 5;
        private const int ExportPauseMs = 120;

        private void ExportAllPngSafely(string outputRoot)
        {
            if (_customPedsForm == null || _customPedsForm.IsDisposed || !_customPedsForm.formopen)
            {
                MessageBox.Show("3D preview is not ready.", "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var addon = MainWindow.AddonManager?.SelectedAddon;
            if (addon?.Drawables == null)
            {
                MessageBox.Show("No addon is loaded.", "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var items = addon.Drawables
                .OfType<GDrawable>()
                .Where(d => d != null && !d.IsEncrypted && d.Textures != null && d.Textures.Count > 0)
                .SelectMany(d => d.Textures
                    .Where(t => t != null && !t.IsPreviewDisabled)
                    .Select(t => new ExportItem { Drawable = d, Texture = t }))
                .ToList();

            if (items.Count == 0)
            {
                MessageBox.Show("No drawable textures found to export.", "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Directory.CreateDirectory(outputRoot);

            var progress = new ExportProgressForm();
            var exported = 0;
            var failed = 0;
            var records = new List<ExportRecord>();
            var previousDrawables = addon.SelectedDrawables?.OfType<GDrawable>().ToList() ?? new List<GDrawable>();
            GDrawable previousDrawable = addon.SelectedDrawable;
            GTexture previousTexture = addon.SelectedTexture;
            object currentSex = null;

            try
            {
                progress.Show();
                progress.SetStatus("Starting safe export...", 0, items.Count, 0, 0, GetMemoryMb());
                Application.DoEvents();

                for (var i = 0; i < items.Count; i++)
                {
                    if (progress.CancelRequested)
                    {
                        break;
                    }

                    var item = items[i];
                    var component = SafePathSegment((item.Drawable.TypeName ?? "unknown").ToLowerInvariant());
                    var drawableId = SafePathSegment(item.Drawable.DisplayNumber);
                    var textureId = item.Texture.TxtNumber.ToString("000");
                    var relativePath = component + "/" + drawableId + "/" + textureId + ".png";
                    var outputPath = Path.Combine(outputRoot, component, drawableId, textureId + ".png");

                    progress.SetStatus("Exporting " + relativePath, i + 1, items.Count, exported, failed, GetMemoryMb());
                    Application.DoEvents();

                    if (!StayUnderRamLimit(progress))
                    {
                        failed += items.Count - i;
                        LogHelper.Log("Export All PNG stopped because RAM stayed above the safety limit.", Views.LogType.Warning);
                        break;
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                        var updates = new Dictionary<string, string>();
                        if (currentSex == null || !currentSex.Equals(item.Drawable.Sex))
                        {
                            var sexName = item.Drawable.Sex.ToString().ToLowerInvariant();
                            SetPedModel(sexName == "male" ? "mp_m_freemode_01" : "mp_f_freemode_01");
                            updates["GenderChanged"] = string.Empty;
                            currentSex = item.Drawable.Sex;
                        }

                        addon.SelectedDrawables.Clear();
                        addon.SelectedDrawables.Add(item.Drawable);
                        addon.SelectedDrawable = item.Drawable;
                        addon.SelectedTexture = item.Texture;

                        UpdateDrawables(addon.SelectedDrawables, item.Texture, updates);

                        for (var wait = 0; wait < 4; wait++)
                        {
                            Application.DoEvents();
                            Thread.Sleep(40);
                        }

                        _customPedsForm.ExportCurrentPreviewPngStable(outputPath);
                        exported++;

                        records.Add(new ExportRecord
                        {
                            Component = component,
                            DrawableId = drawableId,
                            TextureId = textureId,
                            DrawableName = item.Drawable.Name,
                            TextureName = item.Texture.DisplayName,
                            Path = relativePath
                        });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogHelper.Log("Export All PNG skipped " + relativePath + ": " + ex.Message, Views.LogType.Warning);
                    }

                    if ((i + 1) % ExportPauseEveryItems == 0)
                    {
                        ForceMemoryCleanup();
                        Thread.Sleep(ExportPauseMs);
                        Application.DoEvents();
                    }
                }
            }
            finally
            {
                WriteExportManifest(outputRoot, records);
                RestoreExportSelection(addon, previousDrawables, previousDrawable, previousTexture);
                ForceMemoryCleanup();
                if (!progress.IsDisposed)
                {
                    progress.Close();
                }
            }

            MessageBox.Show("Exported: " + exported + "\nFailed/skipped: " + failed, "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static bool StayUnderRamLimit(ExportProgressForm progress)
        {
            var memory = Process.GetCurrentProcess().PrivateMemorySize64;
            if (memory < ExportRamLimitBytes)
            {
                return true;
            }

            progress.SetStatus("RAM limit reached. Cleaning memory...", 0, 1, 0, 0, GetMemoryMb());
            Application.DoEvents();

            for (var i = 0; i < 3; i++)
            {
                ForceMemoryCleanup();
                Thread.Sleep(300);
                Application.DoEvents();

                if (Process.GetCurrentProcess().PrivateMemorySize64 < ExportRamLimitBytes)
                {
                    return true;
                }
            }

            return false;
        }

        private static long GetMemoryMb()
        {
            return Process.GetCurrentProcess().PrivateMemorySize64 / 1024L / 1024L;
        }

        private static void ForceMemoryCleanup()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private void RestoreExportSelection(dynamic addon, List<GDrawable> previousDrawables, GDrawable previousDrawable, GTexture previousTexture)
        {
            try
            {
                addon.SelectedDrawables.Clear();
                foreach (var drawable in previousDrawables)
                {
                    addon.SelectedDrawables.Add(drawable);
                }

                addon.SelectedDrawable = previousDrawable;
                addon.SelectedTexture = previousTexture;

                if (previousDrawables.Count > 0)
                {
                    UpdateDrawables(addon.SelectedDrawables, previousTexture, new Dictionary<string, string>());
                }
            }
            catch
            {
            }
        }

        private static string SafePathSegment(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }
            return value;
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static void WriteExportManifest(string outputRoot, List<ExportRecord> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"exportedAt\": \"" + DateTime.UtcNow.ToString("O") + "\",");
            sb.AppendLine("  \"files\": [");

            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];
                sb.AppendLine("    {");
                sb.AppendLine("      \"component\": \"" + EscapeJson(r.Component) + "\",");
                sb.AppendLine("      \"drawableId\": \"" + EscapeJson(r.DrawableId) + "\",");
                sb.AppendLine("      \"textureId\": \"" + EscapeJson(r.TextureId) + "\",");
                sb.AppendLine("      \"drawableName\": \"" + EscapeJson(r.DrawableName) + "\",");
                sb.AppendLine("      \"textureName\": \"" + EscapeJson(r.TextureName) + "\",");
                sb.AppendLine("      \"path\": \"" + EscapeJson(r.Path) + "\"");
                sb.Append("    }");
                if (i < records.Count - 1)
                {
                    sb.Append(",");
                }
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(outputRoot, "export_manifest.json"), sb.ToString(), Encoding.UTF8);
        }

        private class ExportItem
        {
            public GDrawable Drawable;
            public GTexture Texture;
        }

        private class ExportRecord
        {
            public string Component;
            public string DrawableId;
            public string TextureId;
            public string DrawableName;
            public string TextureName;
            public string Path;
        }

        private class ExportProgressForm : Form
        {
            private readonly Label statusLabel;
            private readonly Label countLabel;
            private readonly ProgressBar progressBar;
            private readonly Button cancelButton;

            public bool CancelRequested { get; private set; }

            public ExportProgressForm()
            {
                Text = "Export All PNG";
                Width = 600;
                Height = 160;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                TopMost = true;

                statusLabel = new Label { Left = 12, Top = 12, Width = 560, Height = 24, Text = "Preparing..." };
                countLabel = new Label { Left = 12, Top = 40, Width = 560, Height = 22, Text = "0%" };
                progressBar = new ProgressBar { Left = 12, Top = 68, Width = 560, Height = 20, Minimum = 0, Maximum = 100 };
                cancelButton = new Button { Left = 472, Top = 98, Width = 100, Height = 26, Text = "Cancel" };
                cancelButton.Click += (s, e) => { CancelRequested = true; cancelButton.Enabled = false; cancelButton.Text = "Cancelling..."; };

                Controls.Add(statusLabel);
                Controls.Add(countLabel);
                Controls.Add(progressBar);
                Controls.Add(cancelButton);
            }

            public void SetStatus(string status, int processed, int total, int exported, int failed, long ramMb)
            {
                if (IsDisposed) return;
                total = Math.Max(1, total);
                var percent = Math.Max(0, Math.Min(100, (int)Math.Round(processed * 100.0 / total)));
                statusLabel.Text = status;
                countLabel.Text = percent + "% | " + processed + "/" + total + " processed | " + exported + " exported | " + failed + " failed | RAM " + ramMb + " MB";
                progressBar.Value = percent;
                Refresh();
            }
        }
    }
}
