# PNG Export Investigation

## Summary

The `Export PNG` preview feature is not a WPF `RenderTargetBitmap`, `PngBitmapEncoder`, `Viewport3D`, or HelixToolkit screenshot. The active export system is a WinForms/SharpDX/CodeWalker offscreen DirectX render inside `CodeWalker/CodeWalker/CustomPedsForm.cs`.

The export button renders the selected drawable or selected ped component into a fixed `768x768` DirectX render target, reads that texture back through a staging texture, derives transparency from depth or rendered pixels, fits visible pixels into a square, then writes PNG through SharpDX WIC.

Texture export menus in the WPF app are separate and unrelated. They use ImageMagick to convert texture files (`.ytd`, `.dds`, `.png`, `.jpg`) and do not capture the 3D preview.

## Search Findings

No relevant WPF screenshot exporter was found:

- No `RenderTargetBitmap` usage in the solution.
- No WPF `PngBitmapEncoder` screenshot path in the WPF app.
- No `Viewport3D`, `HelixViewport3D`, `PerspectiveCamera`, `OrthographicCamera`, `ZoomExtents`, or HelixToolkit screenshot path.

Relevant export/capture systems found:

- 3D preview PNG export: `CodeWalker/CodeWalker/CustomPedsForm.cs`
- DirectX render target override support: `CodeWalker/CodeWalker/Rendering/DirectX/DXManager.cs`
- CodeWalker renderer and camera: `CodeWalker/CodeWalker/Rendering/Renderer.cs`, `CodeWalker/CodeWalker.Core/World/Camera.cs`
- WPF texture export, unrelated to screenshot framing: `grzyClothTool/Controls/Drawable/SelectedDrawable.xaml.cs`, `grzyClothTool/Controls/Drawable/DrawableList.xaml.cs`, `grzyClothTool/Helpers/FileHelper.cs`, `grzyClothTool/Helpers/ImgHelper.cs`

## Full Call Chain: Export PNG Button To PNG File

1. `CustomPedsForm` constructor calls `AddExportPreviewButton();`
   - File: `CodeWalker/CodeWalker/CustomPedsForm.cs`
   - Relevant line: `166`

2. `AddExportPreviewButton()`
   - Signature: `private void AddExportPreviewButton()`
   - File: `CodeWalker/CodeWalker/CustomPedsForm.cs`
   - Lines: `210-234`
   - Creates a WinForms button with text `Export PNG`.
   - Trigger line: `230`
     ```csharp
     exportPreviewButton.Click += ExportPreviewButton_Click;
     ```

3. `ExportPreviewButton_Click(object sender, EventArgs e)`
   - Signature: `private void ExportPreviewButton_Click(object sender, EventArgs e)`
   - File: `CodeWalker/CodeWalker/CustomPedsForm.cs`
   - Lines: `236-263`
   - Opens `SaveFileDialog`.
   - PNG filter/default path lines: `240-245`
   - Calls export line: `254`
     ```csharp
     ExportCurrentPreviewPng(saveFileDialog.FileName);
     ```

4. `ExportCurrentPreviewPng(string filePath)`
   - Signature: `private void ExportCurrentPreviewPng(string filePath)`
   - File: `CodeWalker/CodeWalker/CustomPedsForm.cs`
   - Lines: `265-407`
   - Validates DirectX renderer/backbuffer.
   - Gets selected drawable and texture.
   - Resolves component type from drawable name.
   - Creates offscreen color/depth textures.
   - Overrides the DirectX render target.
   - Frames the camera.
   - Renders only the selected export item.
   - Copies the render target to a staging texture.
   - Calls `SaveTextureToPng(...)`.

5. `SaveTextureToPng(...)`
   - Signature: `private static void SaveTextureToPng(DeviceContext context, Texture2D texture, string filePath, Texture2D depthTexture = null, int selectedPedComponentIndex = -1)`
   - File: `CodeWalker/CodeWalker/CustomPedsForm.cs`
   - Lines: `798-846`
   - Reads GPU texture bytes.
   - Applies alpha.
   - Fits visible pixels to square.
   - Encodes PNG via SharpDX WIC.
   - Final disk write starts at line `828`:
     ```csharp
     using (var outputStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
     ```

## Relevant Files And Methods

### `CodeWalker/CodeWalker/CustomPedsForm.cs`

#### `private void AddExportPreviewButton()`

Lines: `210-234`

What it does:

