using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CodeWalker.GameFiles;
using SharpDX;
using SharpDX.Direct3D11;
using Color = SharpDX.Color;

namespace CodeWalker
{
    public partial class CustomPedsForm
    {
        private const float StableLowrMinimumExportRadius = 0.85f;
        private const float StableLowrCenterZOffset = -0.25f;
        private const float StableTorsoMinimumExportRadius = 0.65f;
        private const float StableDefaultMinimumExportRadius = 0.20f;
        private const float StableLowrExportCameraPadding = 2.25f;
        private const float StableTorsoExportCameraPadding = 1.95f;
        private const float StableDefaultExportCameraPadding = 1.35f;

        public event Action BatchExportRequested;
        public Dictionary<string, Drawable> BatchLoadedDrawables = new Dictionary<string, Drawable>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Drawable, List<TextureDictionary>> BatchLoadedTextureVariants = new Dictionary<Drawable, List<TextureDictionary>>();

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ReplaceExportButtonForStableLowrCapture();
            AddExportAllPreviewButton();
        }

        private void ReplaceExportButtonForStableLowrCapture()
        {
            if (ToolsPanel == null) return;

            var oldButton = ToolsPanel.Controls.Find("ExportPreviewButton", false).OfType<Button>().FirstOrDefault();
            if (oldButton != null && (oldButton.Tag as string) == "StableLowrExport") return;

            var location = oldButton != null ? oldButton.Location : new System.Drawing.Point(ToolsPanel.Width - 99, 3);
            var size = oldButton != null ? oldButton.Size : new System.Drawing.Size(93, 23);
            var anchor = oldButton != null ? oldButton.Anchor : AnchorStyles.Top | AnchorStyles.Right;
            var tabIndex = oldButton != null ? oldButton.TabIndex : 18;

            if (oldButton != null)
            {
                ToolsPanel.Controls.Remove(oldButton);
                oldButton.Dispose();
            }

            var exportPreviewButton = new Button
            {
                Anchor = anchor,
                Location = location,
                Name = "ExportPreviewButton",
                Size = size,
                TabIndex = tabIndex,
                Text = "Export PNG",
                UseVisualStyleBackColor = true,
                Tag = "StableLowrExport"
            };
            exportPreviewButton.Click += StableExportPreviewButton_Click;

            ToolsPanel.Controls.Add(exportPreviewButton);
            exportPreviewButton.BringToFront();
        }

        private void AddExportAllPreviewButton()
        {
            if (ToolsPanel == null) return;
            if (ToolsPanel.Controls.Find("ExportAllPreviewButton", false).Length > 0) return;

            var exportAllButton = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new System.Drawing.Point(ToolsPanel.Width - 207, 3),
                Name = "ExportAllPreviewButton",
                Size = new System.Drawing.Size(105, 23),
                TabIndex = 19,
                Text = "Export All PNG",
                UseVisualStyleBackColor = true
            };
            exportAllButton.Click += ExportAllPreviewButton_Click;

