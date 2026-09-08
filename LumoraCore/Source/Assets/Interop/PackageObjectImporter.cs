// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Persistence;
using Lumora.Core.Phos;

namespace Lumora.Core.Assets.Interop;

// What actually happened during an import, as opposed to what the inspector predicted.
public sealed class PackageImportResult
{
    public Slot? Root { get; set; }
    public string? FailureReason { get; set; }

    public int SlotsCreated { get; set; }
    public int SlotsPruned { get; set; }

    // Slot trees dropped because they carry something the wearer's own user root already owns.
    public int SlotsConflicting { get; set; }

    // Solid colliders inside an avatar that were turned query-only on the way in.
    public int CollidersSoftened { get; set; }

    // Particle systems that got their sprite back by reading through the style chain.
    public int ParticlesTextured { get; set; }

    // Particle systems that got the mesh they actually draw per particle.
    public int ParticlesMeshed { get; set; }

    // Colour/size/lifetime/etc modules pointed back at the system that owns them.
    public int ParticleModulesWired { get; set; }

    public bool AvatarFinished { get; set; }
    public int ComponentsAttached { get; set; }
    public int ComponentsStripped { get; set; }
    public int ComponentsSkipped { get; set; }

    // How much of the wiring actually landed. A component can attach perfectly and still render
    // nothing if the reference to its mesh or material never resolved, and that failure is otherwise
    // completely silent - which is why it needs counting rather than hoping.
    public int ReferencesResolved { get; set; }
    public int ReferencesDangling { get; set; }

    public int SkeletonsBuilt { get; set; }

    public int MeshesConverted { get; set; }
    public int TexturesImported { get; set; }
    public int AssetsFailed { get; set; }

    public bool Succeeded => Root != null && FailureReason == null;
}

// Builds a Lumora hierarchy from a package's object graph.
//
// The shape of the object - its slots, names and transforms - transfers exactly, because that part of
// both formats is just a tree of named transforms. Assets transfer by being re-encoded into our own
// formats and stored in the local database, so from that point on they behave like anything else
// imported here: cached, peer-transferred, variant-generated.
//
// Components are the lossy part and the code is deliberately honest about it. A component is attached
// only when we have a counterpart AND know how to carry its data across; anything else is counted and
// reported rather than attached half-configured, because an empty component that looks right in the
// inspector but does nothing is worse than a visible gap.
//
// Scripted behaviour is stripped and the slots that existed only to hold it are pruned. -xlinka
public static class PackageObjectImporter
{
    public static async Task<PackageImportResult> ImportAsync(string file, Slot parent)
    {
        var result = new PackageImportResult();
        if (parent == null || parent.IsDestroyed)
        {
            result.FailureReason = "No slot to import into.";
            return result;
        }

        var world = parent.World;
        using var archive = PackageArchive.Open(file);

        var record = archive.MainRecord;
        if (record == null || !record.IsObject)
        {
            result.FailureReason = record == null
                ? "Package has no main record."
                : $"Main record is a '{record.RecordType}' package; only object packages convert.";
            return result;
        }

        string? graphSignature = PackageArchive.SignatureOf(TryUri(record.AssetUri));
        if (graphSignature == null || !archive.HasAsset(graphSignature))
        {
            result.FailureReason = "Main record does not point at an object graph inside this package.";
            return result;
        }

        DataTreeDictionary graph;
        try
        {
            graph = PackageGraphReader.Read(archive.ReadAsset(graphSignature));
        }
        catch (Exception ex)
        {
            result.FailureReason = "Could not decode the object graph: " + ex.Message;
            return result;
        }

        var typeTable = ReadTypeTable(graph);

        // Assets first: everything the tree points at must already exist in the local database before a
        // component can be pointed at it.
        var bindPoses = new Dictionary<string, Dictionary<string, float4x4>>(StringComparer.OrdinalIgnoreCase);
        var assetUrls = await ConvertAssetsAsync(archive, graph, result, bindPoses).ConfigureAwait(false);

        await WorldContext.ToWorld();
        if (parent.IsDestroyed)
        {
            result.FailureReason = "The target slot went away during import.";
            return result;
        }

        if (graph.TryGetNode("Object") is not DataTreeDictionary objectNode)
        {
            result.FailureReason = "The object graph has no root object.";
            return result;
        }

        var context = new BuildContext(world, typeTable, assetUrls, result);
        foreach (var pair in bindPoses)
            context.MeshBindPoses[pair.Key] = pair.Value;
        var root = parent.AddSlot(CleanName(record.Name));
        if (!await BuildSlotAsync(objectNode, root, context))
        {
            // Nothing in the whole package survived the strip - do not leave an empty slot behind.
            root.Destroy();
            result.FailureReason = "Everything in this package was stripped; nothing left to import.";
            return result;
        }

        await AttachLooseAssetsAsync(graph, root, context);
        ResolveLinks(context);
        BuildSkeletons(root, context);
        WireParticleLook(context);
        FinishAvatar(root, context);

        // The root's saved Position is wherever the object happened to be standing in the world it was
        // packaged from, and it is meaningless here. The dova dog package carries (-4.27, 12.60,
        // -156.62), so the import landed 157 metres out and 12 up - visible as a speck on the horizon
        // rather than in front of whoever asked for it.
        //
        // Rotation and scale are NOT touched: those are how the author built the thing (this one is
        // scaled 1.25 and yawed about 101 degrees) and dropping them would arrive at the right place
        // wearing the wrong pose. Only the position is stale. The source platform does the same by
        // re-stamping GlobalPosition on the slot right after its load returns. -xlinka
        float3 packagedPosition = root.LocalPosition.Value;
        if (packagedPosition != float3.Zero)
        {
            root.LocalPosition.Value = float3.Zero;
            Logger.Log($"Package import: dropped the packaged root position {packagedPosition} so it lands on the import anchor (the dialog panel) instead.");
        }

        result.Root = root;
        Logger.Log($"Package import: {result.SlotsCreated} slots, {result.ComponentsAttached} components, "
                 + $"{result.MeshesConverted} meshes, {result.TexturesImported} textures "
                 + $"({result.SlotsPruned} slots pruned, {result.SlotsConflicting} orphan/userspace trees dropped, "
                 + $"{result.ComponentsStripped} stripped, {result.ComponentsSkipped} skipped); "
                 + $"references {result.ReferencesResolved} wired, {result.ReferencesDangling} dangling");
        Logger.Log($"Package import: {result.SkeletonsBuilt} skeleton(s) built for skinned meshes");
        if (result.ParticlesTextured > 0 || result.ParticlesMeshed > 0 || result.ParticleModulesWired > 0)
            Logger.Log($"Package import: particles - {result.ParticleModulesWired} modules wired, "
                     + $"{result.ParticlesMeshed} got their mesh, {result.ParticlesTextured} got their texture");
        LogRendererWiring(result.Root);
        LogMaterialReferences(result.Root, context);
        return result;
    }