- Checks `ToolsPanel`.
- Avoids duplicate `ExportPreviewButton`.
- Creates a WinForms `Button`.
- Sets the text to `Export PNG`.
- Hooks click to `ExportPreviewButton_Click`.
- Adds it to the tools panel.

Trigger:

- Called by the `CustomPedsForm` constructor after `InitializeComponent();`.

Image size:

- None.

Camera:

- None.

Async/wait:

- None.

Component handling:

- None.

#### `private void ExportPreviewButton_Click(object sender, EventArgs e)`

Lines: `236-263`

What it does:

- Opens `SaveFileDialog`.
- Uses `preview.png` as default filename.
- Calls `ExportCurrentPreviewPng(saveFileDialog.FileName)`.
- Logs/shows export errors.

Trigger:

- Button click from `Export PNG`.

Image size:

- None directly.

Camera:

- None directly.

Async/wait:

- None.

Component handling:

- None directly.

#### `private void ExportCurrentPreviewPng(string filePath)`

Lines: `265-407`

What it does step by step:

- Lines `267-270`: validates DirectX renderer, device, context, and backbuffer.
- Lines `272-287`: gets selected drawable/texture and resolves component index.
- Lines `290-294`: captures DirectX objects and sets export size.
- Lines `301-306`: locks render sync and validates backbuffer dimensions.
- Line `308`: captures current camera state for restore.
- Lines `312-315`: temporarily forces known ped components into the selected component slot.
- Lines `317-329`: creates the offscreen color render target description.
- Lines `333-345`: creates the offscreen depth texture.
- Lines `348-349`: sets a square viewport and render target override.
- Lines `351-354`: chooses export drawable and frames the export camera.
- Lines `355-357`: begins render and clears color/depth.
- Lines `359-362`: renders selected item with retry.
- Lines `364-366`: renders queued shaders/final pass.
- Lines `368-375`: copies to staging texture and saves PNG.
- Lines `379-404`: restores render target, camera, ped state, and disposes GPU objects.

What triggers it:

- `ExportPreviewButton_Click`.

Image size/dimensions:

- Line `294`:
  ```csharp
  var exportSize = ExportImageSize;
  ```
- Constant line `112`:
  ```csharp
  private const int ExportImageSize = 768;
  ```
- Color render target lines `317-320`:
  ```csharp
  Width = exportSize,
  Height = exportSize,
  ```
- Depth texture lines `333-336`:
  ```csharp
  Width = exportSize,
  Height = exportSize,
  ```
- Viewport line `348`:
  ```csharp
  var viewport = new ViewportF(0.0f, 0.0f, exportSize, exportSize, 0.0f, 1.0f);
  ```

Camera position/zoom/angle before capture:

- Line `308`: camera state is saved.
- Line `354`: calls `FrameExportCamera(exportDrawable ?? selectedDrawable, exportSize, selectedPedComponentIndex);`
- Lines `379-380`: render target and camera are restored afterward.

Async/wait:

- No `await`, `Task.Delay`, or `Thread.Sleep` in this export method.
- It uses a synchronous lock on `Renderer.RenderSyncRoot` at line `301`.

Camera reset/reposition:

- The camera is repositioned for export by `FrameExportCamera`.
- The original camera state is restored afterward.
- Export does not call the UI's full `SetDefaultCameraPosition()`.

Component differences:

- Lines `279-283`: component index is resolved from drawable name first, then ped slot.
- Lines `312-315`: known component names are temporarily applied to `SelectedPed`.
- Lines `351-354`: if forced into a component, the selected drawable is used for framing.

#### `private bool RenderSelectedExportItemWithRetry(...)`

Signature:

```csharp
private bool RenderSelectedExportItemWithRetry(Drawable selectedDrawable, TextureDictionary selectedTexture, int selectedPedComponentIndex)
```

Lines: `409-423`

What it does:

- Tries to render up to 8 times.
- If render fails, services the renderable cache:
  ```csharp
  Renderer.RenderableCache.ContentThreadProc();
  Renderer.RenderableCache.RenderThreadSync();
  ```

Trigger:

- Called by `ExportCurrentPreviewPng` line `359`.

Async/wait:

- No sleep or timed delay.
- There is no `await`.
- Retry is immediate and cache-driven.

Why it matters:

- This is the only export-specific timing mitigation.
- It handles drawables not ready in GPU cache, but it does not wait for camera animation over time.

#### `private static int GetComponentIndexFromDrawableName(string name)`

Lines: `448-491`

What it does:

