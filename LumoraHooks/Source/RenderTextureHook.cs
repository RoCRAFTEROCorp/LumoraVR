// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(RenderTexture))]
public class RenderTextureHook : AssetHook, IRenderTextureAssetHook, IGodotTexture
{
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;

    private bool _renderEnabled = true;
    private int _width = 1024;
    private int _height = 1024;
    private uint _cullMask;
    private Color _clearColor;
    private Vector3 _cameraPosition;
    private Quaternion _cameraRotation = Quaternion.Identity;
    private float _orthoSize = 1f;

    // Set while a Camera component is filming into this texture. It replaces the provider's fixed
    // orthographic framing wholesale and flips the viewport from render-on-change to continuous, because
    // a camera on a slot in a live world has no way to tell us when its picture changed. -xlinka
    private Lumora.Core.Assets.RenderCameraParameters? _cameraOverride;
    private global::Godot.Environment? _cameraEnvironment;

    // Every live offscreen viewport, so the profiler can cost them individually instead of lumping them
    // into one global draw-call number. The dash, every mirror and every camera each own one, and "the
    // frame is expensive" is a useless answer when four viewports are rendering the same world.
    //
    // A registry rather than a scene-tree walk: the tree under the world root runs to tens of thousands
    // of nodes and FindChildren over it four times a second would cost more than the thing it measures.
    // -xlinka
    private static readonly List<SubViewport> _liveViewports = new();
    private static readonly object _liveViewportLock = new();

    public static void CollectLiveViewports(List<SubViewport> into)
    {
        lock (_liveViewportLock)
        {
            for (int i = _liveViewports.Count - 1; i >= 0; i--)
            {
                var viewport = _liveViewports[i];
                if (viewport == null || !GodotObject.IsInstanceValid(viewport))
                {
                    _liveViewports.RemoveAt(i);
                    continue;
                }
                into.Add(viewport);
            }
        }
    }

    public Texture2D? GodotTexture2D => _viewport != null && GodotObject.IsInstanceValid(_viewport)
        ? _viewport.GetTexture()
        : null;

    public bool IsValid => _viewport != null && GodotObject.IsInstanceValid(_viewport) && _viewport.IsInsideTree();

    public void Configure(
        int width,
        int height,
        int cullMask,
        Lumora.Core.Math.color clearColor,
        Lumora.Core.Math.float3 cameraPosition,
        Lumora.Core.Math.floatQ cameraRotation,
        float orthographicSize)
    {
        _width = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);
        if (cullMask != 0)
            _cullMask = (uint)cullMask;
        _clearColor = new Color(clearColor.r, clearColor.g, clearColor.b, clearColor.a);
        _cameraPosition = new Vector3(cameraPosition.x, cameraPosition.y, cameraPosition.z);
        _cameraRotation = new Quaternion(cameraRotation.x, cameraRotation.y, cameraRotation.z, cameraRotation.w);
        _orthoSize = orthographicSize <= 0f ? 1f : orthographicSize;