    // Meshes and materials do NOT live in the slot tree. The graph root carries a separate top-level
    // "Assets" list of bare components, and every renderer in the hierarchy references them by id. Build
    // only the tree and those references have nothing to point at: on the test avatar that was all 30
    // mesh references, all 5 skinned mesh references and 51 material references dangling, so the shapes
    // came through and nothing had geometry or a material to render with.
    //
    // They are attached to one slot under the import root, which also keeps them together the way the
    // package stored them. -xlinka
    private static async Task AttachLooseAssetsAsync(DataTreeDictionary graph, Slot root, BuildContext context)
    {
        var list = graph.TryGetNode("Assets") as DataTreeList
                   ?? graph.TryGetNode("Dependencies") as DataTreeList;
        if (list == null || list.Count == 0)
            return;

        // Tagged so the mesh hook knows this slot is a LIBRARY, not something anyone is looking at.
        //
        // Every mesh in the package lands here as a bare component with no renderer beside it, and the
        // mesh hook's convenience preview draws exactly that case: a lit grey copy of the mesh at the
        // object's full scale, parked at the import root. On this avatar it was a waist-high white
        // sphere standing next to the fox with nothing referencing it. A mesh in an asset list is
        // storage; drawing it was never the intent. -xlinka
        var holder = root.AddSlot("Assets");
        holder.Tag.Value = AssetLibraryTag;

        foreach (var entry in list.Children)
        {
            // Every provider built here starts loading its asset, so this is the burst that actually
            // costs frames - a couple of hundred meshes and textures asking for decode at once. Yield
            // on the same budget as the tree build.
            if (++context.SlotsThisFrame >= SlotsPerFrame)
            {
                context.SlotsThisFrame = 0;
                await WorldContext.NextUpdate();
                if (holder.IsDestroyed)
                    return;
            }

            if (entry is not DataTreeDictionary worker)
                continue;
            if (worker.TryGetNode("Type") is not DataTreeValue typeValue)
                continue;

            int index = SafeInt(typeValue);
            if (index < 0 || index >= context.Types.Count)
                continue;

            var resolution = context.Types[index];
            if (resolution.Fate == PackageTypeFate.Stripped)
            {
                context.Result.ComponentsStripped++;
                continue;
            }

            var data = worker.TryGetNode("Data") as DataTreeDictionary;

            // Kept whether or not the component itself survives - the whole point is the links we cannot
            // otherwise follow.
            if (data != null && PassThroughTypes.Contains(resolution.SimpleName)
                && (data.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is string passId
                && passId.Length > 0)
            {
                context.PassThrough[passId] = data;
            }

            if (resolution.Target == null || data == null)
            {
                context.Result.ComponentsSkipped++;
                continue;
            }

            if (!TryAttach(holder, resolution, data, context))
                context.Result.ComponentsSkipped++;
        }
    }

    // Give every skinned mesh a skeleton to hang off.
    //
    // Packages carry bones as ordinary slots and reference them directly - there is nothing in the file
    // that corresponds to our SkeletonBuilder, because building the platform skeleton is our own step.
    // Without one the skinned path bails before it builds anything (it needs a Skeleton3D to hang the
    // skin on), so the body and every other skinned surface rendered nothing.
    //
    // Two rules learned the hard way:
    //
    // ONE SKELETON PER RENDERER, never a shared one. Bind poses are per-mesh and bone NAMES collide
    // across meshes on the same character - this avatar has a "Head" and a "Jaw" in both the body and
    // the tail-maw rigs. Merging them put two bones called "Head" in one skeleton, and since lookup is
    // by name the second mesh bound to the first mesh's bones and deformed into nonsense.
    //
    // BONES COME FROM THE RENDERER'S OWN LIST, never from walking the slot hierarchy. Walking sweeps up
    // meshes, props and attachment points as joints - 493 "bones" from Hips here - and the per-frame
    // skeleton update then cost 40-160ms forever.
    //
    // The builder sits on the MODEL ROOT (the nearest slot containing both the mesh and its bones,
    // typically the armature) because SkeletonHook derives its orientation offset from the gap between
    // the builder's slot and the root bone's parent. Put it anywhere else and the skin renders offset
    // from the bones. -xlinka
    // Give an imported particle system its sprite back.
    //
    // Their look is three components away from the system that owns it:
    //
    //     ParticleSystem.Style -> ParticleStyle.Renderer -> <renderer>.Material -> its texture
    //
    // The middle two have no counterpart here, so they never attach, and the reference pass that wires
    // everything else stops dead at the first missing link. The system arrives with no texture at all,
    // which is why an imported effect renders as a swarm of untextured beads. The materials themselves
    // DO import, so once the chain is walked in the source data the texture is a component we already
    // hold.
    //
    // Only the sprite is recovered. A renderer that draws an arbitrary MESH per particle has nowhere to
    // land - ours draws a quad or a sphere - so those systems get the material's texture on a billboard,
    // which is closer than nothing but is not what the author drew. -xlinka
    private static void WireParticleLook(BuildContext context)
    {
        foreach (var (system, data) in context.ParticleSystems)
        {
            if (system.IsDestroyed)
                continue;

            string name = system.Slot?.SlotName.Value ?? "?";

            if (ReadValue(data, "Style") is not string styleId
                || !context.PassThrough.TryGetValue(styleId, out var style))
            {
                Logger.Log($"Package import: particle '{name}' - no style component to read through");
                continue;
            }

            if (ReadValue(style, "Renderer") is not string rendererId
                || !context.PassThrough.TryGetValue(rendererId, out var renderer))
            {
                Logger.Log($"Package import: particle '{name}' - style has no renderer");
                continue;
            }

            // THE MODULES. Colour, size, lifetime, speed, rotation, gravity, trails, ribbons - all of it.
            //
            // Their modules do not know which system they belong to: the STYLE owns them as a plain list,
            // and not one of them carries a System reference. Ours is the other way round - every
            // ParticleModuleBase has a SyncRef<ParticleSystem> and registers itself with whatever it
            // points at. So there was nothing for the member mapper to map, System stayed null on all 29,
            // not a single module ever registered, and every imported system ran on pure component
            // defaults. That is white particles at a default size, no matter how many member names line
            // up. Walk the style's list and point each one at its system. -xlinka
            int modulesWired = 0;
            if (style.TryGetNode("Modules") is DataTreeDictionary modulesHolder
                && modulesHolder.TryGetNode("Data") is DataTreeList moduleList)
            {
                foreach (var entry in moduleList.Children)
                {
                    string? moduleId = entry switch
                    {
                        DataTreeDictionary holder => (holder.TryGetNode("Data") as DataTreeValue)?.Value?.ToString(),
                        DataTreeValue value => value.Value?.ToString(),
                        _ => null,
                    };

                    if (string.IsNullOrEmpty(moduleId) || !context.ById.TryGetValue(moduleId, out var moduleElement))
                        continue;
                    if (moduleElement is not ParticleModuleBase module || module.IsDestroyed)
                        continue;
                    if (module.System.Target != null)
                        continue;

                    module.System.Target = system;
                    modulesWired++;
                }
            }
            context.Result.ParticleModulesWired += modulesWired;

            // The MESH the renderer draws per particle. This is the one that matters: most effects on a
            // real avatar are a mesh renderer, not a billboard, and without it every one of them is a ball.
            string meshResult = "none";
            if (system.ParticleMesh.Target == null && system.ParticleMeshAsset.Target == null
                && ReadValue(renderer, "Mesh") is string meshId
                && context.ById.TryGetValue(meshId, out var meshElement)
                && meshElement is Component meshComponent && !meshComponent.IsDestroyed)
            {
                // A provider needs the ASSET reference to decode at all; a procedural mesh needs only to
                // be pointed at.
                if (meshComponent is IAssetProvider<MeshDataAsset> meshProvider)
                {
                    system.ParticleMeshAsset.Target = meshProvider;
                }
                else
                {
                    system.ParticleMesh.Target = meshComponent;
                    // ParticleSystem re-drives this itself on the reference change, but only once it has
                    // started; a system attached in the same frame this runs has not. Belt and braces on
                    // the path that actually produced the floating grey disc.
                    (meshComponent as ImplementableComponent)?.RunApplyChanges();
                }

                context.Result.ParticlesMeshed++;
                meshResult = meshComponent.GetType().Name;
            }

            // The sprite, which lives another hop away on the renderer's material.
            string textureResult = "none";
            if (system.Texture.Target == null
                && ReadValue(renderer, "Material") is string materialId
                && context.ById.TryGetValue(materialId, out var materialElement)
                && materialElement is Component material && !material.IsDestroyed)
            {
                var texture = FirstTexture(material);
                if (texture != null)
                {
                    system.Texture.Target = texture;
                    context.Result.ParticlesTextured++;
                    textureResult = material.GetType().Name;
                }
                else
                {
                    textureResult = material.GetType().Name + " (no texture)";
                }

                // And the material itself, which is how the source platform draws particles in the first
                // place - it has no particle shader, only a renderer pointed at ordinary material.
                if (system.Material.Target == null && material is IAssetProvider<MaterialAsset> materialProvider)
                {
                    system.Material.Target = materialProvider;
                    textureResult += " (used as material)";
                }

                // The material also decides how these blend, and getting that wrong is the difference
                // between a splash and half a splash.
                if (BlendModeOf(material) is { } blend)
                {
                    system.BlendMode.Value = blend;
                    textureResult += $" blend={blend}";
                }
            }

            Logger.Log($"Package import: particle '{name}' - {modulesWired} module(s) wired, "
                     + $"mesh={meshResult} texture={textureResult}");
        }
    }

    // Their particle materials carry a BlendMode member by that exact name; anything without one keeps
    // our additive default.
    private static Assets.BlendMode? BlendModeOf(Component material)
    {
        for (int i = 0; i < material.SyncMemberCount; i++)
        {
            if (!string.Equals(material.GetSyncMemberName(i), "BlendMode", StringComparison.Ordinal))
                continue;
            if (material.GetSyncMember(i) is Sync<Assets.BlendMode> blend)
                return blend.Value;
        }
        return null;
    }

    // The material's headline map. Their particle materials are usually unlit with a plain Texture, but
    // a PBS one names it AlbedoTexture, so the preferred names are tried in order before falling back to
    // whatever texture reference the material happens to carry first.
    private static readonly string[] TextureMemberOrder = { "Texture", "AlbedoTexture", "MainTexture" };

    private static IAssetProvider<TextureAsset>? FirstTexture(Component material)
    {
        foreach (var preferred in TextureMemberOrder)
        {
            for (int i = 0; i < material.SyncMemberCount; i++)
            {
                if (!string.Equals(material.GetSyncMemberName(i), preferred, StringComparison.Ordinal))
                    continue;
                if (material.GetSyncMember(i) is AssetRef<TextureAsset> { Target: { } named })
                    return named;
            }
        }

        for (int i = 0; i < material.SyncMemberCount; i++)
        {
            if (material.GetSyncMember(i) is AssetRef<TextureAsset> { Target: { } any })
                return any;
        }
        return null;
    }

    // Leave an imported avatar in the state the creator would have left it in.
    //
    // Everything needed to BE an avatar arrives - rig, skeleton, bone map - but nothing that says it IS
    // one. The context menu offers "Equip Avatar" only where it finds an AvatarForm, and their marker
    // component has no counterpart here, so a perfectly good avatar imports and then simply cannot be
    // worn. The rest of this mirrors AvatarStudio.RunCreate: facing, references, a root grab. Equip
    // itself still does the IK, hand posers and face drivers, so none of that is repeated here.
    private static void FinishAvatar(Slot root, BuildContext context)
    {
        HumanoidRig? rig = null;
        foreach (var candidate in root.GetComponentsInChildren<HumanoidRig>())
        {
            if (candidate.IsHumanoid)
            {
                rig = candidate;
                break;
            }
        }

        var avatar = rig?.Slot;
        if (rig == null || avatar == null || avatar.IsDestroyed)
            return;
        var skeleton = avatar.GetComponentInChildren<SkeletonBuilder>();
        if (skeleton == null)
            return;

        // The avatar keeps the pose its author left it in, and that is deliberate.
        //
        // This used to force the bind pose here, on the theory that calibration wants a clean T-pose.
        // A bind pose is a RIGGING pose, not a stance: measured on a real avatar its legs are 97%
        // straight where the authored legs are 87%, and the entire digitigrade bend lives in the
        // difference - forcing bind stood the cat up like a person. Calibrating in bind and restoring
        // afterwards is no better, it just moves the damage: the reference points would then be baked
        // at bone positions the avatar never stands at, and the view would sit where the bind head was.
        //
        // Nothing here needs a T-pose anyway. The facing comes from the rig's own ForwardAxis, and the
        // view/grip references come from bone DIRECTIONS, which a normal standing pose gives perfectly
        // well. An avatar genuinely saved in a cursed pose is what AvatarIK.ForceTpose is for, and it
        // stays opt-in. -xlinka
        SoftenAvatarColliders(avatar, context);

        if (avatar.GetComponent<AvatarForm>() == null)
            avatar.AttachComponent<AvatarForm>();

        // FOUR LEGS TAKE A DIFFERENT SOLVER, and that has to be decided HERE.
        //
        // The model-import path has branched on this for a while; this one never did, so an avatar that
        // arrived in a package was set up as a person no matter what it was. It got humanoid facing and
        // humanoid reference placement, and the quadruped rig was not built at all - not until the thing
        // was equipped, because that was the only other site that ever called TryAttachFor. So an
        // imported quadruped sat in the world mis-rigged, and "the legs do not import correctly" was
        // exactly and literally true.
        //
        // TryAttachFor returns null for anything that is not a quadruped, so the humanoid path below is
        // untouched for every avatar that really is one. -xlinka
        var quadrupedIk = Components.Avatar.QuadrupedIK.TryAttachFor(avatar, skeleton, rig);
        if (quadrupedIk == null)
        {
            // Sides from GEOMETRY before anything trusts the labels. A package from a left-handed source
            // arrives reflected with its bone names unchanged: on a real avatar every Left_* bone sat on
            // the physical right and every Right_* on the physical left, arms, legs and eyes alike, so a
            // tool equipped on the right hand raised the arm on the left and crossed the chest to reach
            // it. This resolves physical right from the eyes, toes and knees, none of which carry a
            // label, and swaps whole pairs when the labels disagree. Correct rigs are a logged no-op.
            // -xlinka
            rig.ValidateLimbSides(skeleton);

            // Facing first, then references: equip resets the root to identity, so a reference baked against
            // a stale frame wears the avatar yawed off the user's forward.
            AvatarCalibration.AlignAvatarFacing(avatar, rig);
            // Feet and pelvis off, matching the creator's own defaults: those are opt-in calibration for
            // full-body tracking, and placing them unasked gives a 3-point user targets they never set.
            AvatarCalibration.AutoPlaceReferences(avatar, rig, feet: false, pelvis: false);
        }

        var grab = avatar.GetComponent<Grabbable>() ?? avatar.AttachComponent<Grabbable>();
        grab.BlockWhenWorn.Value = true;

        context.Result.AvatarFinished = true;
        Logger.Log($"Package import: avatar ready to equip - {(quadrupedIk != null ? "QUADRUPED, " : "")}{rig.Bones.Count} rig bones"
                 + (rig.TryGetBone(Lumora.Core.Input.BodyNode.LeftEye) != null || rig.TryGetBone(Lumora.Core.Input.BodyNode.RightEye) != null
                     ? ", eyes mapped" : ", no eye bones")
                 + $", authored stance kept"
                 + $", {context.Result.CollidersSoftened} collider(s) made query-only");
    }

    // An avatar's own colliders are there to be POINTED AT, not walked into.
    //
    // Their static colliders become StaticBody3D here, which is a wall: an imported avatar standing in
    // the world is a body-shaped lump of level geometry that shoves you around, and wearing it does not
    // help because the wearer collides with their own ribcage. Query-only is the same answer we already
    // reach for on our own generated bone capsules - still hit by the laser, by grab and by touch, never
    // by a character controller. Deliberately NOT applied to Active colliders, which are authored
    // physics props and are meant to have mass. -xlinka
    private static void SoftenAvatarColliders(Slot avatar, BuildContext context)
    {
        foreach (var collider in avatar.GetComponentsInChildren<Collider>())
        {
            if (collider.IsDestroyed || collider.Type.Value != Lumora.Core.Physics.ColliderType.Static)
                continue;

            collider.Type.Value = Lumora.Core.Physics.ColliderType.Trigger;
            context.Result.CollidersSoftened++;
        }
    }

    private static void BuildSkeletons(Slot root, BuildContext context)
    {
        // Snapshot the renderers BEFORE building anything. Each skeleton adds a slot and attaches a
        // component, which mutates the very hierarchy the search is walking - enumerating it live threw
        // "Collection was modified" and failed the whole import.
        var pending = new List<SkinnedMeshRenderer>();
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (renderer.Skeleton.Target == null && renderer.Bones.Count > 0)
                pending.Add(renderer);
        }

        foreach (var renderer in pending)
        {
            if (renderer.IsDestroyed || renderer.Slot == null)
                continue;

            var bones = new List<Slot>();
            foreach (var bone in renderer.Bones)
            {
                if (bone != null && !bone.IsDestroyed)
                    bones.Add(bone);
            }
            if (bones.Count == 0)
                continue;

            var rigRoot = Topmost(bones);
            if (rigRoot == null)
                continue;

            // The builder sits directly under the ROOT BONE'S PARENT, and this is load-bearing.
            // SkeletonHook offsets the Skeleton3D by `inverse(builderSlot.global) * firstBone.Parent.global`
            // - note it takes the FIRST BONE ADDED, not RootBone. Parenting the builder anywhere else
            // leaves a non-identity offset that shifts the whole skin away from its bones, which is what
            // put the tail in the wrong place. A plain child slot has an identity local transform, so
            // this makes that offset exactly identity - which is what the bind-derived rests below want,
            // since they already place the skin in the mesh's own bind space. -xlinka
            var skeletonParent = rigRoot.Parent ?? NearestShared(renderer.Slot, rigRoot) ?? rigRoot;
            var holder = skeletonParent.AddSlot("Skeleton");
            var builder = holder.AttachComponent<SkeletonBuilder>();

            builder.ClearBones();
            builder.RootBone.Target = rigRoot;

            // Parents before children: the skeleton is built in order, and a bone whose parent has not
            // been added yet has nothing to attach to.
            bones.Sort((a, b) => Depth(a).CompareTo(Depth(b)));

            // Rest poses come from the MESH'S BIND POSES, not from the bone slots' transforms.
            //
            // Skinning is only correct when a bone's accumulated global rest is the exact inverse of its
            // bind pose - that is literally what SkinnedMeshHook's canary checks. Slot transforms do not
            // satisfy that: the saved pose is not the bind pose, intermediate non-bone slots drop out of
            // the chain, and any scale on the way down compounds. Reading it off the bind pose makes the
            // identity hold by construction, for every bone, whatever the hierarchy did.
            //
            //     globalRest = bind^-1        so       localRest = bind_parent * bind^-1
            //
            // A bone the mesh does not bind falls back to its slot transform - it deforms nothing, it
            // just needs to keep the chain intact for its children. -xlinka
            var binds = BindPosesFor(renderer, context);
            var boneSet = new HashSet<Slot>(bones);

            foreach (var bone in bones)
            {
                float4x4 rest;
                string boneName = bone.SlotName.Value;

                if (binds != null && binds.TryGetValue(boneName, out var bind))
                {
                    var parentBone = NearestBoneAncestor(bone, boneSet);
                    if (parentBone != null && binds.TryGetValue(parentBone.SlotName.Value, out var parentBind))
                        rest = parentBind * bind.Inverse;
                    else
                        rest = bind.Inverse;
                }
                else
                {
                    rest = float4x4.Translate(bone.LocalPosition.Value)
                         * float4x4.Rotate(bone.LocalRotation.Value)
                         * float4x4.Scale(bone.LocalScale.Value);
                }

                builder.AddBone(boneName, bone, rest);
            }

            builder.IsBuilt.Value = true;
            builder.BoneHierarchyChanged = true;
            renderer.Skeleton.Target = builder;

            context.Result.SkeletonsBuilt++;
            Logger.Log($"Package import: skeleton for '{renderer.Slot.SlotName.Value}' - {bones.Count} bones, "
                     + $"root '{rigRoot.SlotName.Value}' under '{skeletonParent.SlotName.Value}'"
                     + (binds != null ? ", rests from bind poses" : ", rests from slot transforms (no bind table)"));
        }
    }

