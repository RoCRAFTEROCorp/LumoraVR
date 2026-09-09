// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Networking.Sync;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

public sealed class SyncIntList : SyncList<Sync<int>>
{
    public event Action<SyncIntList>? OnChanged;

    public SyncIntList(Component owner)
    {
        Parent = owner;
        ElementsAdded += OnElementsAdded;
        ElementsRemoving += OnElementsRemoving;
    }

    public int GetValue(int index, int fallback)
    {
        return index >= 0 && index < Count ? GetElement(index).Value : fallback;
    }

    public void SetValue(int index, int value)
    {
        EnsureMinimumCount(index + 1);
        GetElement(index).Value = value;
    }

    public bool EnsureExactCount(int count, int defaultValue)
    {
        int oldCount = Count;
        base.EnsureExactCount(count);
        for (int i = oldCount; i < Count; i++)
        {
            GetElement(i).Value = defaultValue;
        }

        return oldCount != Count;
    }

    private void OnElementsAdded(SyncElementList<Sync<int>> list, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            list[index + i].OnChanged += OnElementChanged;
        }
        OnChanged?.Invoke(this);
    }

    private void OnElementsRemoving(SyncElementList<Sync<int>> list, int index, int count)
    {
        for (int i = 0; i < count; i++)
        {
            list[index + i].OnChanged -= OnElementChanged;
        }
        OnChanged?.Invoke(this);
    }

    private void OnElementChanged(int value)
    {
        OnChanged?.Invoke(this);
    }
}

[ComponentCategory("Rendering")]
public class MeshRenderer : ImplementableComponent
{
    public const int NoSurfaceRenderPriority = int.MinValue;

    public readonly SyncRef<Component> Mesh;

    public readonly SyncAssetList<MaterialAsset> Materials;
    public readonly SyncAssetList<MaterialPropertyBlockAsset> MaterialPropertyBlocks;
    public readonly SyncIntList SurfaceRenderPriorities;

    public readonly Sync<ShadowCastMode> ShadowCastMode;

    // lower values render first
    public readonly Sync<int> SortingOrder;

    // When true, the hook renders each mesh surface as its OWN MeshInstance3D ordered purely by a distinct
    // per-surface SortingOffset (= SortingOrder * a stride + surface index), with uniform render_priority. This
    // bypasses Godot's 256-level render_priority cap to give unbounded positional order for UI. Set by Helio's
    // GraphicsChunk when the unbounded-ordering mode is enabled; off for all normal (world) meshes. -xlinka
    public bool PerSurfaceOrdering { get; set; }

    // Extra frustum-cull margin (world units) the hook applies to the mesh instance(s). Scrolled UI chunks
    // displace their vertices in the VERTEX SHADER (clip_offset), so the instance AABB - computed from the
    // baked, undisplaced verts - no longer bounds what's on screen; at glancing angles or up close Godot
    // culls the instance while displaced pixels should still be visible. Set by Helio's canvas for
    // scroll-participating chunks; zero for normal meshes. -xlinka
    public float ExtraCullMargin { get; set; }

    // Distance in metres past which the hook stops drawing this renderer, 0 = draws at any distance, with
    // ViewDistanceFadeMargin as the dissolve band in front of the cut. Pushed in by whatever owns the
    // renderer rather than authored on it: a Canvas culling its chunk meshes, a TextRenderer culling the
    // child renderer it built for a sign. The hook folds it into the same Godot visibility range a LodGroup
    // uses and keeps the tighter of the two, so the two systems can't undo each other. -xlinka
    public float MaxViewDistance { get; set; }
    public float ViewDistanceFadeMargin { get; set; }

    public bool MaterialsChanged { get; set; }
    public bool MaterialPropertyBlocksChanged { get; set; }
    public bool SurfaceRenderPrioritiesChanged { get; set; }
    public bool SurfaceAssignmentsChanged { get; set; }

    // Mesh DATA changing (ClearSurfaces + re-add) is invisible to the SyncRef WasChanged path,
    // so callers that rebuild the underlying ArrayMesh contents in place must flag us dirty
    // explicitly or surface override materials end up stale. - xlinka
    public void FlagSurfacesDirty()
    {
        SurfaceAssignmentsChanged = true;
        RunApplyChanges();
    }

    public void EnsureExactSurfaceRenderPriorityCount(int count)
    {
        if (SurfaceRenderPriorities.EnsureExactCount(count, NoSurfaceRenderPriority))
        {
            SurfaceRenderPrioritiesChanged = true;
        }
    }

