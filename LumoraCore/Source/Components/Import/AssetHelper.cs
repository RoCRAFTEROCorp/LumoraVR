// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;

namespace Lumora.Core.Components.Import;

public static class AssetHelper
{
    private static readonly Dictionary<string, AssetClass> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Texture
        { ".png", AssetClass.Texture }, { ".jpg", AssetClass.Texture }, { ".jpeg", AssetClass.Texture },
        { ".webp", AssetClass.Texture }, { ".bmp", AssetClass.Texture }, { ".gif", AssetClass.Texture },
        { ".tga", AssetClass.Texture }, { ".tif", AssetClass.Texture }, { ".tiff", AssetClass.Texture },
        { ".exr", AssetClass.Texture }, { ".hdr", AssetClass.Texture }, { ".psd", AssetClass.Texture },

        // Model
        { ".obj", AssetClass.Model }, { ".fbx", AssetClass.Model }, { ".glb", AssetClass.Model },
        { ".gltf", AssetClass.Model }, { ".dae", AssetClass.Model }, { ".blend", AssetClass.Model },
        { ".stl", AssetClass.Model }, { ".3ds", AssetClass.Model }, { ".ply", AssetClass.Model },
        { ".x3d", AssetClass.Model },

        // PointCloud
        { ".pts", AssetClass.PointCloud }, { ".xyz", AssetClass.PointCloud }, { ".pcd", AssetClass.PointCloud },

        // Video
        { ".mp4", AssetClass.Video }, { ".mov", AssetClass.Video }, { ".avi", AssetClass.Video },
        { ".webm", AssetClass.Video }, { ".mkv", AssetClass.Video }, { ".m4v", AssetClass.Video },
        { ".wmv", AssetClass.Video }, { ".flv", AssetClass.Video }, { ".ts", AssetClass.Video },

        // Audio
        { ".mp3", AssetClass.Audio }, { ".wav", AssetClass.Audio }, { ".ogg", AssetClass.Audio },
        { ".flac", AssetClass.Audio }, { ".m4a", AssetClass.Audio }, { ".aac", AssetClass.Audio },
        { ".opus", AssetClass.Audio }, { ".aiff", AssetClass.Audio },

        // Font
        { ".ttf", AssetClass.Font }, { ".otf", AssetClass.Font },
        { ".woff", AssetClass.Font }, { ".woff2", AssetClass.Font },

        // Subtitle
        { ".srt", AssetClass.Subtitle }, { ".vtt", AssetClass.Subtitle }, { ".ass", AssetClass.Subtitle },
        { ".ssa", AssetClass.Subtitle },

        // Animation
        { ".bvh", AssetClass.Animation },

        // Lumora object / scene
        { ".lumora", AssetClass.Object }, { ".lumoraobj", AssetClass.Object },
        { ".lumoraworld", AssetClass.Object },

        // Text
        { ".txt", AssetClass.Text }, { ".md", AssetClass.Text }, { ".rst", AssetClass.Text },
        { ".log", AssetClass.Text }, { ".csv", AssetClass.Text }, { ".tsv", AssetClass.Text },
        { ".json", AssetClass.Text }, { ".xml", AssetClass.Text }, { ".yaml", AssetClass.Text },
        { ".yml", AssetClass.Text }, { ".toml", AssetClass.Text }, { ".ini", AssetClass.Text },

        // Document
        { ".pdf", AssetClass.Document }, { ".doc", AssetClass.Document }, { ".docx", AssetClass.Document },
        { ".rtf", AssetClass.Document }, { ".odt", AssetClass.Document }, { ".epub", AssetClass.Document },

        // Volume / cubemap
        { ".cube", AssetClass.Volume },

        // Shared object packages from other platforms. Self-contained archives: every mesh, texture
        // and clip the object references travels inside the file, so importing one never needs the
        // network.
        { ".resonitepackage", AssetClass.Package },

        // Shader
        { ".gdshader", AssetClass.Shader }, { ".lumshader", AssetClass.Shader }, { ".glsl", AssetClass.Shader },
        { ".hlsl", AssetClass.Shader }, { ".shader", AssetClass.Shader },
    };

    public static AssetClass IdentifyClass(string path)
    {
        if (string.IsNullOrEmpty(path)) return AssetClass.Unknown;
        try
        {
            if (Directory.Exists(path)) return AssetClass.Folder;
        }
        catch { }
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return AssetClass.Unknown;
        return _byExtension.TryGetValue(ext, out var c) ? c : AssetClass.Unknown;
    }
}