- Maps freemode drawable name prefixes to GTA ped component indices.

Component branches:

```csharp
"head", // 0
"berd", // 1
"hair", // 2
"uppr", // 3
"lowr", // 4
"hand", // 5
"feet", // 6
"teef", // 7
"accs", // 8
"task", // 9
"decl", // 10
"jbib"  // 11
```

Notes:

- `p_head` and `p_eyes` are explicitly excluded from this ped-component path.
- This means shoes/feet use component `6`, accessories use `8`, uppers use `3`, pants/lower use `4`, and jbib overlays use `11`.

#### `private PedComponentExportState ApplyTemporaryExportPedComponent(...)`

Signature:

```csharp
private PedComponentExportState ApplyTemporaryExportPedComponent(int componentIndex, Drawable selectedDrawable, TextureDictionary fallbackTextureDictionary)
```

Lines: `493-524`

What it does:

- Saves the original ped component state.
- Attempts to set the selected drawable/texture through `SelectedPed.SetComponentDrawable`.
- Forces `SelectedPed.Drawables[componentIndex] = selectedDrawable`.
- Forces fallback texture if available.
- Writes the selected drawable name.
- Clears stale cloth/expression data if component lookup did not really load the selected drawable.

Trigger:

- Called by `ExportCurrentPreviewPng` lines `312-315`.

Camera/image size:

- None.

Component differences:

- Only used when drawable name maps to a component prefix.
- This is critical for `uppr`, `lowr`, `feet`, `accs`, and `jbib`.

#### `private bool RenderSelectedExportItem(...)`

Signature:

```csharp
private bool RenderSelectedExportItem(Drawable selectedDrawable, TextureDictionary selectedTexture, int selectedPedComponentIndex)
```

Lines: `555-563`

What it does:

- If a component index exists, renders through `RenderSelectedPedComponent`.
- Otherwise renders the raw selected drawable through `RenderSelectedItem`.

Component branch:

```csharp
if (selectedPedComponentIndex >= 0)
{
    return RenderSelectedPedComponent(selectedPedComponentIndex, selectedTexture);
}
```

#### `private bool RenderSelectedPedComponent(...)`

Signature:

```csharp
private bool RenderSelectedPedComponent(int componentIndex, TextureDictionary fallbackTextureDictionary, Drawable drawableOverride = null)
```

Lines: `565-630`

What it does:

- Gets `SelectedPed.Drawables[componentIndex]`.
- Picks a texture from the ped slot or fallback texture dictionary.
- Ensures animation root motion setting.
- Adapts drawable skeleton to the selected ped skeleton.
- Calls `Renderer.RenderDrawable(...)`.

Trigger:

- Called by `RenderSelectedExportItem`.

Camera/image size:

- None directly.

Component differences:

- Uses component-specific `SelectedPed.Clothes[componentIndex]` and `SelectedPed.Expressions[componentIndex]` at lines `626-627`.

#### `private CameraExportState CaptureCameraState()`

Lines: `676-690`

What it does:

- Saves follow position, distance, rotation, width, height, aspect ratio, and projection-update state.

Camera state saved:

- Lines `680-688`.

#### `private void RestoreCameraState(CameraExportState state)`

Lines: `692-705`

What it does:

- Restores the camera after export.
- Calls `camera.Update(0.0f)` at line `703`.

#### `private void FrameExportCamera(...)`

Signature:

```csharp
private void FrameExportCamera(Drawable drawable, int exportSize, int selectedPedComponentIndex)
```

Lines: `707-721`

What it does:

- Computes export bounds.
- Computes camera distance from bounds radius and field of view.
- Applies component-specific camera padding.
- Resizes camera to square export viewport.
- Moves camera follow target to drawable bounds center.
- Sets target/current distance.
- Keeps current camera rotation.
- Updates projection immediately.

Camera size/dimensions:

- Line `714`:
  ```csharp
  camera.OnWindowResize(exportSize, exportSize);
  ```

Camera position/zoom:

- Line `715`:
  ```csharp
  camera.FollowEntity.Position = bounds.Center;
  ```
- Lines `716-717`:
  ```csharp
  camera.TargetDistance = distance;
  camera.CurrentDistance = distance;
  ```

Camera angle:

- Line `718`:
  ```csharp
  camera.TargetRotation = camera.CurrentRotation;
  ```

Important:

- This does not reset to a deterministic export angle.
- It preserves whatever current preview rotation is active.
- Tall items (`uppr`, `lowr`, `jbib`) are more sensitive to angle/framing than shoes/accessories.

