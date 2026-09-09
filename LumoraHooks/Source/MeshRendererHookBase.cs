// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

public abstract class MeshRendererHookBase<T, U> : ComponentHook<T>, ILodRangeTarget
    where T : MeshRenderer
    where U : Node3D
{
    protected struct RendererData
    {
        public Node3D gameObject;
        public MeshInstance3D meshInstance;
        public U meshRenderer;
    }

    private MeshInstance3D meshInstance = null!;
    private int _lastSurfaceOverrideCount;

    // Unbounded UI ordering (Owner.PerSurfaceOrdering): one MeshInstance3D per mesh surface, each ordered by a
    // distinct SortingOffset so draw order isn't capped by Godot's 256-level render_priority. -xlinka
    private readonly System.Collections.Generic.List<MeshInstance3D> _perSurfaceInstances = new();
    private const float PerSurfaceSortStride = 1024f;

    protected abstract bool UseMeshInstance { get; }

    protected bool meshWasChanged { get; private set; }

    public U MeshRenderer { get; protected set; } = null!;

    protected abstract void AssignMesh(U renderer, Mesh mesh);

    protected virtual void OnAttachRenderer()
    {
        HideSourceMeshInstance();
    }

    private void HideSourceMeshInstance()
    {
        var meshValue = Owner.Mesh.Target;
        if (meshValue is LumoraMeshes.ProceduralMesh proceduralMesh)
        {
            if (proceduralMesh.Hook is MeshHook meshHook)
            {
                var sourceMeshInstance = meshHook.GetMeshInstance();
                if (sourceMeshInstance != null)
                {
                    sourceMeshInstance.Visible = false;
                    LumoraLogger.Debug($"MeshRendererHookBase: Hid source MeshHook's MeshInstance3D");
                }
            }
        }
    }

    protected virtual void OnCleanupRenderer()
    {
    }

    // RenderLayerOverrideHook only walks nodes that exist when it applies, so mesh
    // instances created later (canvas chunks built after a parked dash reactivates)
    // would stay on the default layer - invisible to layer-masked capture cameras.
    // Inherit the override at creation instead.
    private void ApplyInheritedRenderLayer(VisualInstance3D visual)
    {
        for (var slot = Owner.Slot; slot != null; slot = slot.Parent)
        {
            var layerOverride = slot.GetComponent<RenderLayerOverride>();
            if (layerOverride != null && layerOverride.Enabled && layerOverride.Layer.Value != 0)
            {
                visual.Layers = (uint)layerOverride.Layer.Value;
                return;
            }
        }
    }

    public override void ApplyChanges()
    {
        // Not initialized yet (or already torn down) - nothing to attach under.
        if (attachedNode == null)
        {
            return;
        }

        if (Owner.Mesh.Target != null && IsMeshAssetAvailable())
        {
            if (MeshRenderer == null)
            {
                var gameObject = new Node3D();
                gameObject.Name = "MeshRenderer";
                attachedNode.AddChild(gameObject);

                if (UseMeshInstance)
                {
                    meshInstance = new MeshInstance3D();
                    ApplyInheritedRenderLayer(meshInstance);
                    gameObject.AddChild(meshInstance);
                    _rangeApplied = false;
                }

                MeshRenderer = (gameObject as U)!;
                OnAttachRenderer();
            }

            // On a joiner this hook often drains BEFORE its mesh provider has uploaded geometry, so the
            // very first apply latches a 0-surface (or null) mesh. A plain SyncRef<Component> never
            // re-fires when the provider's mesh DATA later appears, so without re-pulling we'd stay
            // invisible forever. Re-pull whenever the ref changed OR we're still holding an empty mesh -
            // so the provider's post-upload FlagSurfacesDirty re-drive (and any later regenerate) actually
            // re-reads the now-populated ArrayMesh. Once we hold a real surface-bearing mesh this stops. -xlinka
            bool meshRefChanged = Owner.Mesh.GetWasChangedAndClear();
            bool haveRealMesh = meshInstance?.Mesh is ArrayMesh curMesh && curMesh.GetSurfaceCount() > 0;
            meshWasChanged = meshRefChanged || !haveRealMesh;
            if (meshWasChanged)
            {
                HideSourceMeshInstance();

                var godotMesh = GetGodotMeshFromAsset();
                if (UseMeshInstance && meshInstance != null)
                {
                    meshInstance.Mesh = godotMesh;
                }
                else
                {
                    AssignMesh(MeshRenderer, godotMesh);
                }
            }

            ApplyBoundsOverride();

            // A renderer with geometry and NO material must not draw.
            //
            // We hand Godot a null surface override when the list is empty and it quietly substitutes its
            // own default white-grey, so the object shows up as a big pale untextured blob instead of not
            // showing up at all. On an imported avatar whose material reference failed to wire, that blob
            // follows the wearer into every world and looks like a bug in something else entirely.
            // Drawing nothing is the honest result of having nothing to draw it with. -xlinka
            bool enabled = Owner.Enabled && Owner.Materials.Count > 0;
            if (meshInstance != null && meshInstance.Visible != enabled)
            {
                meshInstance.Visible = enabled;
            }

            // See MeshRenderer.ExtraCullMargin: vertex-shader-displaced UI needs grown cull bounds.
            if (meshInstance != null && meshInstance.ExtraCullMargin != Owner.ExtraCullMargin)
            {
                meshInstance.ExtraCullMargin = Owner.ExtraCullMargin;
            }

            bool renderQueueApplied = false;
            if (Owner.SortingOrder.GetWasChangedAndClear())
            {
                ApplyRenderQueue();
                renderQueueApplied = true;
            }

            if (Owner.ShadowCastMode.GetWasChangedAndClear())
            {
                ApplyShadowCastMode(Owner.ShadowCastMode.Value);
            }

            bool materialRefsChanged = Owner.MaterialsChanged
                || Owner.MaterialPropertyBlocksChanged
                || AnyChanged(Owner.Materials)
                || AnyChanged(Owner.MaterialPropertyBlocks);
            bool surfacePrioritiesChanged = Owner.SurfaceRenderPrioritiesChanged
                || AnyChanged(Owner.SurfaceRenderPriorities);
            bool surfaceAssignmentsChanged = Owner.SurfaceAssignmentsChanged || meshWasChanged;
            bool materialsChanged = materialRefsChanged || surfacePrioritiesChanged || surfaceAssignmentsChanged;

            if (materialsChanged)
            {
                Owner.MaterialsChanged = false;
                Owner.MaterialPropertyBlocksChanged = false;
                Owner.SurfaceRenderPrioritiesChanged = false;
                Owner.SurfaceAssignmentsChanged = false;
                ApplyMaterials();
                if (!renderQueueApplied)
                    ApplyRenderQueue();
            }

            SyncPerSurfaceInstances();
            ApplyLodRange();
        }
        else
        {
            CleanupRenderer(destroyingWorld: false);
        }
    }

    // LOD plumbing. A LodGroup decides the band; this only makes sure every instance the hook owns
    // carries it, including per-surface ones that get built after the group last spoke. -xlinka
    private LodVisibilityRange _lodRange = LodVisibilityRange.Unbounded;

    // The band last written to the instances, so a re-apply that changed nothing about it (a mesh
    // re-upload, a material swap) does not push five visibility properties through the marshalling
    // layer again. Cleared whenever an instance is created, since a fresh one holds engine defaults.
    private LodVisibilityRange _appliedRange = LodVisibilityRange.Unbounded;
    private bool _rangeApplied;

    public void SetLodVisibilityRange(in LodVisibilityRange range)
    {
        if (_lodRange.Equals(range))
            return;
        _lodRange = range;
        ApplyLodRange();
    }

    public bool TryGetLargestDimension(out float size)
    {
        size = 0f;
        if (meshInstance == null || meshInstance.Mesh == null)
            return false;
        var aabb = meshInstance.GetAabb();
        var basis = meshInstance.GlobalTransform.Basis;
        float scale = Mathf.Max(basis.X.Length(), Mathf.Max(basis.Y.Length(), basis.Z.Length()));
        size = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z)) * scale;
        return size > 0f;
    }

    private void ApplyLodRange()
    {
        // Torn down: a LOD group handing back an unbounded band on its way out can reach us after the
        // component is gone, and there is nothing left to read a view distance off.
        if (Owner == null)
            return;

        var effective = LodVisibilityRange.Tightest(
            in _lodRange,
            LodVisibilityRange.To(Owner.MaxViewDistance, Owner.ViewDistanceFadeMargin));
        if (_rangeApplied && _appliedRange.Equals(effective))
            return;
        _appliedRange = effective;
        _rangeApplied = true;
        effective.ApplyTo(meshInstance);
        foreach (var inst in _perSurfaceInstances)
            effective.ApplyTo(inst);
    }

    // Unbounded UI ordering. When Owner.PerSurfaceOrdering is set (Helio's opt-in mode), render each surface of
    // the chunk mesh as its OWN MeshInstance3D with a distinct SortingOffset (= SortingOrder * stride + surface
    // index) and a uniform render_priority. Godot then orders them by SortingOffset (full range) instead of the
    // 256-level render_priority cap, so UI layers without bound. The combined instance is hidden while this is
    // active; turning the mode off frees the per-surface instances and restores it. Cost: a single-surface mesh
    // + a material duplicate per surface per update, so it's a deliberate opt-in. -xlinka
    private void SyncPerSurfaceInstances()
    {
        if (!Owner.PerSurfaceOrdering)
        {
            if (_perSurfaceInstances.Count > 0)
            {
                foreach (var inst in _perSurfaceInstances)
                    if (GodotObject.IsInstanceValid(inst)) inst.QueueFree();
                _perSurfaceInstances.Clear();
                if (meshInstance != null) meshInstance.Visible = Owner.Enabled;
            }
            return;
        }

        if (meshInstance == null || meshInstance.Mesh is not ArrayMesh combined) return;

        int surfaceCount = combined.GetSurfaceCount();
        meshInstance.Visible = false; // the per-surface instances render in its place

        var parent = meshInstance.GetParent();
        float baseKey = Owner.SortingOrder.Value * PerSurfaceSortStride;
        bool enabled = Owner.Enabled;

        for (int i = 0; i < surfaceCount; i++)
        {
            MeshInstance3D inst;
            if (i < _perSurfaceInstances.Count)
            {
                inst = _perSurfaceInstances[i];
            }
            else
            {
                inst = new MeshInstance3D();
                ApplyInheritedRenderLayer(inst);
                parent?.AddChild(inst);
                _perSurfaceInstances.Add(inst);
                _rangeApplied = false;
            }

            var single = new ArrayMesh();
            single.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, combined.SurfaceGetArrays(i));
            inst.Mesh = single;

            // Duplicate so forcing a uniform render_priority doesn't mutate the chunk's shared/cloned material -
            // order is carried entirely by SortingOffset now.
            var srcMat = GetSurfaceMaterial(i);
            if (srcMat != null)
            {
                var mat = (Material)srcMat.Duplicate();
                mat.RenderPriority = 0;
                inst.SetSurfaceOverrideMaterial(0, mat);
            }

            inst.SortingOffset = baseKey + i;
            // Scrolled UI chunks displace verts in the vertex shader; grow the cull bounds or Godot culls the
            // instance at glancing angles/up close while displaced pixels should still be visible. -xlinka
            inst.ExtraCullMargin = Owner.ExtraCullMargin;
            inst.Visible = enabled;
        }

        for (int i = _perSurfaceInstances.Count - 1; i >= surfaceCount; i--)
        {
            if (GodotObject.IsInstanceValid(_perSurfaceInstances[i]))
                _perSurfaceInstances[i].QueueFree();
            _perSurfaceInstances.RemoveAt(i);
        }
    }

    // Set by GetSurfaceMaterial whenever it hands back the placeholder, read once per apply.
    private bool _surfaceStillLoading;

    protected virtual void ApplyMaterials()
    {
        if (meshInstance == null) return;

        int count = GetSurfaceCount();

        _surfaceStillLoading = false;
        for (int i = 0; i < count; i++)
        {
            meshInstance.SetSurfaceOverrideMaterial(i, GetSurfaceMaterial(i));
        }

        // Come back until the real material lands.
        //
        // The comment on GetSurfaceMaterial says the swap "rides the asset-arrival notification that
        // already re-drives this hook". It does not: ApplyChanges is queue-driven, a texture finishing
        // its download dirties nothing, and so a surface that was still arriving at bind time kept the
        // checker for the rest of the session unless some unrelated edit re-dirtied the hook. Re-queue
        // while anything is still on the placeholder, and stop as soon as nothing is. -xlinka
        if (_surfaceStillLoading && Owner is { IsDestroyed: false })
            Owner.MarkChangeDirty();

        meshInstance.MaterialOverride = null;
        _lastSurfaceOverrideCount = count;

        if (_uiDiagLogged < 3 && Owner.Slot?.Parent?.SlotName?.Value == "HelioTestPanel")
        {
            _uiDiagLogged++;
            int assigned = 0;
            for (int i = 0; i < count; i++)
            {
                if (meshInstance.GetSurfaceOverrideMaterial(i) != null) assigned++;
            }
            LumoraLogger.Debug(
                $"MeshRendererHook.ApplyMaterials[diag]: slot={Owner.Slot?.SlotName.Value} " +
                $"surfaces={count} materials(list)={Owner.Materials.Count} " +
                $"overrides(non-null)={assigned}");
        }
    }

    private int _uiDiagLogged;

    private readonly LoadingSkinWatch _loadingSkin = new();

    private Material GetSurfaceMaterial(int index)
    {
        // Assigned but still arriving: wear the loading skin rather than whatever half-state the
        // material is in (a white untextured PBS, or nothing at all). The swap back to the real
        // material rides the asset-arrival notification that already re-drives this hook, and the
        // core-side latch makes sure it only happens once. -xlinka
        if (Owner.IsSurfaceLoading(index))
        {
            _loadingSkin.Note(index, Owner.SurfaceMaterialProvider(index), Owner.Slot?.SlotName.Value);
            _surfaceStillLoading = true;
            return LoadingPlaceholderMaterial.Get();
        }

        _loadingSkin.Clear(index);

        var materialAsset = GetMaterialAsset(index);
        if (materialAsset == null)
        {
            return null!;
        }

        var propertyBlock = GetPropertyBlockAsset(index);
        if (propertyBlock != null && propertyBlock.IsValid)
        {
            return ApplySurfaceRenderPriority(index, (propertyBlock.ApplyToMaterial(materialAsset) as Material)!);
        }

        return ApplySurfaceRenderPriority(index, (materialAsset.GodotMaterial as Material)!);
    }

    private Material ApplySurfaceRenderPriority(int index, Material material)
    {
        if (material == null)
        {
            return null!;
        }

        int priority = Owner.GetSurfaceRenderPriority(index);
        if (priority == Lumora.Core.Components.MeshRenderer.NoSurfaceRenderPriority)
        {
            return material;
        }

        if (material.RenderPriority == priority)
        {
            return material;
        }

        MaterialPropertyApplicator.ApplyGodotRenderPriority(material, priority);
        return material;
    }

    private MaterialAsset GetMaterialAsset(int index)
    {
        if (Owner.Materials.Count == 0)
        {
            return null!;
        }

        int materialIndex = System.Math.Min(index, Owner.Materials.Count - 1);
        return Owner.Materials.GetElement(materialIndex).Asset;
    }

    private MaterialPropertyBlockAsset GetPropertyBlockAsset(int index)
    {
        if (Owner.MaterialPropertyBlocks.Count == 0 || index >= Owner.MaterialPropertyBlocks.Count)
        {
            return null!;
        }

        return Owner.MaterialPropertyBlocks.GetElement(index).Asset;
    }

    private int GetSurfaceCount()
    {
        return meshInstance?.Mesh?.GetSurfaceCount() ?? 0;
    }

    private static bool AnyChanged<A>(SyncAssetList<A> list) where A : Asset
    {
        bool changed = false;
        foreach (var assetRef in list)
        {
            changed |= assetRef.GetWasChangedAndClear();
        }

        return changed;
    }

    private static bool AnyChanged(SyncIntList list)
    {
        bool changed = false;
        foreach (var field in list)
        {
            changed |= field.GetWasChangedAndClear();
        }

        return changed;
    }

    private void CleanupRenderer(bool destroyingWorld)
    {
        // Per-surface instances are children of the MeshRenderer node, so freeing it frees them too - just drop
        // our stale references so a fresh renderer rebuilds them. -xlinka
        _perSurfaceInstances.Clear();
        _rangeApplied = false;
        if (!destroyingWorld && MeshRenderer != null && GodotObject.IsInstanceValid(MeshRenderer))
        {
            ((Node)MeshRenderer).QueueFree();
        }
        OnCleanupRenderer();
    }

    public override void Destroy(bool destroyingWorld)
    {
        CleanupRenderer(destroyingWorld);
        meshInstance = null!;
        MeshRenderer = default!;
        base.Destroy(destroyingWorld);
    }

    protected virtual bool IsMeshAssetAvailable()
    {
        return Owner.Mesh.Target != null;
    }

    // ProceduralMesh.OverrideBoundingBox was a field nothing read. It matters for any mesh whose
    // shader moves vertices far from the authored geometry: Godot culls on the mesh AABB, so the
    // bubble field vanished the moment its origin left the frustum. A zero Aabb clears the override. -xlinka
    private void ApplyBoundsOverride()
    {
        if (meshInstance == null) return;

        var aabb = new Aabb();
        if (Owner.Mesh.Target is LumoraMeshes.ProceduralMesh proceduralMesh && proceduralMesh.OverrideBoundingBox.Value)
        {
            var box = proceduralMesh.OverridenBoundingBox.Value;
            var min = box.Min;
            var size = box.Max - box.Min;
            aabb = new Aabb(new Vector3(min.x, min.y, min.z), new Vector3(size.x, size.y, size.z));
        }

        if (meshInstance.CustomAabb != aabb)
            meshInstance.CustomAabb = aabb;
    }

    protected virtual Mesh GetGodotMeshFromAsset()
    {
        var meshComponent = Owner.Mesh.Target;
        if (meshComponent == null) return null!;

        if (meshComponent is LumoraMeshes.ProceduralMesh proceduralMesh)
        {
            if (proceduralMesh.Hook is MeshHook meshHook)
            {
                var meshInstance = meshHook.GetMeshInstance();
                if (meshInstance?.Mesh != null)
                {
                    return meshInstance.Mesh;
                }
            }
            LumoraLogger.Debug("MeshRendererHookBase: ProceduralMesh hook not ready");
            return null!;
        }

        // Imported / asset-system meshes: a MeshProvider supplies a MeshDataAsset whose hook (MeshAssetHook) has
        // already built the Godot ArrayMesh from the decoded PhosMesh. Hand that straight to the renderer. Without
        // this, MeshProvider meshes never rendered and spammed "Unsupported mesh component type MeshProvider". -xlinka
        if (meshComponent is MeshProvider meshProvider)
        {
            if (meshProvider.Asset?.Hook is MeshAssetHook meshAssetHook && meshAssetHook.IsValid)
                return meshAssetHook.GodotMesh;
            // Asset still decoding/uploading (the ArrayMesh builds deferred on the main thread) - not an error;
            // the renderer re-applies when the asset finishes.
            LumoraLogger.Debug("MeshRendererHookBase: MeshProvider mesh asset/hook not ready yet");
            return null!;
        }

        LumoraLogger.Warn($"MeshRendererHookBase: Unsupported mesh component type {meshComponent.GetType().Name}");
        return null!;
    }

    protected virtual void ApplySortingOrder(int sortingOrder)
    {
        ApplyRenderQueue();
    }

    protected virtual void ApplyRenderQueue()
    {
        if (meshInstance == null)
        {
            return;
        }

        var materialQueue = Owner.Material.Asset?.RenderQueue ?? -1;
        var effectiveQueue = (materialQueue >= 0 ? materialQueue : 0) + Owner.SortingOrder.Value;

        // Godot uses SortingOffset for transparent object ordering, while Material.RenderPriority
        // applies per-material. Keep them in sync so Lumora's material RenderQueue affects the
        // renderer as well as the material resource.
        meshInstance.SortingOffset = effectiveQueue;
    }

    // Only answers for the native material path, which is what the primitives and the cloth actually
    // use. A shader material carries its culling in the shader variant it was built from, so it is not
    // readable here - those set their cast mode explicitly instead.
    private bool IsDoubleSidedSurface()
    {
        if (meshInstance == null)
            return false;

        var material = meshInstance.MaterialOverride as StandardMaterial3D
                       ?? meshInstance.GetActiveMaterial(0) as StandardMaterial3D;
        return material != null && material.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
    }

    protected virtual void ApplyShadowCastMode(ShadowCastMode shadowCastMode)
    {
        if (meshInstance != null)
        {
            switch (shadowCastMode)
            {
                case ShadowCastMode.Off:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                    break;
                case ShadowCastMode.On:
                    // A DOUBLE-SIDED SURFACE HAS TO CAST A DOUBLE-SIDED SHADOW.
                    //
                    // The renderer culls back faces during the shadow pass under On, so a surface whose
                    // material draws both sides still casts from one of them. On thin geometry - cloth,
                    // a quad, a disc - that means the shadow disappears or lands on the wrong side the
                    // moment the light is behind it, which reads as the shadow being broken rather than
                    // as a mode being wrong. Promote it rather than making every caller remember. -xlinka
                    meshInstance.CastShadow = IsDoubleSidedSurface()
                        ? GeometryInstance3D.ShadowCastingSetting.DoubleSided
                        : GeometryInstance3D.ShadowCastingSetting.On;
                    break;
                case ShadowCastMode.ShadowOnly:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                    break;
                case ShadowCastMode.DoubleSided:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided;
                    break;
            }
        }
    }

}

