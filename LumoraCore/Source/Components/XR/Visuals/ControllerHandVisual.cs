// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// attach to the same slot as a TrackedDevicePositioner; set HandSide before the component reaches OnStart
[ComponentCategory("Users")]
public sealed class ControllerHandVisual : Component
{
    // SYNC FIELDS

    public readonly Sync<Chirality> HandSide = null!;

    // metres
    public readonly Sync<float> JointRadius = null!;

    // metres
    public readonly Sync<float> BoneRadius = null!;

    // scales both the controller-relative rest pose and the mesh thickness
    public readonly Sync<float> HandScale = null!;

    // INNER TYPES

    private readonly struct JointEntry
    {
        public readonly BodyNode Node;
        public readonly Slot     VisualSlot;
        public JointEntry(BodyNode node, Slot slot) { Node = node; VisualSlot = slot; }
    }

    private readonly struct BoneEntry
    {
        public readonly BodyNode NodeA;
        public readonly BodyNode NodeB;
        public BoneEntry(BodyNode a, BodyNode b) { NodeA = a; NodeB = b; }
    }

    // PRIVATE STATE

    private Slot         _handSkeletonRoot = null!;
    private PBS_Metallic _handMaterial = null!;
    private HandSkeletonMesh _skeletonMesh = null!;
    private List<JointEntry> _joints = null!;
    private List<BoneEntry>  _bones = null!;
    private Dictionary<BodyNode, float3> _jointWorldPositions = null!;
    private Dictionary<BodyNode, float3> _restPoseLocalPositions = null!;

    // Scratch pose handed to the mesh each frame, in the skeleton root's local space. Sized once.
    private float3[] _poseJoints = Array.Empty<float3>();
    private float[] _poseRadii = Array.Empty<float>();
    private (int a, int b)[] _poseBones = Array.Empty<(int, int)>();
    private Dictionary<BodyNode, int> _jointIndex = null!;

    // FINGER TOPOLOGY

    // Thumb has no Intermediate segment; all other fingers have five segments.
    private static readonly FingerType[] AllFingers = new[]
    {
        FingerType.Thumb,
        FingerType.Index,
        FingerType.Middle,
        FingerType.Ring,
        FingerType.Pinky,
    };

