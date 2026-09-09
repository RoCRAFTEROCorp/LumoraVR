// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;

namespace Lumora.Godot.Helpers;

public static class RenderHelper
{
    public static Action<Camera3D> RegisterCamera = null!;

    // Render layer bit assignments. Bit 0 (layer 1) is the default "public" layer.
    // Cameras can mask layers via VisualInstance3D.Layers / Camera3D.CullMask.
    public const int PUBLIC_LAYER = 1 << 0;
    public const int PRIVATE_LAYER = 1 << 1;
    public const int TEMP_LAYER = 1 << 2;
    public const int HIDDEN_LAYER = 1 << 3;
    public const int OVERLAY_LAYER = 1 << 4;
    // Free-cam position marker (sphere + label). Rendered for spectators/others but culled from the
    // local view so you don't see your own marker sitting on the camera. - xlinka
    public const int FREECAM_INDICATOR_LAYER = 1 << 19;

    public const int PUBLIC_RENDER_MASK = ~(PRIVATE_LAYER | TEMP_LAYER | HIDDEN_LAYER | OVERLAY_LAYER);
    public const int PRIVATE_RENDER_MASK = ~(TEMP_LAYER | HIDDEN_LAYER | OVERLAY_LAYER);

    // Bits 5..18 are unclaimed. 0-4 are the fixed bands above and 19 is the free-cam indicator, so a
    // camera doing selective or excluded rendering borrows one of the fourteen in between and gives it
    // back when it stops. Godot only has twenty visual layers, so this is a hard ceiling on how many
    // cameras can be doing it at once - past that the camera renders normally rather than wrongly.
    // -xlinka
    private const int SELECTIVE_FIRST_BIT = 5;
    private const int SELECTIVE_LAST_BIT = 18;

    private static readonly object _selectiveLock = new();
    private static int _selectiveBitsInUse;

    // Returns a single-bit mask, or 0 when every bit is taken.
    public static int AllocateSelectiveLayer()
    {
        lock (_selectiveLock)
        {
            for (int bit = SELECTIVE_FIRST_BIT; bit <= SELECTIVE_LAST_BIT; bit++)
            {
                int mask = 1 << bit;
                if ((_selectiveBitsInUse & mask) != 0)
                    continue;
                _selectiveBitsInUse |= mask;
                return mask;
            }
        }
        return 0;
    }

    public static void ReleaseSelectiveLayer(int mask)
    {
        if (mask == 0)
            return;
        lock (_selectiveLock)
        {
            _selectiveBitsInUse &= ~mask;
        }
    }

    // Additive: the subtree keeps every layer it already had and gains one more, so adding a camera's
    // selective layer never changes what anything else sees.
    public static void AddHierarchyLayerBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        if (root is VisualInstance3D visual)
            visual.Layers |= (uint)bit;

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                AddHierarchyLayerBit(child3D, bit);
        }
    }

    public static void RemoveHierarchyLayerBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        if (root is VisualInstance3D visual)
            visual.Layers &= ~(uint)bit;

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                RemoveHierarchyLayerBit(child3D, bit);
        }
    }

    // Move a subtree off the public band and onto a camera's own bit, so that camera can mask it out.
    // The private/temp/hidden/overlay bits are left alone - they carry meaning nothing here is entitled
    // to throw away - and every other camera's mask already includes bits 5..18, so the subtree stays
    // visible everywhere else.
    public static void SwapPublicLayerForBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        // Only nodes that were ACTUALLY on the public layer get swapped. Without this guard a node on
        // the private band alone would come back from RestorePublicLayerFromBit wearing a public bit it
        // never had, which is a visibility leak dressed up as a restore.
        if (root is VisualInstance3D visual && (visual.Layers & (uint)PUBLIC_LAYER) != 0)
            visual.Layers = (visual.Layers & ~(uint)PUBLIC_LAYER) | (uint)bit;

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                SwapPublicLayerForBit(child3D, bit);
        }
    }

    public static void RestorePublicLayerFromBit(Node3D root, int bit)
    {
        if (root == null || !GodotObject.IsInstanceValid(root) || bit == 0)
            return;

        if (root is VisualInstance3D visual && (visual.Layers & (uint)bit) != 0)
            visual.Layers = (visual.Layers & ~(uint)bit) | (uint)PUBLIC_LAYER;

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
                RestorePublicLayerFromBit(child3D, bit);
        }
    }

    public static void SetHierarchyLayer(List<Slot> slots, int layer, Dictionary<Node3D, int> previous)
    {
        var nodes = new List<Node3D>();
        GodotHelper.ConvertSlots(slots, nodes);
        SetHierarchyLayer(nodes, layer, previous);
    }

    public static void SetHierarchyLayer(List<Node3D> nodes, int layer, Dictionary<Node3D, int> previous)
    {
        if (nodes == null)
            return;

        foreach (var node in nodes)
        {
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                SetHierarchyLayer(node, layer, previous);
            }
        }
    }

    public static void RestoreHierarchyLayer(List<Node3D> nodes, Dictionary<Node3D, int> previous)
    {
        if (nodes == null)
            return;

        foreach (var node in nodes)
        {
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                RestoreHierarchyLayer(node, previous);
            }
        }
    }

    public static void SetHierarchyLayer(Node3D root, int layer, Dictionary<Node3D, int> previous)
    {
        if (root is VisualInstance3D visual && visual.Layers != (uint)layer)
        {
            if (!previous.ContainsKey(root))
            {
                previous.Add(root, (int)visual.Layers);
            }
            visual.Layers = (uint)layer;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
            {
                SetHierarchyLayer(child3D, layer, previous);
            }
        }
    }

    public static void RestoreHierarchyLayer(Node3D root, Dictionary<Node3D, int> previous)
    {
        if (root is VisualInstance3D visual && previous.TryGetValue(root, out int previousLayer))
        {
            visual.Layers = (uint)previousLayer;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
            {
                RestoreHierarchyLayer(child3D, previous);
            }
        }
    }

    public static void RestoreLayers(Dictionary<Node3D, int> previous)
    {
        foreach (var pair in previous)
        {
            if (pair.Key != null && GodotObject.IsInstanceValid(pair.Key) && pair.Key is VisualInstance3D visual)
            {
                visual.Layers = (uint)pair.Value;
            }
        }
    }
}