        Callable.From(ApplyOnMainThread).CallDeferred();
    }

    private void ApplyOnMainThread()
    {
        var host = WorldManagerHook.Instance?.Root;
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            LumoraLogger.Warn("RenderTextureHook: no WorldManager root to host the viewport yet");
            return;
        }

        if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
        {
            _viewport = new SubViewport
            {
                Name = "RenderTexture",
                // Render ON DEMAND (one frame per change via RequestRender) instead of every frame. The
                // captured scene is just the UI canvas, which only changes when its mesh is rebuilt, so
                // re-rendering this full-res supersampled viewport every frame was pure waste. Disabled keeps
                // the last rendered frame on the texture. -xlinka
                RenderTargetUpdateMode = _renderEnabled ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                OwnWorld3D = false,
                HandleInputLocally = false,
                Disable3D = false,
            };

            _camera = new Camera3D
            {
                Name = "CaptureCamera",
                Projection = Camera3D.ProjectionType.Orthogonal,
                Near = 0.01f,
                Far = 50f,
                PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
            };
            _viewport.AddChild(_camera);
            host.AddChild(_viewport);

            var world = host.GetWorld3D();
            if (world != null)
                _viewport.World3D = world;

            lock (_liveViewportLock)
                _liveViewports.Add(_viewport);
        }

        _viewport.Size = new Vector2I(_width, _height);

        if (_cameraOverride.HasValue)
            ApplyCameraOverride(_cameraOverride.Value);
        else
            ApplyProviderFraming();

        _camera.Current = true;
    }

    private void ApplyProviderFraming()
    {
        _viewport.TransparentBg = _clearColor.A < 1f;
        _viewport.Msaa3D = Viewport.Msaa.Disabled;
        _viewport.UseOcclusionCulling = false;
        _viewport.PositionalShadowAtlasSize = 2048;

        if (_cullMask != 0)
            _camera.CullMask = _cullMask;
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        _camera.Size = _orthoSize;
        _camera.Near = 0.01f;
        _camera.Far = 50f;
        _camera.Position = _cameraPosition;
        _camera.Quaternion = _cameraRotation;
        _camera.Environment = null;
    }

    private void ApplyCameraOverride(Lumora.Core.Assets.RenderCameraParameters p)
    {
        _camera.Projection = p.Perspective ? Camera3D.ProjectionType.Perspective : Camera3D.ProjectionType.Orthogonal;
        _camera.Fov = p.FieldOfView;
        _camera.Size = p.OrthographicSize <= 0f ? 1f : p.OrthographicSize;
        _camera.Near = System.Math.Max(0.001f, p.NearClip);
        _camera.Far = System.Math.Max(_camera.Near + 0.001f, p.FarClip);
        _camera.Position = new Vector3(p.Position.x, p.Position.y, p.Position.z);
        _camera.Quaternion = new Quaternion(p.Rotation.x, p.Rotation.y, p.Rotation.z, p.Rotation.w);
        _camera.CullMask = unchecked((uint)p.CullMask);

        _viewport.Msaa3D = p.Msaa ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Disabled;
        _viewport.UseOcclusionCulling = p.OcclusionCulling;
        // The only per-viewport shadow switch Godot has. It covers point and spot lights; the sun's
        // shadow belongs to the light, so a scene lit by a directional still casts one.
        _viewport.PositionalShadowAtlasSize = p.RenderShadows ? 2048 : 0;

        bool wantsSky = p.Clear == Lumora.Core.Components.ClearMode.Skybox;
        _viewport.TransparentBg = !wantsSky && p.BackgroundColor.a < 1f;
        _camera.Environment = BuildCameraEnvironment(p);
    }

    // Null means "inherit the world's environment", which is what Clear = Skybox with post-processing on
    // actually wants - the sky and the tonemapper both live there. Everything else needs an environment
    // of our own, and a sky-clearing one has to borrow the world's Sky resource or it would clear to
    // nothing while claiming to draw the sky. -xlinka
    private global::Godot.Environment? BuildCameraEnvironment(Lumora.Core.Assets.RenderCameraParameters p)
    {
        bool wantsSky = p.Clear == Lumora.Core.Components.ClearMode.Skybox;
        if (wantsSky && p.PostProcessing)
        {
            _cameraEnvironment = null;
            return null;
        }

        var env = _cameraEnvironment ??= new global::Godot.Environment();

        if (wantsSky)
        {
            env.BackgroundMode = global::Godot.Environment.BGMode.Sky;
            env.Sky = SkyEnvironment.CurrentSky;
        }
        else if (p.Clear == Lumora.Core.Components.ClearMode.Color)
        {
            env.BackgroundMode = global::Godot.Environment.BGMode.Color;
            env.BackgroundColor = new Color(
                p.BackgroundColor.r, p.BackgroundColor.g, p.BackgroundColor.b, p.BackgroundColor.a);
        }
        else
        {
            // DepthOnly and Nothing both land here: Godot always clears, so the honest version of both
            // is a transparent clear, which is also what makes a camera compositable.
            env.BackgroundMode = global::Godot.Environment.BGMode.Color;
            env.BackgroundColor = new Color(0f, 0f, 0f, 0f);
        }

        env.TonemapMode = p.PostProcessing
            ? global::Godot.Environment.ToneMapper.Agx
            : global::Godot.Environment.ToneMapper.Linear;
        env.GlowEnabled = p.PostProcessing;
        env.SsaoEnabled = false;
        env.SsilEnabled = false;
        env.SsrEnabled = false;

        return env;
    }

    public void SetCameraOverride(Lumora.Core.Assets.RenderCameraParameters? parameters)
    {
        _cameraOverride = parameters;
        Callable.From(ApplyOnMainThread).CallDeferred();
        Callable.From(ApplyRenderEnabled).CallDeferred();
    }

    public void SetRenderEnabled(bool enabled)
    {
        _renderEnabled = enabled;
        Callable.From(ApplyRenderEnabled).CallDeferred();
    }

    private void ApplyRenderEnabled()
    {
        if (_viewport == null || !GodotObject.IsInstanceValid(_viewport)) return;
        // Enabling draws one frame now; subsequent frames come from RequestRender on actual UI changes. -xlinka
        // A camera-driven viewport is filming a live scene and nothing tells us when that scene changed,
        // so it renders every frame. The provider-driven one only ever captures UI that announces its
        // own changes, which is why it stays on render-on-request.
        _viewport.RenderTargetUpdateMode = _renderEnabled
            ? (_cameraOverride.HasValue ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Once)
            : SubViewport.UpdateMode.Disabled;
    }

    public void RequestRender()
    {
        if (!_renderEnabled) return;
        Callable.From(ApplyRequestRender).CallDeferred();
    }

    private void ApplyRequestRender()
    {
        if (!_renderEnabled || _viewport == null || !GodotObject.IsInstanceValid(_viewport)) return;
        // UpdateMode.Once renders a single frame then reverts to no-updates on its own, keeping that frame on
        // the texture until the next request. -xlinka
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    public void UploadData(byte[] pixels, int width, int height, bool hasMipmaps) { }

    // Render textures build their viewport synchronously in Configure; nothing to wait on. -xlinka
    public System.Threading.Tasks.Task WaitForUploadAsync() => System.Threading.Tasks.Task.CompletedTask;

    public void SetWrapMode(TextureWrapMode wrapU, TextureWrapMode wrapV) { }

    public override void Unload()
    {
        if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
        {
            lock (_liveViewportLock)
                _liveViewports.Remove(_viewport);
            _viewport.QueueFree();
        }
        _viewport = null!;
        _camera = null!;
        _cameraEnvironment = null;
    }
}
