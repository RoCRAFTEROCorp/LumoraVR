// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

public class RenderTexture : TextureAsset
{
    public bool RenderEnabled { get; private set; } = true;
    public int RenderWidth { get; private set; } = 1024;
    public int RenderHeight { get; private set; } = 1024;
    public int CullMask { get; private set; }
    public color ClearColor { get; private set; } = new color(0f, 0f, 0f, 0f);
    public float3 CameraPosition { get; private set; }
    public floatQ CameraRotation { get; private set; } = floatQ.Identity;
    public float OrthographicSize { get; private set; } = 1f;

    public void Configure(
        int width,
        int height,
        int cullMask,
        color clearColor,
        float3 cameraPosition,
        floatQ cameraRotation,
        float orthographicSize)
    {
        RenderWidth = width < 1 ? 1 : width;
        RenderHeight = height < 1 ? 1 : height;
        CullMask = cullMask;
        ClearColor = clearColor;
        CameraPosition = cameraPosition;
        CameraRotation = cameraRotation;
        OrthographicSize = orthographicSize <= 0f ? 1f : orthographicSize;
        Version++;

        var hook = Hook as IRenderTextureAssetHook;
        hook?.Configure(
            RenderWidth, RenderHeight, CullMask, ClearColor,
            CameraPosition, CameraRotation, OrthographicSize);
        if (CameraOverride.HasValue)
            hook?.SetCameraOverride(CameraOverride);
    }

    // Set by a Camera component that is filming into this texture, cleared when it stops. Kept on the
    // asset rather than only on the hook so a camera that resolves before the render hook exists is not
    // silently dropped - Configure re-pushes it. -xlinka
    public RenderCameraParameters? CameraOverride { get; private set; }

    public void SetCameraOverride(RenderCameraParameters? parameters)
    {
        CameraOverride = parameters;
        (Hook as IRenderTextureAssetHook)?.SetCameraOverride(parameters);
    }

    /// <summary>
    /// Pause or resume the offscreen render. The viewport and its texture stay
    /// alive so consumers keep a valid (stale) image while paused.
    /// </summary>
    public void SetRenderEnabled(bool enabled)
    {
        if (RenderEnabled == enabled) return;
        RenderEnabled = enabled;
        (Hook as IRenderTextureAssetHook)?.SetRenderEnabled(enabled);
    }

    /// <summary>
    /// Ask the offscreen viewport to render a single frame now (used when the captured UI actually changed).
    /// No-op while paused. This is what lets the UI render only on change instead of every frame. -xlinka
    /// </summary>
    public void RequestRender()
    {
        if (!RenderEnabled) return;
        (Hook as IRenderTextureAssetHook)?.RequestRender();
    }
}