    // The bind table for whatever mesh this renderer is actually showing.
    private static Dictionary<string, float4x4>? BindPosesFor(SkinnedMeshRenderer renderer, BuildContext context)
    {
        var provider = renderer.MeshAsset.Target;
        var url = (provider as StaticAssetProvider<MeshDataAsset>)?.URL.Value;
        if (url == null)
            return null;
        return context.MeshBindPoses.GetValueOrDefault(url.OriginalString);
    }

    // The closest ancestor that is also a bone of this skeleton - which is what the skeleton will use
    // as the parent, regardless of how many non-bone slots sit between them.
    private static Slot? NearestBoneAncestor(Slot bone, HashSet<Slot> bones)
    {
        for (var walk = bone.Parent; walk != null; walk = walk.Parent)
        {
            if (bones.Contains(walk))
                return walk;
        }
        return null;
    }

    // The bone nearest the top of the hierarchy - the rig root.
    private static Slot? Topmost(List<Slot> bones)
    {
        Slot? best = null;
        int bestDepth = int.MaxValue;
        foreach (var bone in bones)
        {
            int depth = Depth(bone);
            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = bone;
            }
        }
        return best;
    }

    private static Slot? NearestShared(Slot? a, Slot? b)
    {
        if (a == null || b == null)
            return null;

        var ancestors = new HashSet<Slot>();
        for (var walk = a; walk != null; walk = walk.Parent)
            ancestors.Add(walk);
        for (var walk = b; walk != null; walk = walk.Parent)
        {
            if (ancestors.Contains(walk))
                return walk;
        }
        return null;
    }

    private static int Depth(Slot slot)
    {
        int depth = 0;
        for (var walk = slot?.Parent; walk != null; walk = walk.Parent)
            depth++;
        return depth;
    }

    // The one thing that decides whether any of this is visible: did the renderers actually end up
    // holding a mesh and a material. Counted after the fact from the built tree rather than trusted.
    private static void LogRendererWiring(Slot root)
    {
        int renderers = 0, withMesh = 0, withMaterial = 0;
        int skinned = 0, skinnedWithMesh = 0, skinnedWithBones = 0;

        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            renderers++;
            if (renderer.Mesh.Target != null) withMesh++;
            if (renderer.Materials.Count > 0) withMaterial++;
        }
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            skinned++;
            if (renderer.MeshAsset.Target != null) skinnedWithMesh++;
            if (renderer.Bones.Count > 0) skinnedWithBones++;
        }

        Logger.Log($"Package import: {withMesh}/{renderers} mesh renderers have a mesh, {withMaterial} have a material; "
                 + $"{skinnedWithMesh}/{skinned} skinned renderers have a mesh, {skinnedWithBones} have bones");

        // Name every renderer that got geometry but no material.
        //
        // This is not a cosmetic gap. With no material we hand Godot a null surface override and it
        // substitutes its own default white-grey, so the thing draws as a big pale untextured blob that
        // rides along wherever the object goes. Counting them told us one existed and nothing about
        // WHICH, and hunting a nameless grey ball through a world is miserable. -xlinka
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            if (renderer.Mesh.Target == null || renderer.Materials.Count > 0)
                continue;
            Logger.Warn($"Package import: renderer '{PathOf(renderer.Slot)}' has mesh "
                      + $"{renderer.Mesh.Target.GetType().Name} but NO material; it would draw as an "
                      + "untextured grey default, so it is left hidden");
        }

        // Every imported material's transparency and culling, by name.
        //
        // A fur or hair shell is alpha-cut geometry: hundreds of cards that are mostly transparent. If
        // the imported material lands on Opaque, every card renders solid and the mesh becomes a thick
        // block hugging the body, which reads as a fat black outline rather than as fur. Guessing at
        // which of the material properties failed to carry cost more time than printing them ever will.
        // -xlinka
        foreach (var material in root.GetComponentsInChildren<MaterialProvider>())
        {
            if (material == null || material.IsDestroyed)
                continue;
            string alpha = "n/a";
            string cull = "n/a";
            var type = material.GetType();
            if (type.GetField("AlphaMode")?.GetValue(material) is Sync<AlphaMode> am)
                alpha = am.Value.ToString();
            else if (type.GetField("BlendMode")?.GetValue(material) is Sync<Assets.BlendMode> bm)
                alpha = bm.Value.ToString();
            if (type.GetField("Culling")?.GetValue(material) is Sync<Culling> cu)
                cull = cu.Value.ToString();
            Logger.Log($"Package import: material '{material.Slot?.SlotName.Value}' {type.Name} "
                     + $"alpha={alpha} culling={cull}");
        }
    }

    // Every asset reference on every imported material, one line each, grep "MATREF:".
    //
    // A material reports READY the moment none of its references is still loading, and an unset
    // reference is not loading - so a material whose albedo link was lost in the wiring pass looks
    // exactly like one whose author left the albedo empty: both render the flat colour, both pass the
    // readiness check, and neither says a word. The link pass counts its failures in aggregate, which
    // told us 103 references dangled on one avatar and nothing about which slot on which material.
    // This is the per-slot answer, taken once at the end of the import, with the texture's load state
    // at that moment so a slow decode and a lost link read differently. -xlinka
    private static void LogMaterialReferences(Slot root, BuildContext context)
    {
        int materials = 0, wired = 0, dangling = 0, empty = 0, pending = 0;

        foreach (var material in root.GetComponentsInChildren<MaterialProvider>())
        {
            if (material == null || material.IsDestroyed)
                continue;
            materials++;
            string owner = $"'{material.Slot?.SlotName.Value}' {material.GetType().Name}";

            for (int i = 0; i < material.SyncMemberCount; i++)
            {
                if (material.GetSyncMember(i) is not IAssetRef reference)
                    continue;
                string member = material.GetSyncMemberName(i);

                if (reference is ISyncRef syncRef && context.RefFailures.TryGetValue(syncRef, out var why))
                {
                    dangling++;
                    Logger.Warn($"MATREF: {owner}.{member} -> DANGLING: {why}");
                    continue;
                }

                var target = reference.Target;
                if (target == null)
                {
                    empty++;
                    Logger.Log($"MATREF: {owner}.{member} -> empty (authored null)");
                    continue;
                }

                wired++;
                string state;
                string url = "";
                if (target is IUrlAssetProvider urlProvider)
                {
                    if (urlProvider.IsLoadPending)
                        pending++;
                    url = " url=" + (urlProvider.SourceUrl?.ToString() ?? "<none>");
                    state = target is StaticAssetProvider<TextureAsset> texture ? texture.LoadStateDescription
                        : target is StaticAssetProvider<CubemapAsset> cubemap ? cubemap.LoadStateDescription
                        : urlProvider.IsLoadPending ? "pending" : "ready";
                }
                else
                {
                    state = target.IsAssetAvailable ? "available (procedural)" : "not built yet (procedural)";
                }

                var targetComponent = target as Component;
                string home = targetComponent?.Slot != null ? $" on '{PathOf(targetComponent.Slot)}'" : "";
                Logger.Log($"MATREF: {owner}.{member} -> {target.GetType().Name} [{state}]{url}{home}");
            }
        }

        Logger.Log($"MATREF: summary - {materials} materials, {wired} asset refs wired ({pending} still loading at import end), "
                 + $"{dangling} dangling, {empty} authored empty");
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    // Read by MeshHook to suppress its default preview instance. Kept here beside the code that sets it.
    public const string AssetLibraryTag = "lumora.assetlibrary";

    private static string PathOf(Slot? slot)
    {
        if (slot == null)
            return "(none)";
        var parts = new System.Collections.Generic.List<string>();
        for (var s = slot; s != null && parts.Count < 12; s = s.Parent)
            parts.Add(s.SlotName.Value ?? "?");
        parts.Reverse();
        return string.Join("/", parts);
    }

    private sealed class BuildContext
    {
        public readonly World World;
        public readonly List<PackageTypeMap.Resolution> Types;
        public readonly Dictionary<string, Uri> AssetUrls;
        public readonly PackageImportResult Result;

        // Their element id -> the thing we built for it, so references can be reconnected once the
        // whole tree exists. SLOTS go in here as well as components: bone lists on a skinned renderer
        // are slot references, and with only components recorded they resolved to nothing and the mesh
        // stayed in its bind pose. -xlinka
        public readonly Dictionary<string, IWorldElement> ById = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PendingLink> Links = new();
        public readonly List<PendingRef> RefLinks = new();

        // Why a single reference stayed unset, keyed by the reference itself. A dangling count says
        // how many links failed and nothing about which; the material diagnostic at the end reads
        // this to say, per texture slot, whether the author left it empty or the wiring lost it.
        public readonly Dictionary<ISyncRef, string> RefFailures = new(ReferenceEqualityComparer.Instance);

        // Raw member data for components we do NOT attach but still have to read THROUGH. A particle's
        // look is not on the particle system: it hangs off a style, which hangs off a renderer, which
        // finally names the material. Two of those three links have no counterpart here, and a chain
        // that stops at the first unsupported link takes the sprite with it.
        public readonly Dictionary<string, DataTreeDictionary> PassThrough = new(StringComparer.OrdinalIgnoreCase);

        public readonly List<(Components.ParticleSystem System, DataTreeDictionary Data)> ParticleSystems = new();

        // Bone name -> bind pose, per converted mesh, keyed by the local asset URL we wrote it to.
        // Captured at decode time because the skeleton has to be built before the provider has
        // finished loading the asset back.
        public readonly Dictionary<string, Dictionary<string, float4x4>> MeshBindPoses =
            new(StringComparer.OrdinalIgnoreCase);

        // Slots built since the last yield back to the update loop.
        public int SlotsThisFrame;

        public BuildContext(World world, List<PackageTypeMap.Resolution> types,
            Dictionary<string, Uri> assetUrls, PackageImportResult result)
        {
            World = world;
            Types = types;
            AssetUrls = assetUrls;
            Result = result;
        }
    }

    private readonly struct PendingLink
    {
        public readonly Component Owner;
        public readonly object List;
        public readonly string TargetId;

        public PendingLink(Component owner, object list, string targetId)
        {
            Owner = owner;
            List = list;
            TargetId = targetId;
        }
    }

    // SyncRefList<T>.Add(T) and SyncAssetList<A>.Add(IAssetProvider<A>) have different signatures, so
    // pick whichever public Add the list actually exposes that will take this target.
    private static bool AppendToList(object list, IWorldElement target)
    {
        foreach (var method in list.GetType().GetMethods())
        {
            if (method.Name != "Add")
                continue;
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                continue;
            if (!parameters[0].ParameterType.IsInstanceOfType(target))
                continue;
            method.Invoke(list, new object[] { target });
            return true;
        }
        return false;
    }

    private readonly struct PendingRef
    {
        public readonly ISyncRef Reference;
        public readonly string TargetId;

        public PendingRef(ISyncRef reference, string targetId)
        {
            Reference = reference;
            TargetId = targetId;
        }
    }

    // References between components are stored as the target's own id, so they can only be reconnected
    // once every component exists. This is the pass that makes an imported object VISIBLE rather than
    // merely present: a renderer with no mesh and no material renders nothing at all, and both of those
    // are references. -xlinka
    private static void ResolveLinks(BuildContext context)
    {
        // Single references first: meshes on renderers, textures on materials. TrySet refuses a target
        // of the wrong type on its own, so a mismatch costs one silent skip rather than an exception.
        foreach (var pending in context.RefLinks)
        {
            if (!context.ById.TryGetValue(pending.TargetId, out var target))
            {
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} was never built (stripped, skipped or on a dropped slot)";
                continue;
            }
            if (target.IsDestroyed)
            {
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} ({target.GetType().Name}) was built and then destroyed (pruned slot)";
                continue;
            }
            try
            {
                if (pending.Reference.TrySet(target))
                {
                    context.Result.ReferencesResolved++;
                }
                else
                {
                    context.Result.ReferencesDangling++;
                    context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} is a {target.GetType().Name}, which this reference does not accept";
                }
            }
            catch (Exception ex)
            {
                context.Result.ReferencesDangling++;
                context.RefFailures[pending.Reference] = $"target {ShortId(pending.TargetId)} threw on assignment: {ex.Message}";
            }
        }

        // Then reference LISTS, appended in order. Resolved by reflection rather than by naming each
        // component: bone lists, material lists and anything else list-shaped all work the same way,
        // and hardcoding the one case we happened to need first is how bones got missed.
        foreach (var link in context.Links)
        {
            if (link.Owner.IsDestroyed)
                continue;
            if (!context.ById.TryGetValue(link.TargetId, out var target) || target.IsDestroyed)
            {
                context.Result.ReferencesDangling++;
                continue;
            }

            try
            {
                if (AppendToList(link.List, target))
                    context.Result.ReferencesResolved++;
                else
                    context.Result.ReferencesDangling++;
            }
            catch { context.Result.ReferencesDangling++; }
        }
    }

    // Copy every member whose name and shape line up, and queue the references for the second pass.
    private static void CopyMembers(Component component, DataTreeDictionary data, BuildContext context)
    {
        PackageMemberMapper.Apply(
            component,
            data,
            (reference, id) => context.RefLinks.Add(new PendingRef(reference, id)),
            (member, name, ids) =>
            {
                foreach (var id in ids)
                    context.Links.Add(new PendingLink(component, member, id));
            },
            original =>
            {
                var signature = PackageArchive.SignatureOf(original);
                if (signature == null)
                    return null;   // points outside the package; we never fetch those
                return context.AssetUrls.TryGetValue(signature, out var local) ? local : null;
            });
    }

    // Returns false when this slot and everything under it was stripped, so the caller can drop it.
    //
    // Async because a package is BIG: an avatar is 600+ slots and 700+ components, and building the lot
    // in one go froze the world for the better part of a minute. The build yields back to the update
    // loop every SlotsPerFrame slots, so the import takes several frames and the game stays responsive
    // instead of locking up. -xlinka
    // NOTE: the awaits below deliberately do NOT use ConfigureAwait(false). Everything in this build
    // path touches the datamodel and, through the hooks, the scene tree - and the platform only allows
    // that from the main thread. NextUpdate() resumes there correctly, but ConfigureAwait(false) on the
    // recursive calls handed the continuation straight back to the thread pool, so slot creation ended
    // up calling AddChild off-thread. Background work belongs in ConvertAssetsAsync, not here. -xlinka
    private const int SlotsPerFrame = 48;

    private static async Task<bool> BuildSlotAsync(DataTreeDictionary node, Slot slot, BuildContext context)
    {
        if (++context.SlotsThisFrame >= SlotsPerFrame)
        {
            context.SlotsThisFrame = 0;
            await WorldContext.NextUpdate();
            if (slot.IsDestroyed)
                return false;
        }

        // Refused BEFORE anything is built, so the whole subtree goes with it: their laser is a slot with
        // the component on it and its mesh, cursor and line slots underneath, and dropping only the
        // component would leave those hanging off the hand doing nothing.
        if (CarriesUserspaceConflict(node, context))
        {
            context.Result.SlotsConflicting++;
            return false;
        }

        // Same treatment for scaffolding a component generated for itself: refused before anything under
        // it is built, so the mesh and renderer inside go with it instead of surviving as an orphan.
        if (ReadValue(node, "Name") is string generatedName
            && PackageTypeMap.IsGeneratedOrphanSlot(StripMarkup(generatedName)))
        {
            context.Result.SlotsConflicting++;
            Logger.Log($"Package import: dropped generated slot '{StripMarkup(generatedName)}' and its subtree; "
                     + "the component that owns it is not ported");
            return false;
        }

        context.Result.SlotsCreated++;

        if ((node.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is string slotId && slotId.Length > 0)
            context.ById[slotId] = slot;

        if (ReadValue(node, "Name") is string rawName)
        {
            var name = StripMarkup(rawName);
            if (name.Length > 0)
                slot.SlotName.Value = name;
        }
        if (ReadValue(node, "Tag") is string tag && tag.Length > 0)
            slot.Tag.Value = tag;
        if (ReadValue(node, "Active") is bool active)
            slot.ActiveSelf.Value = active;

        ApplyTransform(node, slot);

        int attached = AttachComponents(node, slot, context, out int strippedHere);

        int survivingChildren = 0;
        if (node.TryGetNode("Children") is DataTreeList children)
        {
            foreach (var child in children.Children)
            {
                if (child is not DataTreeDictionary childNode)
                    continue;

                var childSlot = slot.AddSlot("Slot");
                if (await BuildSlotAsync(childNode, childSlot, context))
                    survivingChildren++;
                else if (!childSlot.IsDestroyed)
                    childSlot.Destroy();
            }
        }

        // Prune ONLY scaffolding that stripping emptied. A slot that never carried a component in the
        // first place is structure, not leftovers - and the clearest example is a skeleton: bones hold
        // nothing at all, so a rule that drops every empty slot deletes the fingertips, then their
        // parents, then the whole rig from the bottom up, and the avatar arrives with no skeleton to
        // deform. Empty-but-original slots are kept. -xlinka
        if (attached == 0 && survivingChildren == 0 && strippedHere > 0)
        {
            context.Result.SlotsPruned++;
            context.Result.SlotsCreated--;
            return false;
        }
        return true;
    }

    private static void ApplyTransform(DataTreeDictionary node, Slot slot)
    {
        if (ReadFloat3(node, "Position") is { } position)
            slot.LocalPosition.Value = position;
        if (ReadFloatQ(node, "Rotation") is { } rotation)
            slot.LocalRotation.Value = rotation;
        if (ReadFloat3(node, "Scale") is { } scale)
            slot.LocalScale.Value = scale;
    }

    // Links in the particle look chain. Only these - the map is a read-through, not a general cache.
    private static readonly HashSet<string> PassThroughTypes = new(StringComparer.Ordinal)
    {
        "ParticleStyle",
        "BillboardParticleRenderer",
        "MeshParticleRenderer",
    };

    private static bool CarriesUserspaceConflict(DataTreeDictionary node, BuildContext context)
    {
        if (node.TryGetNode("Components") is not DataTreeNode componentsNode)
            return false;

        var list = FindWorkerList(componentsNode);
        if (list == null)
            return false;

        foreach (var entry in list)
        {
            if (entry is not DataTreeDictionary worker)
                continue;
            if (worker.TryGetNode("Type") is not DataTreeValue typeValue)
                continue;

            int index = SafeInt(typeValue);
            if (index < 0 || index >= context.Types.Count)
                continue;

            if (PackageTypeMap.ConflictsWithUserspace(context.Types[index].SimpleName))
                return true;
        }
        return false;
    }

    private static int AttachComponents(DataTreeDictionary node, Slot slot, BuildContext context, out int stripped)
    {
        stripped = 0;
        if (node.TryGetNode("Components") is not DataTreeNode componentsNode)
            return 0;

        var list = FindWorkerList(componentsNode);
        if (list == null)
            return 0;

        int attached = 0;
        foreach (var entry in list)
        {
            if (entry is not DataTreeDictionary worker)
                continue;
            if (worker.TryGetNode("Type") is not DataTreeValue typeValue)
                continue;

            int index = SafeInt(typeValue);
            if (index < 0 || index >= context.Types.Count)
                continue;

            var resolution = context.Types[index];
            if (resolution.Fate == PackageTypeFate.Stripped)
            {
                context.Result.ComponentsStripped++;
                stripped++;
                continue;
            }

            var data = worker.TryGetNode("Data") as DataTreeDictionary;

            // Same read-through as the loose-asset path. The styles and renderers a particle system
            // points at live HERE, in the slot tree, so capturing them only over there recovered nothing.
            if (data != null && PassThroughTypes.Contains(resolution.SimpleName)
                && (data.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is string passId
                && passId.Length > 0)
            {
                context.PassThrough[passId] = data;
            }

            if (resolution.Target == null || data == null)
            {
                context.Result.ComponentsSkipped++;
                continue;
            }

            if (TryAttach(slot, resolution, data, context))
                attached++;
            else
                context.Result.ComponentsSkipped++;
        }
        return attached;
    }

    // Their component list is a sync list, so the actual entries sit under a nested member rather than
    // directly on the "Components" node. Find the first list of worker-shaped dictionaries.
    private static DataTreeList? FindWorkerList(DataTreeNode node)
    {
        switch (node)
        {
            case DataTreeList list:
                foreach (var child in list.Children)
                {
                    if (child is DataTreeDictionary d && d.ContainsKey("Type") && d.ContainsKey("Data"))
                        return list;
                }
                return null;

            case DataTreeDictionary dict:
                foreach (var child in dict.Children.Values)
                {
                    var found = FindWorkerList(child);
                    if (found != null)
                        return found;
                }
                return null;
        }
        return null;
    }

    // Only components we can genuinely carry across are attached. The set is deliberately narrow: these
    // are the ones that decide whether the object is VISIBLE and the right shape, which is what makes an
    // import worth having at all. Everything else is counted as skipped.
    private static bool TryAttach(Slot slot, PackageTypeMap.Resolution resolution,
        DataTreeDictionary data, BuildContext context)
    {
        var target = resolution.Target!;
        Component? component = null;

        try
        {
            // Anything we have a counterpart for is attached; the member mapper then carries across
            // whatever lines up by name. Narrowing this to a hand-picked list of types was the earlier
            // approach and it left most of a package on the floor for no good reason - if we have the
            // component and its members match, there is nothing to be gained by refusing to build it.
            component = slot.AttachComponent(target);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Package import: could not attach {resolution.SimpleName}: {ex.Message}");
            return false;
        }

        if (component == null)
            return false;

        // The id is a bare string on the component, not a wrapped member.
        if ((data.TryGetNode("ID") as DataTreeValue)?.Value?.ToString() is string id && id.Length > 0)
            context.ById[id] = component;

        if (component is Components.ParticleSystem particles)
            context.ParticleSystems.Add((particles, data));

        CopyMembers(component, data, context);
        context.Result.ComponentsAttached++;
        return true;
    }

    // ---- assets ----

    private static async Task<Dictionary<string, Uri>> ConvertAssetsAsync(
        PackageArchive archive, DataTreeDictionary graph, PackageImportResult result,
        Dictionary<string, Dictionary<string, float4x4>> bindPoses)
    {
        await WorldContext.ToBackground();

        var map = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var localDb = Engine.Current?.LocalDB;
        if (localDb == null)
            return map;

        // Only convert what the graph actually references. A package can carry assets nothing points at.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.EnumerateTree())
        {
            if (node is DataTreeValue { IsUrl: true } value)
            {
                var signature = PackageArchive.SignatureOf(value.ExtractUrl());
                if (signature != null && archive.HasAsset(signature))
                    wanted.Add(signature);
            }
        }

        foreach (var signature in wanted)
        {
            try
            {
                var bytes = archive.ReadAsset(signature);
                var kind = SniffKind(bytes);
                switch (kind)
                {
                    case ".lmesh":
                    {
                        // Re-encode into our own mesh format so it travels through the normal asset
                        // pipeline - cached, peer-transferred, decoded by the standard path.
                        var mesh = PackageMeshReader.Read(bytes);
                        var encoded = PhosMeshSerializer.Serialize(mesh);
                        var url = new Uri(await localDb.SaveAssetAsync(encoded, ".lmesh").ConfigureAwait(false));
                        map[signature] = url;

                        if (mesh.BoneCount > 0)
                        {
                            var binds = new Dictionary<string, float4x4>(StringComparer.Ordinal);
                            for (int i = 0; i < mesh.BoneCount; i++)
                                binds[mesh.GetBoneName(i)] = mesh.GetBoneBindPose(i);
                            bindPoses[url.OriginalString] = binds;
                        }
                        result.MeshesConverted++;
                        break;
                    }
                    case null:
                        break;
                    default:
                    {
                        // Textures and audio are the creator's original files and go in untouched.
                        map[signature] = new Uri(await localDb.SaveAssetAsync(bytes, kind).ConfigureAwait(false));
                        if (kind is ".png" or ".jpg" or ".webp" or ".gif" or ".bmp" or ".exr")
                            result.TexturesImported++;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                result.AssetsFailed++;
                Logger.Warn($"Package import: asset {signature[..System.Math.Min(12, signature.Length)]} failed: {ex.Message}");
            }
        }
        return map;
    }

    private static string? SniffKind(byte[] b)
    {
        if (b.Length >= 7 && b[0] == 5 && b[1] == 'M' && b[2] == 'e' && b[3] == 's' && b[4] == 'h' && b[5] == 'X')
            return ".lmesh";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            return ".png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return ".jpg";
        // RIFF is a CONTAINER, not a format: audio and WebP images both start with it, and the four
        // bytes at offset 8 say which. Naming a WebP ".wav" wrote perfectly good textures to disk under
        // an extension the image decoder cannot read, so it failed and retried on every one. -xlinka
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F')
            return b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? ".webp" : ".wav";
        if (b.Length >= 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S')
            return ".ogg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8')
            return ".gif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M')
            return ".bmp";
        if (b.Length >= 4 && b[0] == 0x76 && b[1] == 0x2F && b[2] == 0x31 && b[3] == 0x01)
            return ".exr";
        return null;
    }

    // ---- graph helpers ----

    private static List<PackageTypeMap.Resolution> ReadTypeTable(DataTreeDictionary graph)
    {
        var list = new List<PackageTypeMap.Resolution>();
        if (graph.TryGetNode("Types") is not DataTreeList types)
            return list;
        foreach (var node in types)
            list.Add(PackageTypeMap.Resolve((node as DataTreeValue)?.Value?.ToString() ?? string.Empty));
        return list;
    }

    // Every member is stored as { ID, Data } - the value lives under "Data", never at the top.
    private static object? ReadValue(DataTreeDictionary node, string member)
    {
        if (node.TryGetNode(member) is not DataTreeDictionary holder)
            return (node.TryGetNode(member) as DataTreeValue)?.Value;
        return (holder.TryGetNode("Data") as DataTreeValue)?.Value;
    }

    // Transforms are stored as ARRAYS of numbers, not as text. Parsing them as strings silently
    // yielded nothing for every slot, so nothing was ever positioned, rotated or scaled and the whole
    // object collapsed onto the origin at its raw model scale. -xlinka
    private static float3? ReadFloat3(DataTreeDictionary node, string member)
    {
        var parts = PackageMemberMapper.ReadNumberArray(node, member);
        return parts is { Length: >= 3 } ? new float3(parts[0], parts[1], parts[2]) : null;
    }

    private static floatQ? ReadFloatQ(DataTreeDictionary node, string member)
    {
        var parts = PackageMemberMapper.ReadNumberArray(node, member);
        return parts is { Length: >= 4 } ? new floatQ(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    private static int SafeInt(DataTreeValue value)
    {
        try { return Convert.ToInt32(value.Value); }
        catch { return -1; }
    }

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string CleanName(string? name)
    {
        var cleaned = StripMarkup(name);
        return cleaned.Length > 0 ? cleaned : "Imported Package";
    }

    // Names routinely carry rich-text colour tags. Left in, every slot in the hierarchy reads as markup.
    // Rich-text tags a name may be decorated with. Deliberately a CLOSED LIST.
    //
    // Angle brackets in a slot name are not automatically markup. Rig authors use them as plain naming
    // convention - "<NOIK> Hips" marks a bone the IK must leave alone - and stripping every bracketed run
    // renamed it to "Hips", which then collided with the real Hips: the skeleton refused the duplicate,
    // every bind bone that named the original failed to match and collapsed to bone 0, and the mesh
    // deformed into a knot. Strip what is genuinely formatting; leave everything else exactly as authored.
    // -xlinka
    private static readonly HashSet<string> RichTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "sub", "sup", "br", "nobr", "noparse",
        "color", "size", "alpha", "mark", "align", "font", "gradient", "style",
        "cspace", "indent", "line-height", "lowercase", "uppercase", "smallcaps",
        "margin", "mspace", "pos", "rotate", "space", "sprite", "voffset", "width", "link",
    };

    private static string StripMarkup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var sb = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != '<')
            {
                sb.Append(name[i]);
                continue;
            }

            int close = name.IndexOf('>', i + 1);
            if (close < 0)
            {
                // Unterminated: it is text, not a tag.
                sb.Append(name[i]);
                continue;
            }

            if (IsRichTextTag(name.AsSpan(i + 1, close - i - 1)))
            {
                i = close;      // drop the whole tag
                continue;
            }

            sb.Append(name[i]);
        }
        return sb.ToString().Trim();
    }

    // The body between the angle brackets, without them. A tag is "</name>", "<name>" or "<name=value>".
    private static bool IsRichTextTag(ReadOnlySpan<char> body)
    {
        if (body.IsEmpty)
            return false;
        if (body[0] == '/')
            body = body[1..];

        int stop = body.IndexOfAny('=', ' ');
        if (stop >= 0)
            body = body[..stop];

        return !body.IsEmpty && RichTextTags.Contains(body.ToString());
    }
}