#### `private static DrawableExportBounds GetDrawableExportBounds(Drawable drawable)`

Lines: `723-743`

What it does:

- Prefers `drawable.BoundingBoxMin` / `drawable.BoundingBoxMax`.
- Computes center and diagonal radius from the box.
- Falls back to `drawable.BoundingCenter` / `drawable.BoundingSphereRadius` if bounds are invalid.

Bug relevance:

- Bad/tight bounding sphere values can cause incorrect zoom.
- Using box bounds is safer for tall clothing pieces.

#### `private static float GetExportCameraPadding(int selectedPedComponentIndex)`

Lines: `771-783`

Component-specific camera padding:

- `lowr` / component `4`: `LowrExportCameraPadding = 1.65f`
- `uppr` / component `3` and `jbib` / component `11`: `TorsoExportCameraPadding = 1.75f`
- Other components: `DefaultExportCameraPadding = 1.35f`

Constants:

- Lines `120-122`.

#### `private static void SaveTextureToPng(...)`

Lines: `798-846`

What it does:

- Maps staging texture.
- Reads pixels row-by-row.
- Applies alpha from depth or RGB fallback.
- Calls `FitVisiblePixelsToSquare`.
- Encodes PNG through SharpDX WIC.
- Writes to disk.

Image dimensions:

- Reads `textureDesc.Width` / `textureDesc.Height` at lines `801`, `806-807`.
- WIC bitmap uses these same dimensions at lines `824-825`.
- PNG frame size uses them at line `833`.

PNG write:

- Lines `828-838`.

#### `private static void FitVisiblePixelsToSquare(...)`

Signature:

```csharp
private static void FitVisiblePixelsToSquare(byte[] pixels, int width, int height, int selectedPedComponentIndex)
```

Lines: `914-972`

What it does:

- Finds alpha/color-visible pixel bounds.
- Scales visible content to fit the square image.
- Centers it.
- Applies small lowr vertical offset.
- Resamples with bilinear sampling.

Image size:

- Uses passed `width` / `height` from the render target.

Component-specific scaling:

- Line `916`: uses `GetVisibleFill`.
- Lines `941-944`: applies `LowrVerticalOffset` for component `4`.

#### `private static float GetVisibleFill(int selectedPedComponentIndex)`

Lines: `974-986`

Component-specific post-crop fill:

- `lowr` / component `4`: `LowrVisibleFill = 0.82f`
- `uppr` / component `3` and `jbib` / component `11`: `TorsoVisibleFill = 0.78f`
- Other components: `DefaultVisibleFill = 0.86f`

Constants:

- Lines `116-119`.

### `CodeWalker/CodeWalker/Rendering/DirectX/DXManager.cs`

#### Render target fields

Lines: `23-42`

Relevant fields:

- `device`
- `context`
- `backbuffer`
- `targetview`
- `depthview`
- `targetviewOverride`
- `depthviewOverride`
- `viewportOverride`

#### `public void SetDefaultRenderTarget(DeviceContext ctx)`

Lines: `358-363`

What it does:

- Binds either override render targets or normal backbuffer targets.
- Applies either override viewport or normal viewport.

Bug relevance:

- This is what lets `CustomPedsForm` render into the offscreen `768x768` export target.

#### `public void SetRenderTargetOverride(...)`

Signature:

```csharp
public void SetRenderTargetOverride(RenderTargetView targetView, DepthStencilView depthView, ViewportF viewport)
```

Lines: `365-370`

What it does:

- Stores export render target/depth/viewport overrides.

#### `public void ClearRenderTargetOverride()`

Lines: `372-377`

What it does:

- Clears offscreen export override after capture.

### `CodeWalker/CodeWalker/Rendering/Renderer.cs`

#### `public void BeginRender(DeviceContext ctx)`

Lines: `273-305`

What it does:

- Clears current render target via `dxman.ClearRenderTarget(context)`.
- Begins shader frame.
- Clears render selection state.
- Clears rendered drawables lists.

#### `public void RenderQueued()`

Lines: `316-324`

What it does:

- Flushes queued shader render work through the active camera.

#### `public void RenderFinalPass()`

Lines: `326-331`

What it does:

- Runs final shader pass.

#### `public void EndRender()`

Lines: `333-335`

What it does:

- Calls render cache sync at line `335`.

#### `private void RenderPedComponent(Ped ped, int i)`

Lines: `1871-1920`

What it does:

