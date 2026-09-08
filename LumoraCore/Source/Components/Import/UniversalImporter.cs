// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Components.Import;

public static class UniversalImporter
{
    public static void Import(AssetClass assetClass, IEnumerable<string> files, World world, float3 position, floatQ rotation, bool silent = false)
    {
        if (world == null) return;
        var list = new List<string>();
        foreach (var f in files)
        {
            if (!string.IsNullOrEmpty(f)) list.Add(f.Trim());
        }
        if (list.Count == 0) return;

        Logger.Log($"UniversalImporter: Importing {list.Count} file(s) as {assetClass}");

        switch (assetClass)
        {
            case AssetClass.Folder:
                SpawnFolderDialog(list[0], world, position, rotation);
                break;
            case AssetClass.Texture:
                SpawnImageDialog(list, world, position, rotation, silent);
                break;
            case AssetClass.Model:
                SpawnModelDialog(list, world, position, rotation, silent);
                break;
            case AssetClass.Video:
                SpawnVideoDialog(list, world, position, rotation, silent);
                break;
            // PointCloud and the rest have no import pipeline yet. PointCloud in particular
            // can NOT go through the model dialog - its extensions aren't in ModelImporter's
            // supported set and it throws. Route every unsupported class to an honest notice
            // dialog (with a raw-file drop) rather than faking success with a labeled
            // placeholder. As pipelines land (audio/font/text/etc.), give them their own
            // case above. - xlinka
            case AssetClass.Shader:
                SpawnShaderDialog(list, world, position, rotation, silent);
                break;
            case AssetClass.Package:
                SpawnPackageDialog(list, world, position, rotation, silent);
                break;
            case AssetClass.PointCloud:
            case AssetClass.Unknown:
            case AssetClass.Document:
            case AssetClass.Audio:
            case AssetClass.Font:
            case AssetClass.Subtitle:
            case AssetClass.Animation:
            case AssetClass.Object:
            case AssetClass.Text:
            case AssetClass.Volume:
            case AssetClass.Cubemap:
            case AssetClass.Special:
                Logger.Warn($"UniversalImporter: {assetClass} import is not supported yet ({list.Count} file(s)).");
                SpawnUnsupportedDialog(assetClass, list, world, position, rotation);
                break;
        }
    }

    public static void Import(string path, World world, float3 position, floatQ rotation, bool silent = false, bool rawFile = false)
    {
        var cls = rawFile ? AssetClass.Unknown : AssetHelper.IdentifyClass(path);
        Import(cls, new[] { path }, world, position, rotation, silent);
    }

    // Shader confirmation dialog. The .gdshader/other split happens HERE so glsl/hlsl go straight to
    // the honest unsupported notice and only orb-capable files reach the dialog; silent imports skip
    // the dialog like every other class. -xlinka
    private static void SpawnShaderDialog(List<string> files, World world, float3 position, floatQ rotation, bool silent)
    {
        var supported = new List<string>();
        var unsupported = new List<string>();
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (string.Equals(extension, ".gdshader", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, Lumora.Core.Assets.ShaderBundle.Extension, StringComparison.OrdinalIgnoreCase))
                supported.Add(file);
            else
                unsupported.Add(file);
        }

        if (unsupported.Count > 0)
            SpawnUnsupportedDialog(AssetClass.Shader, unsupported, world, position, rotation);
        if (supported.Count == 0)
            return;