            ToolsPanel.Controls.Add(exportAllButton);
            exportAllButton.BringToFront();
        }

        private void StableExportPreviewButton_Click(object sender, EventArgs e)
        {
            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.AddExtension = true;
                saveFileDialog.DefaultExt = "png";
                saveFileDialog.FileName = "preview.png";
                saveFileDialog.Filter = "PNG image (*.png)|*.png";
                saveFileDialog.OverwritePrompt = true;
                saveFileDialog.Title = "Export preview PNG";

                if (saveFileDialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    ExportCurrentPreviewPngStable(saveFileDialog.FileName);
                    UpdateStatus("Exported preview PNG");
                }
                catch (Exception ex)
                {
                    LogError("Preview PNG export failed: " + ex);
                    MessageBox.Show(this, "Unable to export preview PNG:\n" + ex.Message, "Export PNG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportAllPreviewButton_Click(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Choose folder for exported clothing PNGs";
                folderDialog.ShowNewFolderButton = true;

                if (folderDialog.ShowDialog(this) != DialogResult.OK) return;

                BatchExportProgressForm progress = null;
                try
                {
                    progress = new BatchExportProgressForm();
                    progress.Show(this);
                    progress.SetStatus("Preparing imported clothes...", 0, 1, 0, 0);
                    Application.DoEvents();

                    BatchExportRequested?.Invoke();
                    var result = ExportAllLoadedPreviewPngs(folderDialog.SelectedPath, progress);
                    UpdateStatus($"Exported {result.ExportedCount} clothing PNGs");
                    MessageBox.Show(this, $"Exported {result.ExportedCount} clothing PNGs.\nSkipped/failed: {result.FailedCount}.", "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    LogError("Batch preview PNG export failed: " + ex);
                    MessageBox.Show(this, "Unable to batch export preview PNGs:\n" + ex.Message, "Export All PNG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    if (progress != null && !progress.IsDisposed) progress.Close();
                    BatchLoadedDrawables.Clear();
                    BatchLoadedTextureVariants.Clear();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }

        private BatchExportResult ExportAllLoadedPreviewPngs(string outputRoot, BatchExportProgressForm progress)
        {
            Directory.CreateDirectory(outputRoot);

            var exportItems = GetLoadedExportItems().ToList();
            if (exportItems.Count == 0)
            {
                throw new InvalidOperationException("No imported drawables are available for batch export. Open/import an addon first, then try again.");
            }

            var total = exportItems.Sum(item => item.TextureDictionaries.Count(t => SelectPreviewTexture(t) != null));
            if (total <= 0)
            {
                throw new InvalidOperationException("No valid diffuse textures were found for batch export.");
            }

            var exported = new List<BatchExportRecord>();
            var failed = 0;
            var processed = 0;
            var usedDrawableIdsByComponent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            for (var drawableOrder = 0; drawableOrder < exportItems.Count; drawableOrder++)
            {
                var item = exportItems[drawableOrder];
                var componentName = GetComponentDirectoryName(item.Drawable?.Name);
                if (componentName == null) continue;

                var textureDictionaries = item.TextureDictionaries.Where(t => SelectPreviewTexture(t) != null).ToList();
                if (textureDictionaries.Count == 0) continue;

                var drawableId = GetDrawableExportId(item.Drawable?.Name, drawableOrder);
                drawableId = EnsureUniqueDrawableExportId(componentName, drawableId, usedDrawableIdsByComponent);

                var drawableFolder = Path.Combine(outputRoot, componentName, drawableId);
                Directory.CreateDirectory(drawableFolder);

                for (var textureIndex = 0; textureIndex < textureDictionaries.Count; textureIndex++)
                {
                    if (progress != null && progress.CancelRequested)
                    {
                        WriteBatchExportManifest(outputRoot, exported);
                        return new BatchExportResult { ExportedCount = exported.Count, FailedCount = failed };
                    }

                    var textureDictionary = textureDictionaries[textureIndex];
                    var texture = SelectPreviewTexture(textureDictionary);
                    if (texture == null) continue;

                    var textureId = textureIndex.ToString("000");
                    var relativePath = componentName + "/" + drawableId + "/" + textureId + ".png";
                    var filePath = Path.Combine(drawableFolder, textureId + ".png");

                    processed++;
                    progress?.SetStatus("Exporting " + relativePath, processed, total, exported.Count, failed);
                    UpdateStatus($"Exporting {relativePath}");
                    Application.DoEvents();

                    try
                    {
                        ExportDrawablePreviewPngStable(filePath, item.Drawable, textureDictionary, texture);
                        exported.Add(new BatchExportRecord
                        {
                            Component = componentName,
                            DrawableId = drawableId,
                            TextureId = textureId,
                            DrawableName = item.Drawable?.Name ?? string.Empty,
                            TextureName = texture?.Name ?? string.Empty,
                            RelativePath = relativePath
                        });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogError("Skipped batch PNG export " + relativePath + ": " + ex.Message);
                    }
                }
            }

            WriteBatchExportManifest(outputRoot, exported);
            return new BatchExportResult { ExportedCount = exported.Count, FailedCount = failed };
        }

        private IEnumerable<BatchExportItem> GetLoadedExportItems()
        {
            if (BatchLoadedDrawables.Count > 0)
            {
                foreach (var drawable in BatchLoadedDrawables.Values)
                {
                    if (drawable == null) continue;

                    if (!BatchLoadedTextureVariants.TryGetValue(drawable, out var textureDictionaries))
                    {
                        textureDictionaries = new List<TextureDictionary>();
                    }

                    yield return new BatchExportItem
                    {
                        Drawable = drawable,
                        TextureDictionaries = textureDictionaries
                    };
                }

                yield break;
            }

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drawable in LoadedDrawables.Values)
            {
                if (drawable == null || !seenNames.Add(drawable.Name ?? string.Empty)) continue;

                var textureDictionary = GetSelectedExportTexture(drawable);
                yield return new BatchExportItem
                {
                    Drawable = drawable,
                    TextureDictionaries = textureDictionary != null ? new List<TextureDictionary> { textureDictionary } : new List<TextureDictionary>()
                };
            }
        }

        private static Texture SelectPreviewTexture(TextureDictionary textureDictionary)
        {
            var textures = textureDictionary?.Textures?.data_items?.Where(t => t != null).ToList();
            if (textures == null || textures.Count == 0) return null;

            var diffuse = textures.FirstOrDefault(t => IsLikelyDiffuseTexture(t.Name));
            if (diffuse != null) return diffuse;

            return textures.FirstOrDefault(t => !IsLikelyUtilityTexture(t.Name)) ?? textures.FirstOrDefault();
        }

        private static bool IsLikelyDiffuseTexture(string textureName)
        {
            var name = (textureName ?? string.Empty).ToLowerInvariant();
            return name.EndsWith("_uni") || name.Contains("_diff") || name.Contains("diffuse") || name.EndsWith("_a") || name.EndsWith("_b") || name.EndsWith("_c") || name.EndsWith("_d");
        }

        private static bool IsLikelyUtilityTexture(string textureName)
        {
            var name = (textureName ?? string.Empty).ToLowerInvariant();
            return name.Contains("normal") || name.Contains("bump") || name.Contains("spec") || name.Contains("detail") || name.Contains("mask");
        }

        private static string GetComponentDirectoryName(string drawableName)
        {
            var componentIndex = GetComponentIndexFromDrawableName(drawableName);
            switch (componentIndex)
            {
                case 0: return "head";
                case 1: return "berd";
                case 2: return "hair";
                case 3: return "uppr";
                case 4: return "lowr";
                case 5: return "hand";
                case 6: return "feet";
                case 7: return "teef";
                case 8: return "accs";
                case 9: return "task";
                case 10: return "decl";
                case 11: return "jbib";
                default: return null;
            }
        }

        private static string GetDrawableExportId(string drawableName, int fallbackIndex)
        {
            var parsedId = TryGetLastNumericId(drawableName);
            return parsedId >= 0 ? parsedId.ToString("000") : fallbackIndex.ToString("000");
        }

        private static int TryGetLastNumericId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return -1;

            var matches = Regex.Matches(value, "\\d+");
            if (matches.Count == 0) return -1;

            return int.TryParse(matches[matches.Count - 1].Value, out var id) ? id : -1;
        }

        private static string EnsureUniqueDrawableExportId(string componentName, string drawableId, Dictionary<string, HashSet<string>> usedDrawableIdsByComponent)
        {
            if (!usedDrawableIdsByComponent.TryGetValue(componentName, out var usedIds))
            {
                usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedDrawableIdsByComponent[componentName] = usedIds;
            }

            if (usedIds.Add(drawableId)) return drawableId;

            var suffix = 1;
            string candidate;
            do
            {
                candidate = drawableId + "_" + suffix.ToString("000");
                suffix++;
            }
            while (!usedIds.Add(candidate));

            return candidate;
        }

        private void WriteBatchExportManifest(string outputRoot, List<BatchExportRecord> records)
        {
            var manifestPath = Path.Combine(outputRoot, "export_manifest.json");
            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"ped\": \"" + EscapeJson(PedModel) + "\",");
            json.AppendLine("  \"exportedAt\": \"" + DateTime.UtcNow.ToString("O") + "\",");
            json.AppendLine("  \"files\": [");

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                json.AppendLine("    {");
                json.AppendLine("      \"component\": \"" + EscapeJson(record.Component) + "\",");
                json.AppendLine("      \"drawableId\": \"" + EscapeJson(record.DrawableId) + "\",");
                json.AppendLine("      \"textureId\": \"" + EscapeJson(record.TextureId) + "\",");
                json.AppendLine("      \"drawableName\": \"" + EscapeJson(record.DrawableName) + "\",");
                json.AppendLine("      \"textureName\": \"" + EscapeJson(record.TextureName) + "\",");
                json.AppendLine("      \"path\": \"" + EscapeJson(record.RelativePath) + "\"");
                json.Append("    }");
                if (i < records.Count - 1) json.Append(",");
                json.AppendLine();
            }

            json.AppendLine("  ]");
            json.AppendLine("}");
            File.WriteAllText(manifestPath, json.ToString(), Encoding.UTF8);
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

        private void ExportCurrentPreviewPngStable(string filePath)
        {
            var selectedDrawable = GetSelectedExportDrawable();
            ExportDrawablePreviewPngStable(filePath, selectedDrawable, GetSelectedExportTexture(selectedDrawable), null);
        }

        private void ExportDrawablePreviewPngStable(string filePath, Drawable selectedDrawable, TextureDictionary selectedTexture, Texture textureOverride)
        {
            if (Renderer == null || Renderer.DXMan == null || Renderer.DXMan.device == null || Renderer.DXMan.context == null || Renderer.DXMan.backbuffer == null)
            {
                throw new InvalidOperationException("The 3D preview is not ready yet.");
            }

            if (selectedDrawable == null)
            {
                throw new InvalidOperationException("No selected drawable is available for export.");
            }

            var selectedPedComponentIndex = GetComponentIndexFromDrawableName(selectedDrawable.Name);
            var forceSelectedDrawableIntoComponent = selectedPedComponentIndex >= 0;
            if (selectedPedComponentIndex < 0)
            {
                selectedPedComponentIndex = GetSelectedPedComponentIndex(selectedDrawable);
            }
            if (selectedPedComponentIndex < 0 && selectedTexture == null && liveTexturePath == null && textureOverride == null)
            {
                throw new InvalidOperationException("No texture is available for the selected drawable.");
            }

            var dxman = Renderer.DXMan;
            var device = dxman.device;
            var context = dxman.context;
            var backbufferDesc = dxman.backbuffer.Description;
            var exportSize = ExportImageSize;
            Texture2D exportTexture = null;
            Texture2D depthTexture = null;
            Texture2D stagingTexture = null;
            RenderTargetView exportTargetView = null;
            DepthStencilView exportDepthView = null;

            lock (Renderer.RenderSyncRoot)
            {
                if (backbufferDesc.Width <= 0 || backbufferDesc.Height <= 0)
                {
                    throw new InvalidOperationException("The 3D preview backbuffer has no image data.");
                }

                var cameraState = CaptureCameraState();
                PedComponentExportState componentExportState = null;
                try
                {
                    if (forceSelectedDrawableIntoComponent)
                    {
                        componentExportState = ApplyTemporaryExportPedComponent(selectedPedComponentIndex, selectedDrawable, selectedTexture);
                    }

                    var previewTexture = textureOverride ?? SelectPreviewTexture(selectedTexture);
                    if (previewTexture != null && selectedPedComponentIndex >= 0)
                    {
                        SelectedPed.Textures[selectedPedComponentIndex] = previewTexture;
                    }

                    Renderer.RenderableCache.ContentThreadProc();
                    Renderer.RenderableCache.RenderThreadSync();

                    var exportDesc = new Texture2DDescription
                    {
                        Width = exportSize,
                        Height = exportSize,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = SharpDX.DXGI.Format.R8G8B8A8_UNorm,
                        SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None
                    };
                    exportTexture = new Texture2D(device, exportDesc);
                    exportTargetView = new RenderTargetView(device, exportTexture);

                    depthTexture = new Texture2D(device, new Texture2DDescription
                    {
                        Width = exportSize,
                        Height = exportSize,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = SharpDX.DXGI.Format.D32_Float,
                        SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.DepthStencil,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None
                    });
                    exportDepthView = new DepthStencilView(device, depthTexture);

                    var viewport = new ViewportF(0.0f, 0.0f, exportSize, exportSize, 0.0f, 1.0f);
                    dxman.SetRenderTargetOverride(exportTargetView, exportDepthView, viewport);

                    var exportDrawable = forceSelectedDrawableIntoComponent
                        ? selectedDrawable
                        : (selectedPedComponentIndex >= 0 ? SelectedPed.Drawables[selectedPedComponentIndex] : selectedDrawable);
                    FrameStableExportCamera(exportDrawable ?? selectedDrawable, exportSize, selectedPedComponentIndex);

                    Renderer.BeginRender(context);
                    context.ClearRenderTargetView(exportTargetView, new Color(0, 0, 0, 0));
                    context.ClearDepthStencilView(exportDepthView, DepthStencilClearFlags.Depth, 0.0f, 0);

                    if (!RenderBatchExportItemWithRetry(selectedDrawable, selectedTexture, selectedPedComponentIndex, previewTexture))
                    {
                        throw new InvalidOperationException("The selected drawable is not ready to render yet. Wait for it to appear in the preview, then export again.");
                    }

                    Renderer.RenderQueued();
                    Renderer.RenderFinalPass();
                    Renderer.EndRender();

                    var stagingDesc = exportDesc;
                    stagingDesc.BindFlags = BindFlags.None;
                    stagingDesc.CpuAccessFlags = CpuAccessFlags.Read;
                    stagingDesc.Usage = ResourceUsage.Staging;

                    stagingTexture = new Texture2D(device, stagingDesc);
                    context.CopyResource(exportTexture, stagingTexture);
                    SaveTextureToPng(context, stagingTexture, filePath, depthTexture, selectedPedComponentIndex);
                }
                finally
                {
                    dxman.ClearRenderTargetOverride();
                    RestoreCameraState(cameraState);
                    if (componentExportState != null) componentExportState.Restore(SelectedPed);
                    if (stagingTexture != null) stagingTexture.Dispose();
                    if (exportDepthView != null) exportDepthView.Dispose();
                    if (depthTexture != null) depthTexture.Dispose();
                    if (exportTargetView != null) exportTargetView.Dispose();
                    if (exportTexture != null) exportTexture.Dispose();
                }
            }
        }

        private bool RenderBatchExportItemWithRetry(Drawable selectedDrawable, TextureDictionary selectedTexture, int selectedPedComponentIndex, Texture previewTexture)
        {
            for (int i = 0; i < 8; i++)
            {
                if (RenderBatchExportItem(selectedDrawable, selectedTexture, selectedPedComponentIndex, previewTexture))
                {
                    return true;
                }

                Renderer.RenderableCache.ContentThreadProc();
                Renderer.RenderableCache.RenderThreadSync();
            }

            return false;
        }

        private bool RenderBatchExportItem(Drawable selectedDrawable, TextureDictionary selectedTexture, int selectedPedComponentIndex, Texture previewTexture)
        {
            if (selectedPedComponentIndex >= 0)
            {
                return RenderBatchPedComponent(selectedPedComponentIndex, selectedTexture, previewTexture, selectedDrawable);
            }

            var texture = previewTexture ?? SelectPreviewTexture(selectedTexture);
            if (texture == null) return false;

            selectedDrawable.Owner = SelectedPed;
            if (selectedDrawable.Skeleton == null || selectedDrawable.Skeleton.Bones == null || selectedDrawable.Skeleton.Bones.Items.Length == 0)
            {
                selectedDrawable.Skeleton = SelectedPed.Skeleton.Clone();
            }

            var isProp = selectedDrawable.Name.StartsWith("p_");
            return Renderer.RenderDrawable(selectedDrawable, null, SelectedPed.RenderEntity, 0, selectedTexture, texture, SelectedPed.AnimClip, null, null, isProp, true);
        }

        private bool RenderBatchPedComponent(int componentIndex, TextureDictionary textureDictionary, Texture texture, Drawable drawableOverride)
        {
            var selectedPedDrawable = SelectedPed.Drawables[componentIndex];
            var drawable = drawableOverride ?? selectedPedDrawable;
            if (drawable == null) return false;

            texture = texture ?? SelectPreviewTexture(textureDictionary) ?? SelectedPed.Textures[componentIndex];
            if (texture == null) return false;

            var ac = SelectedPed.AnimClip;
            if (ac != null)
            {
                ac.EnableRootMotion = SelectedPed.EnableRootMotion;
            }

            if (SelectedPed.Skeleton != null)
            {
                if (drawable.Skeleton == null)
                {
                    drawable.Skeleton = SelectedPed.Skeleton;
                }
                else if (drawable.Skeleton != SelectedPed.Skeleton)
                {
                    var dskel = drawable.Skeleton;
                    if (SelectedPed.Skeleton.Bones?.Items != null)
                    {
                        for (int b = 0; b < SelectedPed.Skeleton.Bones.Items.Length; b++)
                        {
                            var srcbone = SelectedPed.Skeleton.Bones.Items[b];
                            var dstbone = srcbone;
                            if (dskel.BonesMap.TryGetValue(srcbone.Tag, out dstbone))
                            {
                                if (srcbone == dstbone) break;
                                dskel.Bones.Items[dstbone.Index] = srcbone;
                                dskel.BonesMap[srcbone.Tag] = srcbone;
                            }
                        }
                        dskel.BonesSorted = SelectedPed.Skeleton.BonesSorted;
                    }
                }
            }

            var isProp = drawable.Name.StartsWith("p_");
            return Renderer.RenderDrawable(drawable, null, SelectedPed.RenderEntity, 0, textureDictionary, texture, ac, null, null, isProp, true);
        }

        private void FrameStableExportCamera(Drawable drawable, int exportSize, int selectedPedComponentIndex)
        {
            var bounds = GetDrawableExportBounds(drawable);
            var center = bounds.Center;
            var radius = Math.Max(GetStableMinimumExportRadius(selectedPedComponentIndex), bounds.Radius);
            var cameraPadding = GetStableExportCameraPadding(selectedPedComponentIndex);
            var distance = (float)(radius / Math.Tan(camera.FieldOfView * 0.5f)) * cameraPadding;

            if (selectedPedComponentIndex == LowrComponentIndex)
            {
                center.Z += StableLowrCenterZOffset;
            }

            camera.OnWindowResize(exportSize, exportSize);
            camera.FollowEntity.Position = center;
            camera.TargetDistance = distance;
            camera.CurrentDistance = distance;

            var exportRotation = new Vector3((float)Math.PI, 0.2f, 0.0f);
            camera.TargetRotation = exportRotation;
            camera.CurrentRotation = exportRotation;
            camera.UpdateProj = true;
            camera.Update(0.0f);
        }

        private static float GetStableMinimumExportRadius(int selectedPedComponentIndex)
        {
            if (selectedPedComponentIndex == LowrComponentIndex) return StableLowrMinimumExportRadius;
            if (selectedPedComponentIndex == UpprComponentIndex || selectedPedComponentIndex == JbibComponentIndex) return StableTorsoMinimumExportRadius;
            return StableDefaultMinimumExportRadius;
        }

        private static float GetStableExportCameraPadding(int selectedPedComponentIndex)
        {
            if (selectedPedComponentIndex == LowrComponentIndex) return StableLowrExportCameraPadding;
            if (selectedPedComponentIndex == UpprComponentIndex || selectedPedComponentIndex == JbibComponentIndex) return StableTorsoExportCameraPadding;
            return StableDefaultExportCameraPadding;
        }

        private class BatchExportProgressForm : Form
        {
            private readonly Label statusLabel;
            private readonly Label countLabel;
            private readonly ProgressBar progressBar;
            private readonly Button cancelButton;

            public bool CancelRequested { get; private set; }

            public BatchExportProgressForm()
            {
                Text = "Export All PNG";
                Width = 520;
                Height = 150;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterParent;

                statusLabel = new Label { Left = 12, Top = 12, Width = 480, Height = 22, Text = "Preparing..." };
                countLabel = new Label { Left = 12, Top = 38, Width = 480, Height = 22, Text = "0%" };
                progressBar = new ProgressBar { Left = 12, Top = 66, Width = 480, Height = 20, Minimum = 0, Maximum = 100 };
                cancelButton = new Button { Left = 392, Top = 92, Width = 100, Height = 26, Text = "Cancel" };
                cancelButton.Click += (s, e) => { CancelRequested = true; cancelButton.Enabled = false; cancelButton.Text = "Cancelling..."; };

                Controls.Add(statusLabel);
                Controls.Add(countLabel);
                Controls.Add(progressBar);
                Controls.Add(cancelButton);
            }

            public void SetStatus(string status, int processed, int total, int exported, int failed)
            {
                if (IsDisposed) return;
                total = Math.Max(1, total);
                var percent = Math.Max(0, Math.Min(100, (int)Math.Round(processed * 100.0 / total)));
                statusLabel.Text = status;
                countLabel.Text = $"{percent}%  |  {processed}/{total} processed  |  {exported} exported  |  {failed} failed";
                progressBar.Value = percent;
                Refresh();
            }
        }

        private class BatchExportItem
        {
            public Drawable Drawable;
            public List<TextureDictionary> TextureDictionaries = new List<TextureDictionary>();
        }

        private class BatchExportRecord
        {
            public string Component;
            public string DrawableId;
            public string TextureId;
            public string DrawableName;
            public string TextureName;
            public string RelativePath;
        }

        private class BatchExportResult
        {
            public int ExportedCount;
            public int FailedCount;
        }
    }
}
