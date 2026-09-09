// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Nexus.Transport;

// Hard upper bounds for any size/count field that crosses the network trust
// boundary. Anything a peer can declare must be checked against one of these
// before allocation. Treat the values as defensive ceilings - the legitimate
// traffic should never come close.
public static class NetworkLimits
{
    public const int MaxControlMessagePayload = 1 * 1024 * 1024; // 1 MB

    public const int MaxStreamMessageData = 256 * 1024; // 256 KB

    public const int MaxStreamEntriesPerMessage = 256;

    public const int MaxStreamEntryData = 64 * 1024; // 64 KB

    public const int MaxAssetTransferTotalBytes = 256 * 1024 * 1024; // 256 MB

    public const int MaxAssetChunkBytes = 256 * 1024; // 256 KB

    public const int MaxLanAnnouncementBytes = 64 * 1024; // 64 KB

    public const int MaxPendingConnections = 64;

    public const int MaxPendingPerIP = 4;

    public const int MaxAssetUriBytes = 4 * 1024; // 4 KB

    // A file extension declared alongside a transferred asset. Real ones are four or five bytes; this
    // only exists so a peer cannot make the reader allocate on a declared length.
    public const int MaxAssetFormatBytes = 64;

    // Sized for codec frames (e.g. Opus 60 ms approx 960 B at 128 kbps); 4 KB leaves generous headroom.
    public const int MaxRawFrameBytes = 4 * 1024;

    // A peer declares the uncompressed length in the compression envelope; this caps the buffer we
    // allocate for it so a crafted frame can't force a huge allocation. Sized generously so a large
    // full-world-state batch in one frame is never falsely rejected.
    public const int MaxDecompressedFrameBytes = 16 * 1024 * 1024; // 16 MB
}
