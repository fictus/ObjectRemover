# AI Object Remover for Paint.NET

An effect plugin for Paint.NET that removes unwanted objects from a photo. You paint over the object with a brush, and an AI inpainting model fills the area with a plausible background.

It appears in Paint.NET under **Effects > Photo > AI Object Remover**.

## What it does

- Lets you highlight an object with a resizable brush (zoom, pan, undo).
- Runs one of two AI models locally on your PC. Nothing is uploaded anywhere.
  - **LaMa**: larger and slower, usually better for big or textured areas.
  - **MI-GAN**: much smaller and faster, good for small objects.
- Runs on the **GPU** through DirectML, or on the **CPU**. In Auto mode it tries the GPU first and falls back to the CPU if the GPU can't run the model.
- Grows the mask slightly (to cover halos and shadows) and blends the edges so the fill doesn't show a seam.

## Requirements

- Windows 10 or 11, 64-bit
- **Paint.NET 5.1 or newer** (this plugin is built for .NET 9, which is what Paint.NET 5.1 runs on)
- For building from source: the **.NET SDK** (version 9 or newer) and the Paint.NET assemblies
- Disk space for the models: about 208 MB for LaMa and 28 MB for MI-GAN
- A DirectML-capable GPU is optional. The CPU works too, only slower.

## Files the plugin needs

All of these go in one folder, next to each other:

```
Paint.NET\Effects\ObjectRemover\
    ObjectRemover.dll                  <- the plugin (built from this source)
    Microsoft.ML.OnnxRuntime.dll       <- ONNX Runtime managed library
    onnxruntime.dll                    <- native ONNX Runtime
    DirectML.dll                       <- native DirectML (GPU support)
    (any other DLLs the build copies, except PaintDotNet.* and System.*)

%LocalAppData%\paint.net-shared\models\
    lama_fp32.onnx                     <- LaMa model (downloaded by the plugin)
    migan_pipeline_v2.onnx             <- MI-GAN model (downloaded by the plugin)
```

