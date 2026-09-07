// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

[ComponentCategory("Assets/Materials")]
public abstract class MaterialProvider : DynamicAssetProvider<MaterialAsset>, IPrimaryColorSource
{
    private Action _assetUpdatedCallback = null!;

    // Indices of this material's own asset references, resolved once off the sync-member table (which is
    // already a cached field array, so this is a scan of a handful of entries, not reflection). Read on
    // the arrival notification that re-drives a renderer, never per frame. -xlinka
    private int[] _assetRefMembers = null!;

    protected abstract MaterialType MaterialType { get; }

    // Whether a renderer may paint the loading skin over a surface this material is still assembling.
    // On for world content, where a checker beats an untextured white blob. OFF for the UI set: a panel
    // whose atlas has not landed shows an empty panel for a frame, which nobody notices - a checkered
    // dashboard, on every open, is a bug report. -xlinka
    public virtual bool UseLoadingPlaceholder => true;

    // False while any texture / cubemap / shader source this material paints with is still arriving.
    // The material ASSET exists long before that - it is created the moment something references it -
    // which is why a renderer cannot just ask IsAssetAvailable and call it ready.
    public bool DependenciesReady
    {
        get
        {
            var members = _assetRefMembers ??= CollectAssetRefMembers();
            for (int i = 0; i < members.Length; i++)
            {
                if (GetSyncMember(members[i]) is IAssetRef reference
                    && AssetReadiness.IsPending(reference.Target, inspectDependencies: false))
                {
                    return false;
                }
            }
            return true;
        }
    }

    // What is holding this material back. Read only by the one-shot log a renderer emits when a surface has
    // worn the loading skin far longer than any load takes - never per frame. -xlinka
    public string DescribePendingDependency()
    {
        var members = _assetRefMembers ??= CollectAssetRefMembers();
        for (int i = 0; i < members.Length; i++)
        {
            if (GetSyncMember(members[i]) is not IAssetRef reference)
                continue;

            var target = reference.Target;
            if (!AssetReadiness.IsPending(target, inspectDependencies: false))
                continue;

            string url = (target as IUrlAssetProvider)?.SourceUrl?.ToString() ?? "<no url>";
            string state = (target?.GenericAsset as Asset)?.LoadState.ToString() ?? "no asset yet";
            return $"{GetSyncMemberName(members[i])} -> {target?.GetType().Name ?? "null"} [{state}] {url}";
        }
        return "nothing pending - the material asset itself has not been built";
    }

    private int[] CollectAssetRefMembers()
    {
        int count = 0;
        int total = SyncMemberCount;
        for (int i = 0; i < total; i++)
        {
            if (GetSyncMember(i) is IAssetRef)
                count++;
        }

        if (count == 0)
            return Array.Empty<int>();

        var indices = new int[count];
        int next = 0;
        for (int i = 0; i < total; i++)
        {
            if (GetSyncMember(i) is IAssetRef)
                indices[next++] = i;
        }
        return indices;
    }

    // Eyedropper source. Every material that already implements ICommonMaterial names its own headline
    // colour there - albedo on the PBS set, tint on the unlit and UI set, the near colour on a fresnel
    // lerp, the line colour on the wireframe - so the default reads that back instead of copying the
    // same three lines into seventeen files. Materials that are NOT ICommonMaterial fall through to
    // false unless they override: a blur renders the screen behind it and its tint is a wash over that,
    // not the colour you are pointing at, so false is the honest answer and the pixel fallback handles
    // it. -xlinka
    // A material whose look is driven by a texture makes its flat colour a lie for sampling: an
    // imported model is usually white albedo under a texture, and an eyedropper answering "white"
    // for every textured thing is what the screen read exists for. -xlinka
    public virtual bool PrimaryColorIsTextured => this is ICommonMaterial common && common.MainTexture != null;

    public virtual bool TryGetPrimaryColor(out colorHDR color)
    {
        if (this is ICommonMaterial common)
        {
            color = common.Color;
            return true;
        }
        color = colorHDR.White;
        return false;
    }

    protected override void OnAssetCreated(MaterialAsset asset)
    {
        asset.SetMaterialType(MaterialType);
    }

    protected override void UpdateAsset(MaterialAsset asset)
    {
        Lumora.Core.Logging.Logger.Debug($"MaterialProvider.UpdateAsset: [{GetType().Name}] Updating material");
        asset.Clear();
        UpdateMaterial(asset);

        if (_assetUpdatedCallback == null)
        {
            _assetUpdatedCallback = () => AssetUpdated();
        }
        Lumora.Core.Logging.Logger.Debug($"MaterialProvider.UpdateAsset: [{GetType().Name}] Calling ApplyChanges");
        asset.ApplyChanges(_assetUpdatedCallback);
    }

    protected abstract void UpdateMaterial(MaterialAsset asset);

    protected override void OnAssetCleared()
    {
    }

    public void ForceUpdate()
    {
        MarkChangeDirty();
    }
}
