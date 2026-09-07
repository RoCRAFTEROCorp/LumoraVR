// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;

namespace Lumora.Core.Assets;

// This is the order the renderer expects its six layers in and the order every producer writes, so it is
// the one place the convention is stated.
public enum CubemapFace
{
    PositiveX = 0,
    NegativeX = 1,
    PositiveY = 2,
    NegativeY = 3,
    PositiveZ = 4,
    NegativeZ = 5,
}

// A face size of 0 means "pick from the source", which every auto request shares.
public sealed record CubemapVariantDescriptor(
    int FaceSize,
    bool GenerateMipmaps) : IAssetVariantDescriptor
{
    public static readonly CubemapVariantDescriptor Default = new(FaceSize: 0, GenerateMipmaps: true);
}

public sealed class CubemapUploadRequest
{
    // Six RGBA8 faces, each FaceSize squared times four bytes, in CubemapFace order.
    public byte[][] Faces { get; init; } = Array.Empty<byte[]>();

    // Faces are always square and always equal.
    public int FaceSize { get; init; }

    public bool GenerateMipmaps { get; init; }

    public Action<long>? Report { get; init; }
}

public interface ICubemapAssetHook : IAssetHook
{
    bool IsValid { get; }

    void UploadCubemap(CubemapUploadRequest request);

    // Completes once the most recent UploadCubemap has actually built the GPU resource.
    // Uploads only queue a deferred main-thread build, so a URL instance awaits this before reporting
    // itself loaded - otherwise a sky or probe binds a null cubemap in the gap and nothing rebinds it.
    Task WaitForUploadAsync();
}

// Six RGBA8 cube faces. URL instances decode an equirectangular panorama in LoadSelf
// and project it onto the faces; procedural instances (gradient, checker, noise) and the six-image
// assembler push their faces straight in through SetFaces.
//
// Faces are the only form kept. A panorama is an import format, not a runtime one: keeping both
// would double the resident bytes of every skybox, and the cube is what a sky shader, a probe
// fallback and a material all want to sample anyway. -xlinka
public class CubemapAsset : ImplementableAsset<ICubemapAssetHook>
{
    public const int MinFaceSize = 8;

    // Cap on a single face. Six faces at this size is 384 MB of RGBA8 before mips.
    public const int MaxFaceSize = 4096;

    private byte[][] _faces = Array.Empty<byte[]>();
    private int _faceSize;
    private bool _hasMipmaps;

    // 0 when nothing has been uploaded.
    public int FaceSize => _faceSize;

    // CubemapFace order. Empty until data is set.
    public byte[][] Faces => _faces;

    public bool HasMipmaps => _hasMipmaps;

    public long MemorySize
    {
        get
        {
            long total = 0;
            foreach (var face in _faces)
                total += face?.LongLength ?? 0;
            return total;
        }
    }

    // Null until the renderer reports.
    public long? GpuBytes { get; private set; }

    // Null for procedural data.
    public (int Width, int Height)? SourcePanoramaSize { get; private set; }

    // Gather and decode this cubemap from its URL. The source is a single equirectangular image; six separate
    // face images go through the assembler component instead, which reuses the ordinary texture pipeline per
    // face rather than growing a second one here.
    protected override async Task LoadSelf()
    {
        var descriptor = TargetVariant as CubemapVariantDescriptor ?? CubemapVariantDescriptor.Default;

        var bytes = await AssetManager.RequestGather(AssetURL).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
        {
            FailLoad($"No image data gathered for {AssetURL}");
            return;
        }

        byte[]? panorama;
        int width, height;
        try
        {
            // An HDR panorama cannot go through the 8-bit decoder. Radiance would come back tone-mapped
            // and an EXR would fail with a decode error that says nothing useful, and in both cases the
            // sky quietly loses the range it was chosen for. Refuse it with a reason instead: a skybox
            // that does not load is a question, a skybox that loads wrong is a bug hunt. -xlinka
            if (TextureMetadata.DetectHdr(bytes))
            {
                FailLoad($"HDR panoramas are not supported yet (needs a half-float projector): {AssetURL}");
                return;
            }

            panorama = TextureVariantStore.DecodeRgba(bytes, out width, out height);
        }
        catch (Exception ex)
        {
            FailLoad($"Failed to decode panorama {AssetURL}: {ex.Message}");
            return;
        }

        if (panorama == null || width <= 0 || height <= 0)
        {
            FailLoad($"Failed to decode panorama {AssetURL}: no pixels");
            return;
        }

        SourcePanoramaSize = (width, height);
        int faceSize = CubemapProjector.ChooseFaceSize(descriptor.FaceSize, width);
        var faces = CubemapProjector.ProjectPanorama(panorama, width, height, faceSize);

        SetFaces(faces, faceSize, descriptor.GenerateMipmaps);

        if (Hook != null)
            await Hook.WaitForUploadAsync().ConfigureAwait(false);
    }

    // Every face must be square, the same size, and RGBA8.
    public void SetFaces(byte[][] faces, int faceSize, bool mipmaps = true)
    {
        if (faces == null || faces.Length != 6)
            throw new ArgumentException("A cubemap needs exactly six faces", nameof(faces));
        if (faceSize < MinFaceSize)
            throw new ArgumentOutOfRangeException(nameof(faceSize), $"Face size must be at least {MinFaceSize}");

        int expected = faceSize * faceSize * 4;
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i] == null || faces[i].Length < expected)
                throw new ArgumentException(
                    $"Face {(CubemapFace)i} is {faces[i]?.Length ?? 0} bytes, expected {expected}", nameof(faces));
        }

        _faces = faces;
        _faceSize = faceSize;
        _hasMipmaps = mipmaps;
        Version++;

        Hook?.UploadCubemap(new CubemapUploadRequest
        {
            Faces = faces,
            FaceSize = faceSize,
            GenerateMipmaps = mipmaps,
            Report = bytes => GpuBytes = bytes,
        });
    }

    public static CubemapAsset FromFaces(byte[][] faces, int faceSize, bool mipmaps = true)
    {
        var asset = new CubemapAsset();
        asset.InitializeDynamic();
        asset.SetFaces(faces, faceSize, mipmaps);
        return asset;
    }

    public override void Unload()
    {
        _faces = Array.Empty<byte[]>();
        _faceSize = 0;
        _hasMipmaps = false;
        GpuBytes = null;
        SourcePanoramaSize = null;
        base.Unload();
    }
}