- Draws standard ped components by slot.
- Uses ped drawable, texture, cloth, expression.
- This is the normal live preview path, not necessarily the export-only forced component path.

### `CodeWalker/CodeWalker.Core/World/Camera.cs`

#### Camera state fields

Lines: `12-32`

Relevant fields:

- `TargetRotation`
- `CurrentRotation`
- `TargetDistance`
- `CurrentDistance`
- `ZoomCurrentTime`
- `ZoomTargetTime`
- `ZoomSpeed`
- `Width`
- `Height`
- `FieldOfView`
- `AspectRatio`

#### `private void UpdateFollow(float elapsed)`

Lines: `93-170`

What it does:

- Smoothly moves `CurrentRotation` toward `TargetRotation`.
- Smoothly moves `CurrentDistance` toward `TargetDistance`.
- Computes camera position from rotation, distance, and follow target.

Timing relevance:

- Normal preview camera is smoothed over elapsed time.
- Export bypasses most smoothing by setting current and target distance equal and calling `camera.Update(0.0f)`.
- Export does not set a deterministic rotation; it keeps current rotation.

#### `public void MouseZoom(int z)` and `public void ControllerZoom(float z)`

Lines: `261-288`

What they do:

- Change `TargetDistance` and orthographic target size.

Bug relevance:

- User zoom affects normal preview camera.
- Export currently resets distance but keeps rotation.

### WPF Texture Export: Not The Screenshot System

These methods export texture image files, not 3D preview screenshots.

#### `grzyClothTool/Controls/Drawable/SelectedDrawable.xaml.cs`

Method:

```csharp
private async void ExportTexture_Click(object sender, RoutedEventArgs e)
```

Lines: `1066-1106`

What it does:

- Opens a folder picker.
- Calls `FileHelper.SaveTexturesAsync(...)` on a background task.

No 3D camera.

#### `grzyClothTool/Controls/Drawable/DrawableList.xaml.cs`

Method:

```csharp
private async void ExportDrawable_Click(object sender, RoutedEventArgs e)
```

Lines: `562-608`

What it does:

- Exports selected drawable files or textures.
- For `PNG` and `DDS`, calls `FileHelper.SaveTexturesAsync(...)`.

No 3D camera.

#### `grzyClothTool/Helpers/FileHelper.cs`

Method:

```csharp
public static async Task SaveTexturesAsync(List<GTexture> textures, string folderPath, string format)
```

Lines: `465-534`

What it does:

- Creates output folder.
- Chooses `.dds`, `.png`, or `.ytd`.
- Loads texture with `ImgHelper.GetImage`.
- Converts through ImageMagick.
- Writes file with `File.WriteAllBytesAsync`.

PNG write:

- Line `520`.

No 3D camera.

#### `grzyClothTool/Helpers/ImgHelper.cs`

Method:

```csharp
public static MagickImage GetImage(string path)
```

Lines: `17-40`

What it does:

- Reads `.ytd` texture through CodeWalker or reads image directly through ImageMagick.

Method:

```csharp
public static byte[] GetDDSBytes(GTexture gtxt)
```

Lines: `130-158`

What it does:

- Converts `.png` / `.jpg` / `.dds` texture input to DDS/YTD texture bytes.

No 3D camera.

## How Preview Selection Reaches CodeWalker

### `grzyClothTool/Controls/PreviewWindowHost.xaml.cs`

#### `public void InitializePreview()`

Lines: `54-93`

What it does:

- Creates `CustomPedsForm`.
- Embeds it inside WPF through `WindowsFormsHost`.
- Shows it.

#### `public void UpdateDrawables(...)`

Signature:

```csharp
public void UpdateDrawables(ObservableCollection<GDrawable> selectedDrawables, GTexture selectedTexture, Dictionary<string, string> updateDict)
```

Lines: `163-226`

What it does:

- Converts selected WPF drawable model to CodeWalker `YddFile`.
- Stores first drawable in `_customPedsForm.LoadedDrawables`.
- Converts selected texture to `YtdFile`.
- Calls `_customPedsForm.UpdateSelectedDrawable(...)`.

No PNG write.

### `grzyClothTool/Helpers/CWHelper.cs`

#### `public static void SendDrawableUpdateToPreview(EventArgs args)`

Lines: `103-132`

What it does:

- Gets selected drawables from the WPF addon manager.
- Sends them to `DockedPreviewHost.UpdateDrawables(...)`.

#### `public static void OpenDrawableInPreview(GDrawable drawable)`