        var slot = CreateDialogSlot(world, "Shader Importer", position, rotation);
        var dialog = slot.AttachComponent<ShaderImportDialog>();
        dialog.TargetWorld = world;
        dialog.Paths.AddRange(supported);
        dialog.SetLocalUserAsImporting();
        if (silent) dialog.RunImport();
    }

    // Shader import: each .gdshader becomes a grabbable material orb carrying a ShaderSourceProvider
    // (local asset URL, so it peer-transfers like any imported texture) + a CustomShaderMaterial whose
    // uniform params auto-populate from the parsed source. glsl/hlsl identify as Shader too but are not
    // material shaders here, so they get the honest unsupported notice instead of a broken orb. -xlinka
    public static void SpawnShaderOrbs(List<string> files, World world, float3 position, floatQ rotation)
    {
        var supported = new List<string>();
        var unsupported = new List<string>();
        foreach (var file in files)
        {
            if (string.Equals(Path.GetExtension(file), ".gdshader", StringComparison.OrdinalIgnoreCase))
                supported.Add(file);
            else
                unsupported.Add(file);
        }

        if (unsupported.Count > 0)
        {
            Logger.Warn($"UniversalImporter: {unsupported.Count} shader file(s) skipped (only .gdshader imports as a material).");
            SpawnUnsupportedDialog(AssetClass.Shader, unsupported, world, position, rotation);
        }

        int rowSize = (int)System.Math.Max(1, System.Math.Ceiling(System.Math.Sqrt(supported.Count)));
        int index = 0;
        foreach (var file in supported)
        {
            var offset = GridOffset(ref index, rowSize);
            var spawnAt = position + rotation * offset;
            world.StartTask(() => ImportShaderOrbAsync(file, world, spawnAt, rotation));
        }
    }

    private static async System.Threading.Tasks.Task ImportShaderOrbAsync(string path, World world, float3 position, floatQ rotation)
    {
        await WorldContext.ToBackground();

        // Every shader is stored as a .lumshader bundle: the text, the includes it pulls in from
        // beside it, and a manifest with the sandbox verdict and the uniforms. A bundle handed in
        // directly is kept as it is. That one file is the record the cloud carries when a world or an
        // object using the shader is saved. -xlinka
        byte[] bundle;
        string source;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (Lumora.Core.Assets.ShaderBundle.IsBundle(bytes))
            {
                if (!Lumora.Core.Assets.ShaderBundle.TryOpen(bytes, out _, out source))
                {
                    Logger.Warn($"UniversalImporter: '{Path.GetFileName(path)}' is not a shader bundle this build can open.");
                    return;
                }
                bundle = bytes;
            }
            else
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var includes = Lumora.Core.Assets.ShaderBundle.CollectIncludes(text, Path.GetDirectoryName(path));
                source = Lumora.Core.Assets.ShaderBundle.Inline(text, includes);
                bundle = Lumora.Core.Assets.ShaderBundle.Build(Path.GetFileNameWithoutExtension(path), text, includes);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"UniversalImporter: failed to read shader '{path}': {ex.Message}");
            return;
        }

        // Early verdict for the log; the orb is created either way so the user can open its inspector
        // and read the full sandbox report. A rejected shader never compiles regardless - the material
        // gate re-validates on every peer.
        var verdict = Lumora.Core.Assets.ShaderSourceValidator.Validate(source);
        if (!verdict.IsValid)
        {
            Logger.Warn($"UniversalImporter: shader '{Path.GetFileName(path)}' fails the sandbox ({verdict.Errors[0]}); importing anyway so the inspector can show the report.");
        }

        var localDb = Engine.Current?.LocalDB;
        if (localDb == null)
        {
            Logger.Warn("UniversalImporter: no local asset database; cannot import shader.");
            return;
        }

        string uri;
        try
        {
            uri = await localDb.SaveAssetAsync(bundle, Lumora.Core.Assets.ShaderBundle.Extension);
        }
        catch (Exception ex)
        {
            Logger.Warn($"UniversalImporter: failed to store shader '{path}': {ex.Message}");
            return;
        }

        // Datamodel writes happen on the world's update thread. Everything below this line is back on it.
        await WorldContext.ToWorld();

        var slot = world.RootSlot.AddSlot(Path.GetFileNameWithoutExtension(path));
        slot.GlobalPosition = position;
        slot.GlobalRotation = rotation;
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 0.18f;
        mesh.Segments.Value = 32;
        mesh.Rings.Value = 16;
        mesh.UVScale.Value = new float2(5f, 2.5f);

        var collider = slot.AttachComponent<SphereCollider>();
        collider.Radius.Value = mesh.Radius.Value;
        collider.Type.Value = ColliderType.Trigger;

        var provider = slot.AttachComponent<Lumora.Core.Components.Assets.ShaderSourceProvider>();
        provider.URL.Value = new Uri(uri);

        var material = slot.AttachComponent<Lumora.Core.Components.Assets.CustomShaderMaterial>();
        material.Shader.Target = provider;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        // Open the focused material panel half a meter to the orb's right so tuning can start
        // immediately, facing the same way the orb spawned.
        MaterialInspectorPanel.Spawn(world, material, position + rotation * new float3(0.5f, 0f, 0f), rotation);

        Lumora.Core.Components.InspectorUndo.Record(world,
            Lumora.Core.Components.SlotExistenceUndoBatch.Created(world, new[] { slot }, Lumora.Core.Components.UndoLocale.ImportShader));
        Logger.Log($"UniversalImporter: shader orb '{slot.SlotName.Value}' created ({uri}).");
    }

    public static float3 GridOffset(ref int index, int rowSize)
    {
        var result = new float3(index % rowSize, index / rowSize, 0f);
        index++;
        return result;
    }

    // Spawn an honest "not supported yet" dialog for an asset class with no pipeline.
    // It tells the user the format isn't handled and offers a raw-file drop - it never
    // fabricates a placeholder that pretends the content loaded. - xlinka
    private static void SpawnUnsupportedDialog(AssetClass assetClass, List<string> files, World world, float3 position, floatQ rotation)
    {
        var slot = CreateDialogSlot(world, "Unsupported Importer", position, rotation);
        var dialog = slot.AttachComponent<UnsupportedImportDialog>();
        dialog.TargetWorld = world;
        dialog.ClassName = ClassDisplayName(assetClass);
        dialog.Paths.AddRange(files);
        dialog.SetLocalUserAsImporting();
    }

    private static string ClassDisplayName(AssetClass assetClass) => assetClass switch
    {
        AssetClass.PointCloud => "Point cloud",
        AssetClass.Package => "Object package",
        AssetClass.Unknown => "This file type",
        _ => assetClass.ToString(),
    };

    private static Slot CreateDialogSlot(World world, string name, float3 position, floatQ rotation)
    {
        // Dialog lives in the focused world (where the user and their laser
        // pointer are). Local slot so it's per-user/non-synced. The dialog
        // creates its own FontProvider in this same world via DefaultFontUrl
        // so no cross-world asset refs are needed. - xlinka
        var slot = world.RootSlot.AddLocalSlot(name);
        slot.GlobalPosition = position;
        slot.GlobalRotation = rotation;
        slot.GlobalScale = float3.One;
        return slot;
    }

    // A package is read and measured before anything is built, so this dialog opens on a readout of
    // what would convert rather than on an import button. Deliberately ignores `silent`: there is
    // nothing safe to do without being asked, because a conversion between engines always drops
    // something and the user should see what before it happens. -xlinka
    private static void SpawnPackageDialog(List<string> files, World world, float3 position, floatQ rotation, bool silent)
    {
        int index = 0;
        int rowSize = (int)System.Math.Max(1, System.Math.Ceiling(System.Math.Sqrt(files.Count)));
        foreach (var file in files)
        {
            var offset = GridOffset(ref index, rowSize);
            var slot = CreateDialogSlot(world, "Package Importer", position + rotation * offset, rotation);
            var dialog = slot.AttachComponent<PackageImportDialog>();
            dialog.TargetWorld = world;
            dialog.Paths.Add(file);
            dialog.SetLocalUserAsImporting();
            if (silent) dialog.RunImport();
        }
    }

    private static void SpawnFolderDialog(string folder, World world, float3 position, floatQ rotation)
    {
        var slot = CreateDialogSlot(world, "Folder Importer", position, rotation);
        var dialog = slot.AttachComponent<FolderImportDialog>();
        dialog.TargetWorld = world;
        dialog.Path.Value = folder;
        dialog.SetLocalUserAsImporting();
    }

    private static void SpawnImageDialog(List<string> files, World world, float3 position, floatQ rotation, bool silent)
    {
        var slot = CreateDialogSlot(world, "Image Importer", position, rotation);
        var dialog = slot.AttachComponent<ImageImportDialog>();
        dialog.TargetWorld = world;
        dialog.Paths.AddRange(files);
        dialog.SetLocalUserAsImporting();
        if (silent) dialog.RunImport();
    }

    private static void SpawnModelDialog(List<string> files, World world, float3 position, floatQ rotation, bool silent)
    {
        var slot = CreateDialogSlot(world, "Model Importer", position, rotation);
        var dialog = slot.AttachComponent<ModelImportDialog>();
        dialog.TargetWorld = world;
        dialog.Paths.AddRange(files);
        dialog.SetLocalUserAsImporting();
        if (silent)
        {
            dialog.AutoScale.Value = true;
            dialog.RunImport();
        }
    }

    private static void SpawnVideoDialog(List<string> files, World world, float3 position, floatQ rotation, bool silent)
    {
        var slot = CreateDialogSlot(world, "Video Importer", position, rotation);
        var dialog = slot.AttachComponent<VideoImportDialog>();
        dialog.TargetWorld = world;
        dialog.Paths.AddRange(files);
        dialog.SetLocalUserAsImporting();
        if (silent) dialog.RunImport();
    }
}