    public void SetSurfaceRenderPriority(int index, int priority)
    {
        if (SurfaceRenderPriorities.GetValue(index, NoSurfaceRenderPriority) == priority)
        {
            return;
        }

        SurfaceRenderPriorities.SetValue(index, priority);
        SurfaceRenderPrioritiesChanged = true;
    }

    public int GetSurfaceRenderPriority(int index)
    {
        return SurfaceRenderPriorities.GetValue(index, NoSurfaceRenderPriority);
    }

    public AssetRef<MaterialAsset> Material
    {
        get
        {
            if (Materials.Count == 0)
            {
                return Materials.Add();
            }

            return Materials.GetElement(0);
        }
    }

    private readonly LoadingSurfaceLatch _loadingSurfaces = new();

    // True while this surface's material is a thing that IS coming but has not arrived - so the hook
    // paints the loading skin over it instead of whatever half-state the material is in. A surface with
    // NO material assigned is authored, not loading, and answers false: an untextured mesh is somebody's
    // intent, not a gap. Purely event-driven - the arrival notification that lands the texture already
    // re-drives the renderer, so nothing here runs per frame. -xlinka
    public bool IsSurfaceLoading(int surfaceIndex)
    {
        int index = SurfaceMaterialIndex(surfaceIndex);
        if (index < 0)
            return false;
        return _loadingSurfaces.IsLoading(index, Materials.GetElement(index).Target);
    }

    // The material that governs a given surface. Same clamp IsSurfaceLoading applies, so a diagnostic
    // that asks what a surface is waiting on names the material that surface actually gets.
    public IAssetProvider<MaterialAsset>? SurfaceMaterialProvider(int surfaceIndex)
    {
        int index = SurfaceMaterialIndex(surfaceIndex);
        return index < 0 ? null : Materials.GetElement(index).Target;
    }

    // Surfaces past the end of the list share the last material, which is what the hook paints.
    private int SurfaceMaterialIndex(int surfaceIndex)
    {
        int count = Materials.Count;
        if (count == 0 || surfaceIndex < 0)
            return -1;
        return surfaceIndex < count ? surfaceIndex : count - 1;
    }

    public MeshRenderer()
    {
        Mesh = new SyncRef<Component>(this);
        Materials = new SyncAssetList<MaterialAsset>(this);
        MaterialPropertyBlocks = new SyncAssetList<MaterialPropertyBlockAsset>(this);
        SurfaceRenderPriorities = new SyncIntList(this);
        ShadowCastMode = new Sync<ShadowCastMode>(this, Components.ShadowCastMode.On);
        SortingOrder = new Sync<int>(this, 0);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Materials.OnChanged += OnMaterialsChanged;
        MaterialPropertyBlocks.OnChanged += OnMaterialPropertyBlocksChanged;
        SurfaceRenderPriorities.OnChanged += OnSurfaceRenderPrioritiesChanged;
        // The Mesh ref is a plain SyncRef<Component>, which (unlike AssetRef) does NOT re-fire us when its
        // target resolves. On a joiner the mesh provider can decode AFTER us, so our first apply has no
        // geometry to read. Re-pull when the provider resolves or the ref retargets, so we don't stay
        // invisible. (The provider also pokes us via FlagSurfacesDirty once it uploads geometry.) -xlinka
        Mesh.OnObjectAvailable += OnMeshRefResolved;
        Mesh.OnTargetChange += OnMeshRefResolved;
        LumoraLogger.Debug($"MeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    private void OnMeshRefResolved(SyncRef<Component> reference)
    {
        FlagSurfacesDirty();
    }

    private void OnMaterialsChanged(SyncAssetList<MaterialAsset> list)
    {
        MaterialsChanged = true;
    }

    private void OnMaterialPropertyBlocksChanged(SyncAssetList<MaterialPropertyBlockAsset> list)
    {
        MaterialPropertyBlocksChanged = true;
    }

    private void OnSurfaceRenderPrioritiesChanged(SyncIntList list)
    {
        SurfaceRenderPrioritiesChanged = true;
    }

    public override void OnDestroy()
    {
        Materials.OnChanged -= OnMaterialsChanged;
        MaterialPropertyBlocks.OnChanged -= OnMaterialPropertyBlocksChanged;
        SurfaceRenderPriorities.OnChanged -= OnSurfaceRenderPrioritiesChanged;
        base.OnDestroy();
        LumoraLogger.Debug($"MeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }
}

public enum ShadowCastMode
{
    Off = 0,
    On = 1,
    ShadowOnly = 2,
    DoubleSided = 3
}

public enum MotionVectorMode
{
    Camera = 0,
    Object = 1,
    NoMotion = 2
}