Lines: `134-161`

What it does:

- Opens the preview pane.
- Initializes the preview.
- Sends selected drawable to preview.

## Bug Analysis

The cropping problem is not caused by WPF viewport clipping. The final preview PNG is created from a DirectX offscreen render target.

The important difference between tall components and small components:

- Shoes/feet and accessories are compact; even if the export angle or bounds are imperfect, they usually remain visible.
- Uppers, lowr/pants, and jbib overlays are taller and more likely to exceed the camera frame if the camera is too close, the bounds are too small, or the angle is unfavorable.

Current code already contains several mitigations:

- Fixed export size: `ExportImageSize = 768` at line `112`.
- Component name mapping: lines `448-491`.
- Forced temporary component assignment: lines `493-524`.
- Component-specific camera padding: lines `771-783`.
- Component-specific square fit: lines `974-986`.
- Bounds-box based camera framing: lines `723-743`.

Remaining camera-state risk:

- Export resets target/current distance at lines `716-717`.
- Export does not reset to a deterministic camera rotation.
- Line `718` keeps the current camera rotation:
  ```csharp
  camera.TargetRotation = camera.CurrentRotation;
  ```

That means export framing can still vary depending on the user's current preview angle. This matches the intermittent symptom better than a pure image-size issue: sometimes `uppr`/`jbib`/pants export fine, sometimes they are too close or badly framed, while small shoes/accessories are less affected.

Historical bug source in this export code:

- The old export camera logic used only `drawable.BoundingSphereRadius` and `drawable.BoundingCenter`.
- Tall clothing drawables can have tight or misleading sphere bounds.
- That made `FrameExportCamera` put the camera too close or target the wrong center before capture.

Current working-tree fix:

- `FrameExportCamera` now prefers the drawable bounding box (`BoundingBoxMin` / `BoundingBoxMax`) at lines `723-735`.
- It falls back to sphere bounds only if the box is invalid at lines `738-742`.

## Exact Lines Responsible For Current Residual Risk

If the bug still reproduces after the bounds fix, the most suspicious current line is:

- `CodeWalker/CodeWalker/CustomPedsForm.cs:718`
  ```csharp
  camera.TargetRotation = camera.CurrentRotation;
  ```

Reason:

- This preserves user/live-preview rotation.
- It does not enforce a stable export angle per component.
- Tall components are more sensitive to this than shoes/accessories.

There is no export-time `await`, `Task.Delay`, or `Thread.Sleep` that waits for camera animation to settle. The retry loop at lines `409-423` only warms GPU renderables; it does not animate or stabilize camera rotation.

## Proposed Fix

Make export camera rotation deterministic for the screenshot, while still restoring the user's original camera state afterward through `RestoreCameraState`.

In `FrameExportCamera`, replace:

```csharp
camera.TargetRotation = camera.CurrentRotation;
camera.UpdateProj = true;
camera.Update(0.0f);
```

with:

```csharp
var exportRotation = new Vector3((float)Math.PI, 0.2f, 0.0f);
camera.TargetRotation = exportRotation;
camera.CurrentRotation = exportRotation;
camera.UpdateProj = true;
camera.Update(0.0f);
```

Why:

- `SetDefaultCameraPosition()` already uses this front-facing angle at lines `2666-2669`.
- Export already saves/restores the user's camera in `CaptureCameraState` / `RestoreCameraState`.
- This removes preview-angle dependence from PNG export.
- It should make `uppr`, `lowr`, and `jbib` frame consistently like shoes/accessories.

Optional component-specific refinement:

If tall components still need more margin, increase:

```csharp
private const float TorsoExportCameraPadding = 1.75f;
private const float LowrExportCameraPadding = 1.65f;
```

to:

```csharp
private const float TorsoExportCameraPadding = 1.95f;
private const float LowrExportCameraPadding = 1.85f;
```

The deterministic rotation is the safer first change because it fixes intermittent framing without increasing whitespace for every export.

## Verification Plan

1. Build:
   ```powershell
   dotnet build -v minimal
   ```

2. Run app and open 3D Preview.

3. Test exports with the preview camera rotated/zoomed differently before each export:
   - `uppr`
   - `lowr`
   - `jbib`
   - `feet`
   - `accs`

4. Expected result after deterministic camera rotation:
   - All exports remain `768x768`.
   - Background remains transparent.
   - `uppr`, `lowr`, and `jbib` no longer change framing based on the current preview camera angle.
   - Shoes/accessories should remain correct.

