using System;
using System.IO;
using System.Linq;
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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ReplaceExportButtonForStableLowrCapture();
        }

        private void ReplaceExportButtonForStableLowrCapture()
        {
            if (ToolsPanel == null)
            {
                return;
            }

            var oldButton = ToolsPanel.Controls.Find("ExportPreviewButton", false).OfType<Button>().FirstOrDefault();
            if (oldButton != null && (oldButton.Tag as string) == "StableLowrExport")
            {
                return;
            }

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

                if (saveFileDialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

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

        private void ExportCurrentPreviewPngStable(string filePath)
        {
            if (Renderer == null || Renderer.DXMan == null || Renderer.DXMan.device == null || Renderer.DXMan.context == null || Renderer.DXMan.backbuffer == null)
            {
                throw new InvalidOperationException("The 3D preview is not ready yet.");
            }

            var selectedDrawable = GetSelectedExportDrawable();
            var selectedTexture = GetSelectedExportTexture(selectedDrawable);
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
            if (selectedPedComponentIndex < 0 && selectedTexture == null && liveTexturePath == null)
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

                    if (!RenderSelectedExportItemWithRetry(selectedDrawable, selectedTexture, selectedPedComponentIndex))
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
                    if (componentExportState != null)
                    {
                        componentExportState.Restore(SelectedPed);
                    }
                    if (stagingTexture != null)
                    {
                        stagingTexture.Dispose();
                    }
                    if (exportDepthView != null)
                    {
                        exportDepthView.Dispose();
                    }
                    if (depthTexture != null)
                    {
                        depthTexture.Dispose();
                    }
                    if (exportTargetView != null)
                    {
                        exportTargetView.Dispose();
                    }
                    if (exportTexture != null)
                    {
                        exportTexture.Dispose();
                    }
                }
            }
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
            if (selectedPedComponentIndex == LowrComponentIndex)
            {
                return StableLowrMinimumExportRadius;
            }
            if (selectedPedComponentIndex == UpprComponentIndex || selectedPedComponentIndex == JbibComponentIndex)
            {
                return StableTorsoMinimumExportRadius;
            }

            return StableDefaultMinimumExportRadius;
        }

        private static float GetStableExportCameraPadding(int selectedPedComponentIndex)
        {
            if (selectedPedComponentIndex == LowrComponentIndex)
            {
                return StableLowrExportCameraPadding;
            }
            if (selectedPedComponentIndex == UpprComponentIndex || selectedPedComponentIndex == JbibComponentIndex)
            {
                return StableTorsoExportCameraPadding;
            }

            return StableDefaultExportCameraPadding;
        }
    }
}