The models live in a common folder, `%LocalAppData%\paint.net-shared\models\`, so other Paint.NET plugins can share them. You can install only one of the two models and select just that one in the dialog.

## Step 1: Build the plugin

1. Open `ObjectRemover.csproj` and make sure the target framework matches the runtime your Paint.NET uses. For Paint.NET 5.1 that is:

   ```xml
   <TargetFramework>net9.0-windows</TargetFramework>
   ```

   To check your version, open `paintdotnet.runtimeconfig.json` in the Paint.NET folder and look at the framework `"version"`. A plugin must not target a newer .NET than Paint.NET itself runs, or it will fail to load with a `System.Runtime` error.

2. Point the build at the Paint.NET folder that contains `PaintDotNet.Base.dll`. This is the install you will run the plugin in. For the Store version, download the portable ZIP from getpaint.net and use that folder instead.

   ```
   cd ObjectRemover
   rd /s /q bin obj
   dotnet build -c Release -p:PdnRoot="C:\Program Files\Paint.NET"
   ```

   Or set the default once in the `.csproj`:

   ```xml
   <PdnRoot Condition="'$(PdnRoot)' == ''">C:\Program Files\Paint.NET</PdnRoot>
   ```

3. The output is in `bin\Release\net9.0-windows\win-x64\`. The NuGet package `Microsoft.ML.OnnxRuntime.DirectML` places its files there too (the native DLLs may be in a `runtimes\win-x64\native\` subfolder).

If the build reports that it cannot find `PaintDotNet.*` assemblies, `PdnRoot` is pointing at the wrong folder.

## Step 2: Get the models (automatic)

You don't need to download anything by hand. The first time you paint over an object and click **OK** (or **Preview removal**) while a model is missing, the plugin asks whether to download the missing models and shows a progress window.

- Files are saved to `%LocalAppData%\paint.net-shared\models\`.
- Downloads are **resumable**. If your connection drops or you cancel, the partial file (`*.part`) is kept. Click **OK** again and the download continues where it stopped.
- If the plugin finds a model in the old `Models` folder next to `ObjectRemover.dll`, it still uses it.

To download manually instead (for example on an offline PC), put the files in the folder above under exactly these names:

| Model | Download page | File to download | Save as |
|---|---|---|---|
| LaMa (Apache-2.0) | https://huggingface.co/Carve/LaMa-ONNX | `lama_fp32.onnx` (about 208 MB) | `lama_fp32.onnx` |
| MI-GAN (MIT) | https://huggingface.co/andraniksargsyan/migan | `migan_pipeline_v2.onnx` (about 28 MB, at the top level of the repo) | `migan_pipeline_v2.onnx` |

- For LaMa, use `lama_fp32.onnx`, not `lama.onnx`.
- For MI-GAN, use the **pipeline v2** file, not `migan.onnx`.
- The filenames must match exactly, because the plugin looks for those names.

## Step 3: Install into Paint.NET

1. Create the folder `C:\Program Files\Paint.NET\Effects\ObjectRemover\` (administrator rights are needed to write into Program Files).
2. Copy in `ObjectRemover.dll`, `Microsoft.ML.OnnxRuntime.dll`, `onnxruntime.dll`, `DirectML.dll` and any other DLLs the build output contains.
3. Do **not** copy `PaintDotNet.*.dll` or `System.*.dll` files. Paint.NET already provides them.
4. If Windows blocked the DLLs, right-click each one, choose **Properties**, and tick **Unblock**.
5. Restart Paint.NET.

If you are updating the plugin, delete the old files in the folder first (downloaded models are stored separately and are kept), then copy the new build in.

## How to use it

1. Open an image in Paint.NET. If you want to restrict the edit to part of the picture, make a selection first.
2. Choose **Effects > Photo > AI Object Remover**.
3. Paint over the object you want removed. Cover the whole object, including its shadow if it has one.
4. Choose a model and processor in the **Removal** group (see below).
5. Click **Preview removal**, or leave **Preview automatically after each stroke** ticked to see the result as you paint.
6. If the result looks right, click **OK**. Otherwise adjust the mask or settings and preview again.

The first run is slow because the model has to load, and on the GPU there is a short self-test. Later runs are much faster. The status box at the bottom of the dialog shows what is happening and how long the last run took.

### Brush controls

| Action | How |
|---|---|
| Paint the mask | Left-click and drag |
| Erase part of the mask | Tick **Eraser**, or right-click and drag |
| Change brush size | **Brush size** box or slider (2 to 400 px), Ctrl + mouse wheel, or the `[` and `]` keys |
| Zoom | Mouse wheel |
| Pan | Scrollbars, middle-drag, or hold Space and drag. Shift+wheel scrolls horizontally, Alt+wheel vertically |
| Undo last stroke | **Undo** button or Ctrl+Z (up to 40 strokes) |
| Clear the whole mask | **Clear mask** |
| Reset the view | **Fit view** |

### Removal settings

| Setting | Meaning |
|---|---|
| **Model** | LaMa (better quality, slower) or MI-GAN (faster, smaller). |
| **Processor** | Auto (GPU, falling back to CPU), GPU, or CPU. |
| **GPU adapter #** | Which GPU to use if you have more than one (0 is the first). Disabled when CPU is selected. |
| **Grow mask (px)** | Expands your painted area by this many pixels (0 to 64, default 6). Raise it if you see leftover edges or shadows. |
| **Edge blend (px)** | Fades the fill into the surrounding image over this many pixels (0 to 32, default 4). Raise it if you see a visible seam. |
| **Show result** | Untick to view the mask instead of the previewed result. |

### Tips

- Paint slightly beyond the object's edge, or raise **Grow mask**. Models work best when the whole object and its shadow are covered.
- For big objects, work in several smaller passes. Run the effect again on the result.
- If the fill looks blurry or repetitive, try the other model.
- Small objects on a simple background: MI-GAN is usually enough and much quicker.
- Pressing **Ctrl+F** repeats the effect with the same settings, but you will normally want to paint a new mask instead.

## Troubleshooting

| Problem | Likely cause and fix |
|---|---|
| The effect doesn't appear in the menu | Wrong folder, a blocked DLL, a build for the wrong .NET version, or Paint.NET older than 5.1. Check the Paint.NET error dialog at startup. |
| `Could not load file or assembly 'System.Runtime, Version=10.0.0.0'` | The plugin targets a newer .NET than Paint.NET. Set `<TargetFramework>` to `net9.0-windows` (match `paintdotnet.runtimeconfig.json`) and rebuild. |
| Build errors about `PaintDotNet` types not found | `PdnRoot` does not point at the folder containing `PaintDotNet.Base.dll`. |
| "Model file not found" | The models haven't been downloaded. Click OK in the plugin to download them, or place the files in `%LocalAppData%\paint.net-shared\models\` with the exact filenames. |
| Download keeps stopping | Click OK again; the download resumes from the partial `.part` file. If it never starts, check your firewall or proxy allows `huggingface.co`. |
| Status says GPU not usable, used CPU | The GPU or driver can't run that model through DirectML. It still works on the CPU. You can set **Processor** to CPU to skip the GPU test. |
| Errors mentioning `onnxruntime` or `DirectML` | The native DLLs are missing or not in the same folder as `ObjectRemover.dll`. |
| Very slow results | You are on the CPU, or processing a very large object with LaMa. Try MI-GAN or a smaller mask. |

## Notes

- The models are loaded on first use and unloaded after about 3 minutes of inactivity to free memory.
- The source contains `API-CHECK` comments marking Paint.NET API calls that may need small adjustments between Paint.NET builds.
- Model licenses: LaMa is Apache-2.0 (Samsung Research, ONNX export by Carve); MI-GAN is MIT (Picsart AI Research, ONNX export by andraniksargsyan). Check each model page before redistributing.
