// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using GodotProbe = Godot.ReflectionProbe;
using LumoraProbe = Lumora.Core.Components.ReflectionProbe;

namespace Lumora.Godot.Hooks;

// Drives a Godot ReflectionProbe from the component's fields. No capture logic of its own - the
// renderer owns that entirely; this only says what and when.
//
// The rebake trick is the one thing worth explaining. A Once probe captures when it comes up and
// then holds that capture forever, and there is no "capture now" call to make. Flipping it to Always
// for a single frame and back is what makes it re-capture, which is why the component publishes a
// counter rather than firing an event: the hook compares the counter it last saw against the current
// one, so a rebake requested while the world was unfocused still happens when it comes back rather
// than being missed. -xlinka
[ImplementableHook(typeof(LumoraProbe))]
public sealed class ReflectionProbeHook : NodeBackedComponentHook<LumoraProbe, GodotProbe>
{
    private int _lastBakeGeneration;
    private bool _rebaking;
    private bool _frameHooked;

    public static IHook<LumoraProbe> Constructor() => new ReflectionProbeHook();

    public GodotProbe GodotReflectionProbe => PlatformNode;

    protected override GodotProbe CreatePlatformNode() => new GodotProbe { Name = "ReflectionProbe" };

    protected override void OnAfterAttach()
    {
        _lastBakeGeneration = Owner.BakeGeneration.Value;
        EngineSettings.Changed += OnSettingsChanged;
    }

    protected override void SyncProperties()
    {
        var probe = PlatformNode;
        if (probe == null)
            return;

        // The quality toggle is a hard off: an invisible probe is not captured and contributes
        // nothing, so turning reflections off actually removes the cost rather than dimming it.
        bool enabled = Owner.Enabled && EngineSettings.ReflectionsEnabled;
        probe.Visible = enabled;

        var size = Owner.Size.Value;
        probe.Size = new Vector3(size.x, size.y, size.z);

        var offset = Owner.OriginOffset.Value;
        probe.OriginOffset = new Vector3(offset.x, offset.y, offset.z);

        probe.Intensity = Owner.Intensity.Value;
        probe.BoxProjection = Owner.BoxProjection.Value;
        probe.Interior = Owner.Interior.Value;
        probe.BlendDistance = Owner.BlendDistance.Value;
        probe.MaxDistance = Owner.MaxDistance.Value;
        probe.EnableShadows = Owner.CaptureShadows.Value;

        // 0 means "whatever the renderer defaults to", not "capture nothing", so a zero never reaches
        // the node. But the renderer default is ALL twenty layers, and the eye cameras deliberately
        // strip the editor chrome layers - so a default probe was baking gizmos, overlay geometry, the
        // free-cam marker and the dashboard's offscreen render into every surface it lit. Fall back to
        // the same mask the eye uses instead. PRIVATE_LAYER is deliberately kept: an unfocused or
        // private world renders on it, and masking it out would stop probes seeing their own world.
        int cullMask = Owner.CullMask.Value;
        probe.CullMask = (uint)(cullMask != 0 ? cullMask : Helpers.RenderHelper.PRIVATE_RENDER_MASK);

        probe.AmbientMode = Owner.AmbientMode.Value switch
        {
            ProbeAmbientMode.Disabled => GodotProbe.AmbientModeEnum.Disabled,
            ProbeAmbientMode.Color => GodotProbe.AmbientModeEnum.Color,
            _ => GodotProbe.AmbientModeEnum.Environment,
        };
        var ambient = Owner.AmbientColor.Value;
        probe.AmbientColor = new Color(ambient.r, ambient.g, ambient.b, ambient.a);
        probe.AmbientColorEnergy = Owner.AmbientEnergy.Value;

        // The generation is consumed only where the rebake is ACTUALLY armed.
        //
        // It used to be taken unconditionally, one line above a gate that drops the request when the
        // component is disabled or reflections are off in settings. A rebake pressed in either state
        // was eaten and never replayed, which is the opposite of what this hook's own header promises.
        // The settings-changed path re-marks the component dirty, so a request parked here replays on
        // its own once the gate opens. -xlinka
        int generation = Owner.BakeGeneration.Value;
        bool rebakeRequested = generation != _lastBakeGeneration;

        if (rebakeRequested && enabled && Owner.UpdateMode.Value == ProbeUpdateMode.Once)
        {
            _lastBakeGeneration = generation;
            BeginRebake(probe);
        }
        else if (!_rebaking)
            probe.UpdateMode = Owner.UpdateMode.Value == ProbeUpdateMode.Always
                ? GodotProbe.UpdateModeEnum.Always
                : GodotProbe.UpdateModeEnum.Once;
    }

    private void BeginRebake(GodotProbe probe)
    {
        probe.UpdateMode = GodotProbe.UpdateModeEnum.Always;
        if (_rebaking)
            return;

        _rebaking = true;
        var tree = probe.GetTree();
        if (tree == null)
        {
            // No tree to wait a frame on. Leaving it in Always would quietly turn a Once probe into
            // the expensive kind, so give the capture back immediately rather than risk that.
            _rebaking = false;
            probe.UpdateMode = GodotProbe.UpdateModeEnum.Once;
            return;
        }

        _frameHooked = true;
        tree.ProcessFrame += EndRebake;
    }

    private void EndRebake()
    {
        var probe = PlatformNode;
        UnhookFrame(probe);
        _rebaking = false;

        if (probe != null && GodotObject.IsInstanceValid(probe) && Owner is { IsDestroyed: false }
            && Owner.UpdateMode.Value == ProbeUpdateMode.Once)
        {
            probe.UpdateMode = GodotProbe.UpdateModeEnum.Once;
        }
    }

    private void UnhookFrame(GodotProbe? probe)
    {
        if (!_frameHooked)
            return;
        _frameHooked = false;
        var tree = probe != null && GodotObject.IsInstanceValid(probe) ? probe.GetTree() : null;
        if (tree != null)
            tree.ProcessFrame -= EndRebake;
    }

    // The settings screen writes from its own world's thread, so bounce the re-apply through ours.
    private void OnSettingsChanged()
    {
        var owner = Owner;
        var world = owner?.World;
        if (owner == null || world == null || owner.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!owner.IsDestroyed)
                owner.MarkChangeDirty();
        });
    }

    public override void Destroy(bool destroyingWorld)
    {
        EngineSettings.Changed -= OnSettingsChanged;
        UnhookFrame(PlatformNode);
        base.Destroy(destroyingWorld);
    }
}
