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

            ExportSettings settings;
            using (var configForm = new ExportConfigForm(items.Count))
            {
                if (configForm.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                settings = configForm.Settings;
            }

            if ((settings.Mode == ExportMode.Hard || settings.Mode == ExportMode.Extreme) &&
                MessageBox.Show("Hard and Extreme modes can stress the GPU and may crash the app or trigger an AMD/NVIDIA driver timeout.\n\nContinue with " + settings.Mode + " mode?",
                    "GPU crash risk", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            Directory.CreateDirectory(outputRoot);

            var progress = new ExportProgressForm(settings);
            var exported = 0;
            var failed = 0;
            var records = new List<ExportRecord>();
            var previousDrawables = addon.SelectedDrawables?.OfType<GDrawable>().ToList() ?? new List<GDrawable>();
            GDrawable previousDrawable = addon.SelectedDrawable;
            GTexture previousTexture = addon.SelectedTexture;
            object currentSex = null;
            var oldRenderLoopPaused = _customPedsForm.Renderer?.DXMan?.RenderLoopPaused ?? false;

            try
            {
                if (settings.PauseRenderLoop && _customPedsForm.Renderer?.DXMan != null)
                {
                    _customPedsForm.Renderer.DXMan.RenderLoopPaused = true;
                    Thread.Sleep(150);
                    Application.DoEvents();
                }

                progress.Show();
                progress.SetStatus("Starting " + settings.Mode + " export...", 0, items.Count, 0, 0, GetMemoryMb());
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

                    if (!StayUnderRamLimit(progress, settings))
                    {
                        failed += items.Count - i;
                        LogHelper.Log("Export All PNG stopped because RAM stayed above the configured safety limit.", Views.LogType.Warning);
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

                        WaitWithEvents(settings.PreviewSettleMs);

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
                    catch (SharpDX.SharpDXException ex)
                    {
                        failed++;
                        LogHelper.Log("Export All PNG DirectX failure at " + relativePath + ": " + ex.Message, Views.LogType.Error);
                        MessageBox.Show("DirectX/GPU driver error during export. Export stopped to protect the app.\n\nLast item: " + relativePath + "\n\nTry Low or Stable mode with bigger delays.", "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogHelper.Log("Export All PNG skipped " + relativePath + ": " + ex.Message, Views.LogType.Warning);
                    }

                    if (settings.ItemDelayMs > 0)
                    {
                        WaitWithEvents(settings.ItemDelayMs);
                    }

                    if (settings.PauseEveryItems > 0 && (i + 1) % settings.PauseEveryItems == 0)
                    {
                        ForceMemoryCleanup();
                        WaitWithEvents(settings.BatchPauseMs);
                    }
                }
            }
            finally
            {
                WriteExportManifest(outputRoot, records);
                RestoreExportSelection(addon, previousDrawables, previousDrawable, previousTexture);
                ForceMemoryCleanup();

                if (_customPedsForm.Renderer?.DXMan != null)
                {
                    _customPedsForm.Renderer.DXMan.RenderLoopPaused = oldRenderLoopPaused;
                }

                if (!progress.IsDisposed)
                {
                    progress.Close();
                }
            }

            MessageBox.Show("Exported: " + exported + "\nFailed/skipped: " + failed, "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static bool StayUnderRamLimit(ExportProgressForm progress, ExportSettings settings)
        {
            var limitBytes = settings.RamLimitMb * 1024L * 1024L;
            var memory = Process.GetCurrentProcess().PrivateMemorySize64;
            if (memory < limitBytes)
            {
                return true;
            }

            progress.SetStatus("RAM limit reached. Cleaning memory...", 0, 1, 0, 0, GetMemoryMb());
            Application.DoEvents();

            for (var i = 0; i < 3; i++)
            {
                ForceMemoryCleanup();
                WaitWithEvents(500);

                if (Process.GetCurrentProcess().PrivateMemorySize64 < limitBytes)
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

        private static void WaitWithEvents(int milliseconds)
        {
            var remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                var step = Math.Min(100, remaining);
                Thread.Sleep(step);
                Application.DoEvents();
                remaining -= step;
            }
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

        private enum ExportMode
        {
            Low,
            Stable,
            Medium,
            Hard,
            Extreme
        }

        private class ExportSettings
        {
            public ExportMode Mode;
            public int PreviewSettleMs;
            public int ItemDelayMs;
            public int PauseEveryItems;
            public int BatchPauseMs;
            public int RamLimitMb;
            public bool PauseRenderLoop;

            public static ExportSettings FromMode(ExportMode mode)
            {
                switch (mode)
                {
                    case ExportMode.Low:
                        return new ExportSettings { Mode = mode, PreviewSettleMs = 900, ItemDelayMs = 1200, PauseEveryItems = 2, BatchPauseMs = 3000, RamLimitMb = 1500, PauseRenderLoop = true };
                    case ExportMode.Stable:
                        return new ExportSettings { Mode = mode, PreviewSettleMs = 600, ItemDelayMs = 800, PauseEveryItems = 5, BatchPauseMs = 2000, RamLimitMb = 2500, PauseRenderLoop = true };
                    case ExportMode.Medium:
                        return new ExportSettings { Mode = mode, PreviewSettleMs = 350, ItemDelayMs = 400, PauseEveryItems = 10, BatchPauseMs = 1000, RamLimitMb = 3500, PauseRenderLoop = true };
                    case ExportMode.Hard:
                        return new ExportSettings { Mode = mode, PreviewSettleMs = 200, ItemDelayMs = 150, PauseEveryItems = 20, BatchPauseMs = 500, RamLimitMb = 5000, PauseRenderLoop = true };
                    case ExportMode.Extreme:
                        return new ExportSettings { Mode = mode, PreviewSettleMs = 75, ItemDelayMs = 25, PauseEveryItems = 50, BatchPauseMs = 100, RamLimitMb = 8000, PauseRenderLoop = false };
                    default:
                        return FromMode(ExportMode.Stable);
                }
            }
        }

        private class ExportConfigForm : Form
        {
            private readonly ComboBox modeBox;
            private readonly NumericUpDown settleBox;
            private readonly NumericUpDown delayBox;
            private readonly NumericUpDown pauseEveryBox;
            private readonly NumericUpDown pauseMsBox;
            private readonly NumericUpDown ramBox;
            private readonly CheckBox pauseRenderLoopBox;
            private readonly Label warningLabel;
            private readonly int totalItems;
            private bool applyingMode;

            public ExportSettings Settings { get; private set; }

            public ExportConfigForm(int totalItems)
            {
                this.totalItems = totalItems;
                Text = "Export All PNG - Configuration";
                Width = 520;
                Height = 355;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;

                var intro = new Label
                {
                    Left = 12,
                    Top = 12,
                    Width = 480,
                    Height = 42,
                    Text = "Choose export speed. Faster modes stress GPU harder and may crash the app or trigger driver timeout on weaker systems."
                };

                var modeLabel = new Label { Left = 12, Top = 62, Width = 130, Height = 22, Text = "Mode" };
                modeBox = new ComboBox { Left = 160, Top = 60, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
                modeBox.Items.AddRange(Enum.GetNames(typeof(ExportMode)));
                modeBox.SelectedItem = ExportMode.Stable.ToString();
                modeBox.SelectedIndexChanged += (s, e) => ApplyMode((ExportMode)Enum.Parse(typeof(ExportMode), modeBox.SelectedItem.ToString()));

                settleBox = AddNumber("Preview settle ms", 92, 0, 5000);
                delayBox = AddNumber("Delay per PNG ms", 122, 0, 10000);
                pauseEveryBox = AddNumber("Pause every N PNGs", 152, 1, 1000);
                pauseMsBox = AddNumber("Batch pause ms", 182, 0, 30000);
                ramBox = AddNumber("RAM limit MB", 212, 512, 64000);

                pauseRenderLoopBox = new CheckBox
                {
                    Left = 160,
                    Top = 242,
                    Width = 320,
                    Height = 24,
                    Text = "Pause live 3D render loop during export (recommended)"
                };

                warningLabel = new Label
                {
                    Left = 12,
                    Top = 270,
                    Width = 480,
                    Height = 36,
                    Text = "Stable mode is recommended. Total PNGs: " + totalItems
                };

                var okButton = new Button { Left = 312, Top = 308, Width = 85, Height = 26, Text = "Start" };
                var cancelButton = new Button { Left = 407, Top = 308, Width = 85, Height = 26, Text = "Cancel" };
                okButton.Click += (s, e) => { SaveSettings(); DialogResult = DialogResult.OK; Close(); };
                cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

                Controls.Add(intro);
                Controls.Add(modeLabel);
                Controls.Add(modeBox);
                Controls.Add(pauseRenderLoopBox);
                Controls.Add(warningLabel);
                Controls.Add(okButton);
                Controls.Add(cancelButton);

                ApplyMode(ExportMode.Stable);
            }

            private NumericUpDown AddNumber(string label, int top, int minimum, int maximum)
            {
                Controls.Add(new Label { Left = 12, Top = top + 2, Width = 140, Height = 22, Text = label });
                var box = new NumericUpDown { Left = 160, Top = top, Width = 120, Minimum = minimum, Maximum = maximum, Increment = 50 };
                Controls.Add(box);
                return box;
            }

            private void ApplyMode(ExportMode mode)
            {
                if (applyingMode) return;
                applyingMode = true;
                var s = ExportSettings.FromMode(mode);
                settleBox.Value = s.PreviewSettleMs;
                delayBox.Value = s.ItemDelayMs;
                pauseEveryBox.Value = s.PauseEveryItems;
                pauseMsBox.Value = s.BatchPauseMs;
                ramBox.Value = s.RamLimitMb;
                pauseRenderLoopBox.Checked = s.PauseRenderLoop;

                var approxSeconds = totalItems * (s.PreviewSettleMs + s.ItemDelayMs) / 1000.0;
                warningLabel.Text = mode == ExportMode.Hard || mode == ExportMode.Extreme
                    ? "WARNING: " + mode + " can crash the app/GPU driver. Approx base time: " + Math.Round(approxSeconds / 60.0, 1) + " min."
                    : mode + " mode selected. Approx base time: " + Math.Round(approxSeconds / 60.0, 1) + " min.";
                applyingMode = false;
            }

            private void SaveSettings()
            {
                Settings = new ExportSettings
                {
                    Mode = (ExportMode)Enum.Parse(typeof(ExportMode), modeBox.SelectedItem.ToString()),
                    PreviewSettleMs = (int)settleBox.Value,
                    ItemDelayMs = (int)delayBox.Value,
                    PauseEveryItems = (int)pauseEveryBox.Value,
                    BatchPauseMs = (int)pauseMsBox.Value,
                    RamLimitMb = (int)ramBox.Value,
                    PauseRenderLoop = pauseRenderLoopBox.Checked
                };
            }
        }

        private class ExportProgressForm : Form
        {
            private readonly Label statusLabel;
            private readonly Label countLabel;
            private readonly ProgressBar progressBar;
            private readonly Button cancelButton;
            private readonly ExportSettings settings;

            public bool CancelRequested { get; private set; }

            public ExportProgressForm(ExportSettings settings)
            {
                this.settings = settings;
                Text = "Export All PNG - " + settings.Mode;
                Width = 640;
                Height = 170;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                TopMost = true;

                statusLabel = new Label { Left = 12, Top = 12, Width = 600, Height = 24, Text = "Preparing..." };
                countLabel = new Label { Left = 12, Top = 40, Width = 600, Height = 38, Text = "0%" };
                progressBar = new ProgressBar { Left = 12, Top = 82, Width = 600, Height = 20, Minimum = 0, Maximum = 100 };
                cancelButton = new Button { Left = 512, Top = 110, Width = 100, Height = 26, Text = "Cancel" };
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
                countLabel.Text = percent + "% | " + processed + "/" + total + " processed | " + exported + " exported | " + failed + " failed | RAM " + ramMb + " MB\nMode: " + settings.Mode + " | Delay: " + settings.ItemDelayMs + "ms | Pause every " + settings.PauseEveryItems + " | Render loop paused: " + settings.PauseRenderLoop;
                progressBar.Value = percent;
                Refresh();
            }
        }
    }
}