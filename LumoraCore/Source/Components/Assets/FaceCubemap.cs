// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Components;

namespace Lumora.Core.Assets;

// A cubemap assembled from six ordinary images, one per face.
//
// Each face is a plain TextureAsset behind a normal image provider, so a face gets
// LocalDB storage, resolution variants, peer transfer and sharing without any of that being
// reimplemented here. This component only waits for all six, checks they agree on a square size,
// and copies their pixels into the cube.
//
// Faces must be square and the same size as each other; the renderer has no way to represent a cube
// whose faces disagree, and silently rescaling them would hide a mistake in the source set. -xlinka
[ComponentCategory("Assets/Cubemaps")]
public class FaceCubemap : DynamicAssetProvider<CubemapAsset>, ICustomInspectorUI
{
    public readonly AssetRef<TextureAsset> PositiveX;
    public readonly AssetRef<TextureAsset> NegativeX;
    public readonly AssetRef<TextureAsset> PositiveY;
    public readonly AssetRef<TextureAsset> NegativeY;
    public readonly AssetRef<TextureAsset> PositiveZ;
    public readonly AssetRef<TextureAsset> NegativeZ;

    public readonly Sync<bool> GenerateMipmaps;

    // empty when the last assembly succeeded
    private string _status = "no faces assigned";

    public FaceCubemap()
    {
        PositiveX = new AssetRef<TextureAsset>(this);
        NegativeX = new AssetRef<TextureAsset>(this);
        PositiveY = new AssetRef<TextureAsset>(this);
        NegativeY = new AssetRef<TextureAsset>(this);
        PositiveZ = new AssetRef<TextureAsset>(this);
        NegativeZ = new AssetRef<TextureAsset>(this);
        GenerateMipmaps = new Sync<bool>(this, true);
    }

    // Face textures load asynchronously, so the first assembly attempt after a reference is set
    // usually finds nothing. It does not need a re-trigger of its own: an AssetRef is registered as a
    // reference on its provider, so the provider's load-complete notification lands on it and comes
    // back here as an ordinary change. Assembly is therefore attempted repeatedly and only succeeds
    // once all six have pixels. -xlinka
    private AssetRef<TextureAsset>[] Faces() => new[]
    {
        PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ,
    };

    protected override void OnAssetCreated(CubemapAsset asset) { }

    protected override void UpdateAsset(CubemapAsset asset)
    {
        var refs = Faces();
        var sources = new TextureAsset[6];
        for (int i = 0; i < 6; i++)
        {
            var texture = refs[i].Asset;
            if (texture == null || texture.PixelData == null || texture.Width <= 0)
            {
                _status = $"waiting for {(CubemapFace)i}";
                return;
            }
            if (texture.PixelFormat != TextureFormatKind.RGBA8)
            {
                _status = $"{(CubemapFace)i} is {texture.PixelFormat.Label()}; faces must be 8-bit";
                return;
            }
            sources[i] = texture;
        }

        int size = sources[0].Width;
        for (int i = 0; i < 6; i++)
        {
            if (sources[i].Width != size || sources[i].Height != size)
            {
                _status = $"{(CubemapFace)i} is {sources[i].Width}x{sources[i].Height}, expected {size}x{size}";
                return;
            }
        }

        if (size < CubemapAsset.MinFaceSize || size > CubemapAsset.MaxFaceSize)
        {
            _status = $"face size {size} is outside {CubemapAsset.MinFaceSize}..{CubemapAsset.MaxFaceSize}";
            return;
        }

        // Decoded rows are top-down, which is the order cube faces upload in, so they go in as they are.
        var faces = new byte[6][];
        for (int i = 0; i < 6; i++)
            faces[i] = sources[i].PixelData;

        _status = string.Empty;
        asset.SetFaces(faces, size, GenerateMipmaps.Value);
    }

    protected override void OnAssetCleared() { }

    public void BuildInspectorBody(UIBuilder ui)
    {
        var asset = Asset;
        InspectorStats.AddRow(ui, "Face size", asset is { FaceSize: > 0 } ? $"{asset.FaceSize} px" : "not assembled");
        InspectorStats.AddRow(ui, "Status", string.IsNullOrEmpty(_status) ? "assembled" : _status);
        if (asset != null && asset.FaceSize > 0)
        {
            InspectorStats.AddRow(ui, "Faces", InspectorStats.Bytes(asset.MemorySize));
            InspectorStats.AddRow(ui, "VRAM", asset.GpuBytes is { } vram
                ? InspectorStats.Bytes(vram)
                : "renderer has not reported yet");
        }
    }
}
