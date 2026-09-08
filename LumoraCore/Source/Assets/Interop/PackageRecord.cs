// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Core.Assets.Interop;

// The JSON manifest sitting at the root of a package. Only the fields we actually consume are declared;
// the rest (ownership, tags, timestamps, cloud bookkeeping) is deliberately ignored, since an offline
// conversion has no use for another platform's account model. -xlinka
public sealed class PackageRecord
{
    // Their writer emits camelCase but has shipped PascalCase historically, so match case-insensitively
    // rather than guessing which vintage produced the file in front of us.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string? RecordId { get; set; }
    public string? RecordType { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? AssetUri { get; set; }
    public string? ThumbnailUri { get; set; }
    public List<string>? Tags { get; set; }
    public List<PackageManifestEntry>? AssetManifest { get; set; }

    [JsonIgnore]
    public bool IsObject => string.Equals(RecordType, "object", System.StringComparison.OrdinalIgnoreCase);
}

public sealed class PackageManifestEntry
{
    public string? Hash { get; set; }
    public long Bytes { get; set; }
}
