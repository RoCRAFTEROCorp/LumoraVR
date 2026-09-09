// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(MeshRenderer))]
public class MeshRendererHook : MeshRendererHookBase<MeshRenderer, Node3D>
{
    protected override bool UseMeshInstance => true;

    protected override void AssignMesh(Node3D renderer, Mesh mesh)
    {
        // Not reached: UseMeshInstance=true means the base class owns mesh assignment via MeshInstance3D.
        LumoraLogger.Warn("MeshRendererHook.AssignMesh: unexpected call - base class should handle mesh assignment when UseMeshInstance is true.");
    }

    protected override void OnAttachRenderer()
    {
        base.OnAttachRenderer();
        if (LumoraLogger.EnableDebug)
            LumoraLogger.Debug($"MeshRendererHook: Attached renderer for slot '{Owner.Slot.SlotName.Value}'");
    }

    protected override void OnCleanupRenderer()
    {
        base.OnCleanupRenderer();
        if (LumoraLogger.EnableDebug)
            LumoraLogger.Debug($"MeshRendererHook: Cleaned up renderer for slot '{Owner?.Slot?.SlotName.Value}'");
    }

    public static IHook<MeshRenderer> Constructor()
    {
        return new MeshRendererHook();
    }
}