    private static readonly FingerSegmentType[] ThumbSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip,
    };

    private static readonly FingerSegmentType[] FingerSegments = new[]
    {
        FingerSegmentType.Metacarpal,
        FingerSegmentType.Proximal,
        FingerSegmentType.Intermediate,
        FingerSegmentType.Distal,
        FingerSegmentType.Tip,
    };

    // LIFECYCLE

    public override void OnInit()
    {
        base.OnInit();
        HandSide.Value    = Chirality.Right;
        JointRadius.Value = 0.0105f;
        BoneRadius.Value  = 0.0065f;
        HandScale.Value   = 1.12f;
    }

    public override void OnStart()
    {
        base.OnStart();
        _joints = new List<JointEntry>();
        _bones  = new List<BoneEntry>();
        _jointIndex = new Dictionary<BodyNode, int>(32);
        _jointWorldPositions   = new Dictionary<BodyNode, float3>(32);
        _restPoseLocalPositions = BuildRestPoseLocalPositions(HandSide.Value, HandScale.Value);
        // The skeleton is a per-viewer visual: RefreshVisuals drives every joint/bone each frame from THIS
        // machine's InputInterface (tracked node poses) or a controller-relative rest pose, so the geometry
        // can't be coherently networked anyway - every peer would just overwrite it from its own local input.
        // Building it under a local root (see BuildHandSkeleton) means each viewer mints its own copy hanging
        // off the replicated controller slot, so remote users still see your hands (the controller slot is the
        // networked, avatar-posed part). Local slots are never permission-gated, so no bypass is needed and a
        // non-owner peer never tries to write under a slot it doesn't own. -xlinka
        BuildHandSkeleton();
        RefreshVisuals();
        LumoraLogger.Log($"ControllerHandVisual: Initialized for {HandSide.Value} hand on '{Slot.SlotName.Value}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        RefreshVisuals();
    }

    // These slots are already positioned in world space from whatever the best available source is
    // that frame - tracked hand skeleton, or the rest pose off the controller. Anything that needs to
    // sit ON a finger (a touch probe, say) rides this rather than re-deriving an offset off the
    // controller and drifting away from the hand the user can actually see. -xlinka
    public Slot? TryGetJointSlot(BodyNode node)
    {
        var joints = _joints;
        if (joints == null)
            return null;

        for (int i = 0; i < joints.Count; i++)
        {
            if (joints[i].Node != node)
                continue;
            var slot = joints[i].VisualSlot;
            return slot != null && !slot.IsDestroyed ? slot : null;
        }
        return null;
    }

    public override void OnDestroy()
    {
        // Child slots are destroyed with the parent slot; just release list references.
        _joints = null!;
        _bones  = null!;
        _jointIndex = null!;
        _skeletonMesh = null!;
        _jointWorldPositions = null!;
        _restPoseLocalPositions = null!;
        base.OnDestroy();
    }

    // VISUAL CONSTRUCTION

    private void BuildHandSkeleton()
    {
        // LOCAL root: mints a LOCAL_BYTE RefID the world never replicates. Every child AddSlot/AttachComponent
        // below inherits local placement (a child of a local element is local), so the whole skeleton subtree
        // stays on this machine - no permission gate, no duplicate networked slots when each peer builds its own.
        // RefreshVisuals positions it from the replicated controller slot, so remote hands still render. -xlinka
        _handSkeletonRoot = Slot.AddLocalSlot("HandSkeleton");

        // One shared material for all joint spheres and bone cylinders.
        var matSlot = _handSkeletonRoot.AddSlot("HandSkeletonMaterial");
        _handMaterial = matSlot.AttachComponent<PBS_Metallic>();
        _handMaterial.AlbedoColor.Value   = new colorHDR(0.82f, 0.82f, 0.86f, 1f);
        _handMaterial.EmissiveColor.Value = new colorHDR(0.06f, 0.06f, 0.10f, 1f);
        _handMaterial.Metallic.Value      = 0.25f;
        _handMaterial.Smoothness.Value    = 0.55f;
        _handMaterial.RenderQueue.Value   = 60;

        // ONE mesh for the whole hand. Joints keep a slot each (a touch probe sits on a fingertip
        // slot), but nothing renders per joint or per bone any more: the mesh below is rewritten in
        // place from the pose every frame and drawn once. -xlinka
        _skeletonMesh = _handSkeletonRoot.AttachComponent<HandSkeletonMesh>();
        var renderer = _handSkeletonRoot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target     = _skeletonMesh;
        renderer.Material.Target = _handMaterial;
        renderer.SortingOrder.Value = 60;

        Chirality side    = HandSide.Value;
        float scale = MathF.Max(HandScale.Value, 0.2f);
        float     jRadius = JointRadius.Value * scale;
        float     bRadius = BoneRadius.Value * scale;

        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;

        // Palm joint sphere.
        CreateJointVisual(palmNode, jRadius);

        foreach (var finger in AllFingers)
        {
            BodyNode[] nodes = GetFingerNodes(finger, side);

            // Joint sphere at every segment node.
            foreach (var node in nodes)
                CreateJointVisual(node, jRadius);

            // Bone connecting palm to this finger's metacarpal.
            CreateBoneVisual(palmNode, nodes[0], bRadius);

            // Bones connecting each adjacent pair of segments within the finger.
            for (int i = 0; i < nodes.Length - 1; i++)
                CreateBoneVisual(nodes[i], nodes[i + 1], bRadius);
        }

        _poseJoints = new float3[_joints.Count];
        _poseRadii = new float[_joints.Count];
        for (int i = 0; i < _joints.Count; i++)
            _poseRadii[i] = jRadius;
        _poseBones = new (int, int)[_bones.Count];

        _handSkeletonRoot.ActiveSelf.Value = true;
    }

    private void CreateJointVisual(BodyNode node, float radius)
    {
        var slot = _handSkeletonRoot.AddSlot($"Joint_{node}");
        _jointIndex[node] = _joints.Count;
        _joints.Add(new JointEntry(node, slot));
    }

    private void CreateBoneVisual(BodyNode nodeA, BodyNode nodeB, float radius)
    {
        _bones.Add(new BoneEntry(nodeA, nodeB));
    }

    // PER-FRAME UPDATE

    private void RefreshVisuals()
    {
        var input = Engine.Current?.InputInterface;
        _jointWorldPositions.Clear();

        // Update joint sphere world positions. Prefer tracked node poses, but fall back
        // to a controller-relative hand rest pose when tracking is unavailable.
        foreach (var joint in _joints)
        {
            if (joint.VisualSlot == null || joint.VisualSlot.IsDestroyed)
                continue;

            float3 jointWorldPos;

            if (input != null)
            {
                var device = input.GetBodyNode(joint.Node);
                if (device != null && device.IsTracking)
                {
                    jointWorldPos = device.Position;
                }
                else
                {
                    jointWorldPos = GetFallbackJointWorldPosition(joint.Node);
                }
            }
            else
            {
                jointWorldPos = GetFallbackJointWorldPosition(joint.Node);
            }

            joint.VisualSlot.GlobalPosition = jointWorldPos;
            joint.VisualSlot.ActiveSelf.Value = true;
            _jointWorldPositions[joint.Node] = jointWorldPos;
        }

        // Hand the whole pose to the one mesh. The default hand gets disposed when a real avatar is
        // equipped over the hands while this refresh can still run for a frame; a disposed mesh is
        // skipped rather than written. -xlinka
        var mesh = _skeletonMesh;
        var root = _handSkeletonRoot;
        if (mesh == null || mesh.IsDestroyed || root == null || root.IsDestroyed || _poseJoints.Length != _joints.Count)
            return;

        for (int i = 0; i < _joints.Count; i++)
        {
            _poseJoints[i] = _jointWorldPositions.TryGetValue(_joints[i].Node, out var world)
                ? root.GlobalPointToLocal(world)
                : float3.Zero;
        }

        int written = 0;
        foreach (var bone in _bones)
        {
            // A bone without both ends collapses to a point inside the mesh instead of vanishing from
            // the index buffer, so the topology never has to be rebuilt for a lost joint.
            int a = -1, b = -1;
            bool ok = _jointIndex.TryGetValue(bone.NodeA, out a) && _jointIndex.TryGetValue(bone.NodeB, out b)
                      && _jointWorldPositions.ContainsKey(bone.NodeA) && _jointWorldPositions.ContainsKey(bone.NodeB);
            _poseBones[written++] = ok ? (a, b) : (-1, -1);
        }

        float bRadius = BoneRadius.Value * MathF.Max(HandScale.Value, 0.2f);
        mesh.SetPose(_poseJoints, _poseRadii, _poseBones, bRadius);
    }

    private float3 GetFallbackJointWorldPosition(BodyNode node)
    {
        if (_restPoseLocalPositions != null && _restPoseLocalPositions.TryGetValue(node, out var local))
        {
            return Slot.GlobalPosition + (Slot.GlobalRotation * local);
        }

        return Slot.GlobalPosition;
    }

    private static Dictionary<BodyNode, float3> BuildRestPoseLocalPositions(Chirality side, float scale)
    {
        static float3 MirrorForSide(float3 rightHandPos, Chirality chirality)
        {
            return chirality == Chirality.Left
                ? new float3(-rightHandPos.x, rightHandPos.y, rightHandPos.z)
                : rightHandPos;
        }

        static void AddChain(
            Dictionary<BodyNode, float3> map,
            Chirality chirality,
            FingerType finger,
            IReadOnlyList<float3> rightHandPoints)
        {
            FingerSegmentType[] segments = finger == FingerType.Thumb
                ? ThumbSegments
                : FingerSegments;

            for (int i = 0; i < segments.Length; i++)
            {
                BodyNode node = finger.ComposeFinger(segments[i], chirality);
                map[node] = MirrorForSide(rightHandPoints[i], chirality);
            }
        }

        var map = new Dictionary<BodyNode, float3>(32);
        float poseScale = MathF.Max(scale, 0.2f);
        BodyNode palmNode = side == Chirality.Left ? BodyNode.LeftPalm : BodyNode.RightPalm;
        map[palmNode] = MirrorForSide(new float3(0.0000f, -0.0150f, -0.0350f) * poseScale, side);

        AddChain(map, side, FingerType.Thumb, new[]
        {
            new float3(0.0240f, -0.0100f, -0.0320f) * poseScale,
            new float3(0.0360f, -0.0040f, -0.0430f) * poseScale,
            new float3(0.0440f,  0.0000f, -0.0560f) * poseScale,
            new float3(0.0500f,  0.0040f, -0.0680f) * poseScale,
        });

        AddChain(map, side, FingerType.Index, new[]
        {
            new float3(0.0160f, -0.0060f, -0.0480f) * poseScale,
            new float3(0.0170f, -0.0030f, -0.0670f) * poseScale,
            new float3(0.0180f,  0.0010f, -0.0850f) * poseScale,
            new float3(0.0190f,  0.0040f, -0.1030f) * poseScale,
            new float3(0.0200f,  0.0070f, -0.1190f) * poseScale,
        });

        AddChain(map, side, FingerType.Middle, new[]
        {
            new float3(0.0060f, -0.0060f, -0.0470f) * poseScale,
            new float3(0.0060f, -0.0020f, -0.0680f) * poseScale,
            new float3(0.0060f,  0.0020f, -0.0890f) * poseScale,
            new float3(0.0060f,  0.0060f, -0.1090f) * poseScale,
            new float3(0.0060f,  0.0100f, -0.1260f) * poseScale,
        });

        AddChain(map, side, FingerType.Ring, new[]
        {
            new float3(-0.0040f, -0.0070f, -0.0450f) * poseScale,
            new float3(-0.0050f, -0.0030f, -0.0640f) * poseScale,
            new float3(-0.0060f,  0.0000f, -0.0820f) * poseScale,
            new float3(-0.0070f,  0.0030f, -0.0990f) * poseScale,
            new float3(-0.0080f,  0.0060f, -0.1130f) * poseScale,
        });

        AddChain(map, side, FingerType.Pinky, new[]
        {
            new float3(-0.0140f, -0.0090f, -0.0410f) * poseScale,
            new float3(-0.0160f, -0.0060f, -0.0570f) * poseScale,
            new float3(-0.0180f, -0.0030f, -0.0720f) * poseScale,
            new float3(-0.0200f,  0.0000f, -0.0860f) * poseScale,
            new float3(-0.0220f,  0.0030f, -0.0980f) * poseScale,
        });

        return map;
    }

    // STATIC HELPERS

    // thumb uses a four-node sequence (no Intermediate); other fingers use five
    private static BodyNode[] GetFingerNodes(FingerType finger, Chirality chirality)
    {
        FingerSegmentType[] segments = finger == FingerType.Thumb ? ThumbSegments : FingerSegments;
        var nodes = new BodyNode[segments.Length];
        for (int i = 0; i < segments.Length; i++)
            nodes[i] = finger.ComposeFinger(segments[i], chirality);
        return nodes;
    }
}
