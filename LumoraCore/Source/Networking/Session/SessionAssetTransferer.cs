// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumora.Core.Assets;
using Lumora.Core.Networking;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

// Handles peer-to-peer asset transfer over the control message channel.
//
// Protocol:
//   Client -> AssetRequest(uri) -> Host/Owner
//   Host -> AssetTransmissionStart(id, uri, totalBytes, format) -> Client
//   Client -> AssetNextChunkRequest(id) -> Host   (first pull fetches 16 chunks)
//   Host -> AssetChunk(id, offset, data) xN -> Client
//   ...repeat until done...
//   Host -> AssetNotAvailable(uri) -> Client  (if it can't serve the file)
//
// Host relay: a client always asks the host for an asset, but the host might not own it - another
// peer may have imported it (its URI carries that peer's machine id). When the host can't serve a
// local:// asset from its own store, instead of giving up it forwards the request to the owning peer,
// receives the bytes, and streams them on to the original requester. Several requesters waiting on the
// same asset are coalesced onto one fetch. This is what lets "B imports content, C joins" work when
// the host is neither B nor C.
public class SessionAssetTransferer : IDisposable
{

    private readonly struct JobID : IEquatable<JobID>
    {
        public readonly IConnection Connection;
        public readonly int ID;

        public JobID(IConnection connection, int id) { Connection = connection; ID = id; }
        public bool Equals(JobID other) => Connection == other.Connection && ID == other.ID;
        public override bool Equals(object? obj) => obj is JobID j && Equals(j);
        public override int GetHashCode() => HashCode.Combine(Connection, ID);
    }

    // Outbound (we are sending)

    private sealed class FileTransmitJob
    {
        public IConnection Target { get; }
        public int ID { get; }
        public Uri AssetUri { get; }

        private readonly byte[] _data;
        private readonly string _format;
        private int _offset;
        private bool _firstChunk = true;

        public bool IsDone => _offset >= _data.Length;
        public bool FirstChunk => _firstChunk;

        public long TotalBytes => _data.Length;
        public long SentBytes => _offset;

        private const int ChunkSize = 32 * 1024; // 32 KB

        public FileTransmitJob(string filePath, Uri assetUri, IConnection target, int id)
        {
            Target = target;
            ID = id;
            AssetUri = assetUri;
            // A local:// URI is a content hash and carries no format. The only machine that still knows
            // what these bytes are is the one holding the file, and it knows because its cache file kept
            // the extension, so send it. Without it the receiver has a nameless blob and every decoder
            // that dispatches on extension - the mesh decoder above all - has nothing to dispatch on.
            // -xlinka
            _format = Path.GetExtension(filePath) ?? string.Empty;
            _data = File.ReadAllBytes(filePath);
        }

        public ControlMessage Initialize()
        {
            var msg = new ControlMessage(ControlMessage.Message.AssetTransmissionStart);
            msg.Targets.Add(Target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            w.Write(AssetUri.OriginalString);
            w.Write(_data.Length);
            // Appended after the fields a build without it wrote, so its reader stops cleanly at the
            // length and simply gets no format.
            w.Write(_format);
            msg.Payload = ms.ToArray();
            return msg;
        }

        public ControlMessage GetChunk()
        {
            int size = System.Math.Min(ChunkSize, _data.Length - _offset);
            var msg = new ControlMessage(ControlMessage.Message.AssetChunk);
            msg.Targets.Add(Target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            w.Write(_offset);
            w.Write(size);
            w.Write(_data, _offset, size);
            msg.Payload = ms.ToArray();
            _offset += size;
            _firstChunk = false;
            return msg;
        }
    }

    // Inbound (we are receiving)

    private sealed class FileReceiveJob
    {
        public int ID { get; }
        public Uri AssetUri { get; }
        public IConnection Source { get; }

        private readonly int _totalSize;
        private readonly byte[] _buffer;
        private int _received;
        private readonly string _tempPath;

        public bool IsDone => _received >= _totalSize;

        public int TotalBytes => _totalSize;
        public int ReceivedBytes => _received;

        // tempPathFor is handed the sender's declared format so the file we land on disk keeps the
        // extension the asset actually has. The temp name is what the local database then adopts, and
        // the extension on that cache file is the only thing an extension-dispatching decoder has to go
        // on later, so losing it here quietly breaks every load of that asset from then on. -xlinka
        public FileReceiveJob(IConnection source, BinaryReader reader, Func<string, string> tempPathFor)
        {
            Source = source;
            ID = reader.ReadInt32();
            // Bound the URI string before constructing Uri (which itself has work to do).
            AssetUri = new Uri(reader.ReadBoundedString(NetworkLimits.MaxAssetUriBytes));
            _totalSize = reader.ReadInt32();

            // A peer cannot make us pre-allocate a multi-GB buffer just by declaring
            // a fake transfer size. Reject negative or out-of-bounds totals before alloc.
            if (_totalSize < 0 || _totalSize > NetworkLimits.MaxAssetTransferTotalBytes)
                throw new InvalidDataException($"Asset transfer total size {_totalSize} out of bounds (cap {NetworkLimits.MaxAssetTransferTotalBytes}).");

            // Absent on a build that predates the format field, which leaves the stream exactly here.
            string? declaredFormat = null;
            if (HasMore(reader))
                declaredFormat = reader.ReadBoundedString(NetworkLimits.MaxAssetFormatBytes);
            Format = SanitizeFormat(declaredFormat);

            _buffer = new byte[_totalSize];
            _tempPath = tempPathFor(Format);
        }

        public string Format { get; }

        private static bool HasMore(BinaryReader reader)
        {
            var stream = reader.BaseStream;
            return stream.CanSeek && stream.Position < stream.Length;
        }

        public void ApplyChunk(BinaryReader reader)
        {
            int offset = reader.ReadInt32();
            int size = reader.ReadInt32();

            // Bound the chunk size, reject negative offsets, and ensure the chunk lands
            // inside the pre-declared buffer - the latter prevents an OOB write via
            // a peer crafting offset+size > _totalSize after a legitimate Initialize.
            if (size < 0 || size > NetworkLimits.MaxAssetChunkBytes)
                throw new InvalidDataException($"Asset chunk size {size} out of bounds (cap {NetworkLimits.MaxAssetChunkBytes}).");
            if (offset < 0 || offset > _totalSize - size)
                throw new InvalidDataException($"Asset chunk offset {offset} + size {size} exceeds buffer length {_totalSize}.");

            var data = reader.ReadBoundedBytes(size, NetworkLimits.MaxAssetChunkBytes);
            Buffer.BlockCopy(data, 0, _buffer, offset, size);
            _received += size;
        }

        public ControlMessage NextChunkRequest(IConnection target)
        {
            var msg = new ControlMessage(ControlMessage.Message.AssetNextChunkRequest);
            msg.Targets.Add(target);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ID);
            msg.Payload = ms.ToArray();
            return msg;
        }

        public string FinalizeAndGetFile()
        {
            File.WriteAllBytes(_tempPath, _buffer);
            return _tempPath;
        }
    }

    private const int MaxTransmitJobs = 4;

    // What an incoming asset is called when the sender declared no format, or declared one we refuse.
    private const string UnknownFormat = ".asset";

    // The declared format ends up in a filename, so it is a peer-controlled path fragment and gets no
    // benefit of the doubt: a leading dot and a short run of ASCII alphanumerics, nothing else. That
    // rules out separators, "..", drive letters and every unicode lookalike in one pass, and a refusal
    // costs only the neutral extension plus the decoder's content sniff. -xlinka
    private static string SanitizeFormat(string? format)
    {
        if (string.IsNullOrEmpty(format) || format![0] != '.' || format.Length < 2 || format.Length > 12)
            return UnknownFormat;

        for (int i = 1; i < format.Length; i++)
        {
            char c = format[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
            if (!ok)
                return UnknownFormat;
        }

        return format;
    }

    // How many distinct unknown assets the host will chase down from owning peers at once. Caps the
    // damage a peer could do by requesting a flood of bogus URIs (each would open a receive job). -xlinka
    private const int MaxConcurrentRelays = 8;

    // Holds the completion callback plus an optional per-chunk progress callback for an in-flight fetch. -xlinka
    // Uri.ToString() LOWERCASES the authority, and our machine ids are case-sensitive, so keying a
    // request on it makes a client-imported asset unfindable while a host-imported one works. That is
    // the same class of bug that once stopped peer meshes transferring at all; OriginalString is the
    // spelling the sender actually replicated. -xlinka
    private static string UriKey(Uri uri) => uri.OriginalString;

    private readonly struct GatherCallbacks
    {
        public readonly Action<Uri, string> OnGathered;
        public readonly Action<Uri, long, long>? OnProgress; // (uri, totalBytes, receivedBytes)
        public GatherCallbacks(Action<Uri, string> onGathered, Action<Uri, long, long>? onProgress)
        {
            OnGathered = onGathered;
            OnProgress = onProgress;
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<JobID, FileTransmitJob> _transmitJobs = new();
    private readonly Dictionary<JobID, FileReceiveJob> _receiveJobs = new();
    // A LIST per URI, not one entry. Nine meshes of one avatar routinely resolve to the same asset, and
    // dropping the second requester's callback left its gather task awaiting a completion that could
    // never arrive - a permanent hang, not a slow load. Every waiter is answered from the one transfer.
    private readonly Dictionary<string, List<GatherCallbacks>> _assetRequests = new();

    // Pending outbound transfers awaiting an initialize slot. Not a FIFO: RefreshJobs picks the job whose
    // target connection was served least recently, so one peer flooding requests can't starve another. -xlinka
    private readonly List<FileTransmitJob> _transmitJobsToInitialize = new();
    // Last-served ordinal per connection; lower = waited longer (absent = never served = -1). -xlinka
    private readonly Dictionary<IConnection, int> _connectionIndexes = new();
    private int _globalConnectionIndex;

    // uriStr -> the connections waiting for the host to relay that asset to them. The host fetches the
    // bytes from the owning peer once and fans them out to everyone in the list when they arrive. -xlinka
    private readonly Dictionary<string, List<IConnection>> _pendingRelays = new();

    private int _outboundIdPool;

    public Session Session { get; }

    // Live transfer counts for diagnostics (the Network debug tab), guarded by the jobs' own lock.
    public int UploadJobCount { get { lock (_lock) { return _transmitJobs.Count + _transmitJobsToInitialize.Count; } } }
    public int DownloadJobCount { get { lock (_lock) { return _receiveJobs.Count; } } }
    public int PendingAssetRequestCount { get { lock (_lock) { return _assetRequests.Count; } } }
    public int PendingRelayCount { get { lock (_lock) { return _pendingRelays.Count; } } }

    public readonly struct AssetTransfer
    {
        public readonly Uri Uri;
        public readonly bool IsUpload;
        public readonly long Transferred;
        public readonly long Total;

        public AssetTransfer(Uri uri, bool isUpload, long transferred, long total)
        {
            Uri = uri;
            IsUpload = isUpload;
            Transferred = transferred;
            Total = total;
        }

        public float Fraction => Total > 0 ? (float)Transferred / Total : 0f;
    }

    // Safe to call from the render/UI thread.
    public List<AssetTransfer> GetActiveTransfers()
    {
        var list = new List<AssetTransfer>();
        lock (_lock)
        {
            foreach (var job in _transmitJobs.Values)
                list.Add(new AssetTransfer(job.AssetUri, true, job.SentBytes, job.TotalBytes));
            foreach (var job in _receiveJobs.Values)
                list.Add(new AssetTransfer(job.AssetUri, false, job.ReceivedBytes, job.TotalBytes));
        }
        return list;
    }

    public SessionAssetTransferer(Session session)
    {
        Session = session;
    }

    // Request an asset by URI. Sends AssetRequest to the host (clients) or to the asset owner's connection (if
    // we are the authority).
    public void RequestAsset(Uri assetUri, Action<Uri, string> onGathered, Action<Uri, long, long>? onProgress = null)
    {
        lock (_lock)
        {
            var uriStr = UriKey(assetUri);
            if (_assetRequests.TryGetValue(uriStr, out var waiting))
            {
                // Already in flight: join the queue rather than being forgotten.
                waiting.Add(new GatherCallbacks(onGathered, onProgress));
                return;
            }

            IConnection target;
            if (Session.World.IsAuthority)
            {
                // Authority -> request from the machine that owns this local:// asset
                var machineId = ExtractMachineId(assetUri);
                var owner = Session.World.GetAllUsers()
                    ?.FirstOrDefault(u => u.MachineID?.Value == machineId);
                if (owner == null || !Session.Connections.TryGetConnection(owner, out target))
                {
                    LumoraLogger.Warn($"AssetTransferer: no peer for machine '{machineId}', cannot fetch {uriStr}");
                    onGathered(assetUri, null!);
                    return;
                }
            }
            else
            {
                target = Session.Connections.HostConnection;
            }

            if (target == null)
            {
                onGathered(assetUri, null!);
                return;
            }

            _assetRequests[uriStr] = new List<GatherCallbacks> { new GatherCallbacks(onGathered, onProgress) };
            SendAssetRequest(assetUri, target);
            LumoraLogger.Log($"AssetTransferer: requested {uriStr}");
        }
    }

    // Send a bare AssetRequest control message to target. Does NOT register a
    // completion callback - callers that want one register in _assetRequests themselves
    // (a client fetch) or track their own pending list (the host relay). -xlinka
    private void SendAssetRequest(Uri assetUri, IConnection target)
    {
        var msg = new ControlMessage(ControlMessage.Message.AssetRequest);
        msg.Targets.Add(target);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(UriKey(assetUri));
        msg.Payload = ms.ToArray();
        Session.Sync.EnqueueForTransmission(msg);
    }

    private void SendNotAvailable(string uriStr, IConnection target)
    {
        if (target == null)
            return;

        var msg = new ControlMessage(ControlMessage.Message.AssetNotAvailable);
        msg.Targets.Add(target);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(uriStr);
        msg.Payload = ms.ToArray();
        Session.Sync.EnqueueForTransmission(msg);
    }

    public void ProcessMessage(ControlMessage message)
    {
        lock (_lock)
        {
            // Per-message try/catch so a malformed payload from one peer doesn't take
            // down the asset transfer system for everyone. Bounds-check failures from
            // the decoders surface here as InvalidDataException.
            try
            {
                switch (message.ControlMessageType)
                {
                    case ControlMessage.Message.AssetRequest:       HandleAssetRequest(message);       break;
                    case ControlMessage.Message.AssetTransmissionStart: HandleTransmissionStart(message); break;
                    case ControlMessage.Message.AssetChunk:         HandleChunk(message);              break;
                    case ControlMessage.Message.AssetNextChunkRequest: HandleNextChunkRequest(message); break;
                    case ControlMessage.Message.AssetNotAvailable:  HandleNotAvailable(message);       break;
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"AssetTransferer: dropped malformed {message.ControlMessageType} from {message.Sender?.Identifier}: {ex.Message}");
            }
            RefreshJobs();
        }
    }

    public void ConnectionClosed(IConnection connection)
    {
        lock (_lock)
        {
            var dead = _transmitJobs.Keys.Where(k => k.Connection == connection).ToList();
            foreach (var key in dead) _transmitJobs.Remove(key);

            dead = _receiveJobs.Keys.Where(k => k.Connection == connection).ToList();
            foreach (var key in dead)
            {
                var job = _receiveJobs[key];
                _receiveJobs.Remove(key);
                var uriStr = UriKey(job.AssetUri);
                if (_assetRequests.TryGetValue(uriStr, out var dropped))
                {
                    _assetRequests.Remove(uriStr);
                    foreach (var cb in dropped)
                        cb.OnGathered(job.AssetUri, null!);
                }
                // We were relaying this asset FROM the peer that just dropped - fail everyone waiting. -xlinka
                FailPendingRelays(uriStr);
            }

            // A downstream requester dropped - stop tracking it so we don't relay into a dead connection.
            // If that leaves a relay with nobody waiting, drop the whole entry. -xlinka
            var emptiedRelays = new List<string>();
            foreach (var kvp in _pendingRelays)
            {
                kvp.Value.RemoveAll(c => c == connection);
                if (kvp.Value.Count == 0)
                    emptiedRelays.Add(kvp.Key);
            }
            foreach (var uriStr in emptiedRelays)
                _pendingRelays.Remove(uriStr);

            // Drop any pending outbound transfers aimed at the dead peer + forget its served-index. -xlinka
            _transmitJobsToInitialize.RemoveAll(j => j.Target == connection);
            _connectionIndexes.Remove(connection);

            RefreshJobs();
        }
    }

    private void HandleAssetRequest(ControlMessage message)
    {
        string uriStr;
        using (var ms = new MemoryStream(message.Payload))
        using (var r = new BinaryReader(ms))
            uriStr = r.ReadBoundedString(NetworkLimits.MaxAssetUriBytes);

        var assetUri = new Uri(uriStr);

        var localDB = Engine.Current?.LocalDB;
        string localPath = null!;
        if (assetUri.Scheme == "local" && localDB != null)
            localPath = localDB.GetFilePath(uriStr);

        if (localPath != null && File.Exists(localPath))
        {
            var id = _outboundIdPool++;
            _transmitJobsToInitialize.Add(new FileTransmitJob(localPath, assetUri, message.Sender, id));
            LumoraLogger.Log($"AssetTransferer: queued outbound transfer for {uriStr} (job {id})");
            return;
        }

        // We don't have it ourselves. If we're the authority and this is a peer-owned local:// asset,
        // we can still get it: forward the request to the owning peer and relay the bytes back. That's
        // how an asset one user imported reaches another user who joined later, even when neither of
        // them is us. -xlinka
        var ourMachineId = localDB?.MachineId;
        var assetMachineId = ExtractMachineId(assetUri);
        bool canRelay = Session.World.IsAuthority
            && assetUri.Scheme == "local"
            && !string.IsNullOrEmpty(assetMachineId)
            && assetMachineId != ourMachineId;

        if (canRelay)
        {
            RelayFromOwner(assetUri, message.Sender);
            return;
        }

        LumoraLogger.Warn($"AssetTransferer: cannot serve {uriStr} - not found locally");
        SendNotAvailable(uriStr, message.Sender);
    }

    // Host-as-relay: fetch a peer-owned asset from its owner and forward it to requester.
    // Coalesces multiple requesters of the same asset onto a single fetch; the bytes are handed to all of
    // them when they arrive (see ServePendingRelays). -xlinka
    private void RelayFromOwner(Uri assetUri, IConnection requester)
    {
        var uriStr = UriKey(assetUri);

        // Already gathering this exact asset - either another requester's relay or our own in-flight
        // client fetch. Pile this requester on and let the one completion serve everyone. This is also
        // what stops a second request from opening a duplicate receive job. -xlinka
        bool alreadyInFlight = _pendingRelays.ContainsKey(uriStr) || _assetRequests.ContainsKey(uriStr);
        if (alreadyInFlight)
        {
            AddPendingRelay(uriStr, requester);
            return;
        }

        // Resolve the peer that owns this local:// asset (its machine id is in the URI).
        var owner = Session.World.GetAllUsers()
            ?.FirstOrDefault(u => u.MachineID?.Value == ExtractMachineId(assetUri));
        if (owner == null || !Session.Connections.TryGetConnection(owner, out var ownerConn) || ownerConn == null)
        {
            LumoraLogger.Warn($"AssetTransferer: no connected peer owns {uriStr}, cannot relay");
            SendNotAvailable(uriStr, requester);
            return;
        }

        if (_pendingRelays.Count >= MaxConcurrentRelays)
        {
            LumoraLogger.Warn($"AssetTransferer: relay cap ({MaxConcurrentRelays}) reached, refusing {uriStr}");
            SendNotAvailable(uriStr, requester);
            return;
        }

        AddPendingRelay(uriStr, requester);
        SendAssetRequest(assetUri, ownerConn);
        LumoraLogger.Log($"AssetTransferer: relaying {uriStr} from owner '{owner.UserName.Value}'");
    }

    private void AddPendingRelay(string uriStr, IConnection requester)
    {
        if (requester == null)
            return;

        if (_pendingRelays.TryGetValue(uriStr, out var list))
        {
            if (!list.Contains(requester))
                list.Add(requester);
        }
        else
        {
            _pendingRelays[uriStr] = new List<IConnection> { requester };
        }
    }

    private void ServePendingRelays(Uri assetUri, string localPath)
    {
        var uriStr = UriKey(assetUri);
        if (!_pendingRelays.TryGetValue(uriStr, out var requesters))
            return;

        _pendingRelays.Remove(uriStr);

        // FileTransmitJob reads the file into memory in its constructor, so the temp file is safe to
        // reuse/clean up after this. Each requester gets its own job (its own send cursor). -xlinka
        foreach (var requester in requesters)
        {
            if (requester == null)
                continue;
            var id = _outboundIdPool++;
            _transmitJobsToInitialize.Add(new FileTransmitJob(localPath, assetUri, requester, id));
        }
        LumoraLogger.Log($"AssetTransferer: relayed {uriStr} to {requesters.Count} requester(s)");
    }

    private void FailPendingRelays(string uriStr)
    {
        if (!_pendingRelays.TryGetValue(uriStr, out var requesters))
            return;

        _pendingRelays.Remove(uriStr);
        foreach (var requester in requesters)
            SendNotAvailable(uriStr, requester);
    }

    private void HandleTransmissionStart(ControlMessage message)
    {
        // The temp name is chosen from the format the sender declares, which is only known once the
        // header is read, so the path is produced by callback rather than up front.
        var localDB = Engine.Current?.LocalDB;

        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);
        var job = new FileReceiveJob(message.Sender, r, format =>
            localDB?.GetTempFilePath(format) ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + format));
        _receiveJobs[new JobID(message.Sender, job.ID)] = job;

        Session.Sync.EnqueueForTransmission(job.NextChunkRequest(message.Sender));
        LumoraLogger.Log($"AssetTransferer: receiving {job.AssetUri} as '{job.Format}' ({job.ID})");
    }

    private void HandleChunk(ControlMessage message)
    {
        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);

        int id = r.ReadInt32();
        var key = new JobID(message.Sender, id);
        if (!_receiveJobs.TryGetValue(key, out var job))
            return;

        job.ApplyChunk(r);

        if (job.IsDone)
        {
            _receiveJobs.Remove(key);
            string path = job.FinalizeAndGetFile();

            // Adopt before the path goes anywhere. The file is already addressed - the hash is in the URI
            // we asked for - so taking that address costs nothing, where re-deriving it means a second
            // full read of a file we are about to decode anyway.
            //
            // It also has to happen here rather than in each consumer. Both of the two below can be live
            // at once (the host wanted this asset AND is relaying it), and whichever adopted first would
            // move the temp file out from under the other, which then reads a path that no longer exists.
            //
            // The side effect is deliberate: a host that only relayed this now holds it, so the next
            // joiner is served from the cache instead of the owner being asked all over again. -xlinka
            var localDB = Engine.Current?.LocalDB;
            string adopted = localDB?.AdoptGatheredAsset(job.AssetUri.OriginalString, path, job.Format) ?? path;
            LumoraLogger.Log($"AssetTransferer: finished receiving {job.AssetUri} -> {adopted}");

            var uriStr = UriKey(job.AssetUri);
            if (_assetRequests.TryGetValue(uriStr, out var waiters))
            {
                _assetRequests.Remove(uriStr);
                foreach (var cb in waiters)
                    cb.OnGathered(job.AssetUri, adopted);
            }
            // Hand the freshly gathered bytes to anyone the host was relaying this asset to. -xlinka
            ServePendingRelays(job.AssetUri, adopted);
        }
        else
        {
            // Report progress per chunk so a requester can drive a download bar / detect a stall. -xlinka
            var progressUri = UriKey(job.AssetUri);
            if (_assetRequests.TryGetValue(progressUri, out var progressWaiters))
            {
                foreach (var pcb in progressWaiters)
                    pcb.OnProgress?.Invoke(job.AssetUri, job.TotalBytes, job.ReceivedBytes);
            }

            Session.Sync.EnqueueForTransmission(job.NextChunkRequest(message.Sender));
        }
    }

    private void HandleNextChunkRequest(ControlMessage message)
    {
        using var ms = new MemoryStream(message.Payload);
        using var r = new BinaryReader(ms);
        int id = r.ReadInt32();

        var key = new JobID(message.Sender, id);
        if (!_transmitJobs.TryGetValue(key, out var job))
            return;

        // First pull: send 16 chunks; subsequent pulls: send 1
        int count = job.FirstChunk ? 16 : 1;
        for (int i = 0; i < count; i++)
        {
            Session.Sync.EnqueueForTransmission(job.GetChunk());
            if (job.IsDone)
            {
                _transmitJobs.Remove(key);
                LumoraLogger.Log($"AssetTransferer: finished sending job {id}");
                break;
            }
        }
    }

    private void HandleNotAvailable(ControlMessage message)
    {
        string uriStr;
        using (var ms = new MemoryStream(message.Payload))
        using (var r = new BinaryReader(ms))
            uriStr = r.ReadBoundedString(NetworkLimits.MaxAssetUriBytes);

        LumoraLogger.Warn($"AssetTransferer: peer reported AssetNotAvailable for {uriStr}");
        if (_assetRequests.TryGetValue(uriStr, out var failed))
        {
            _assetRequests.Remove(uriStr);
            foreach (var cb in failed)
                cb.OnGathered(new Uri(uriStr), null!);
        }
        // The owner we were relaying from doesn't have it either - pass the bad news on to the requesters. -xlinka
        FailPendingRelays(uriStr);
    }

    // Pull the owner machine id out of a local://{machineId}/{hash} URI. Read from the ORIGINAL string,
    // not Uri.Host - Uri.Host lowercases the authority, which would mangle the case-sensitive id stamped on
    // User.MachineID and break owner resolution. -xlinka
    private static string ExtractMachineId(Uri assetUri)
    {
        var s = assetUri.OriginalString;
        const string prefix = "local://";
        if (!s.StartsWith(prefix, StringComparison.Ordinal))
            return assetUri.Host;

        int start = prefix.Length;
        int slash = s.IndexOf('/', start);
        return slash > start ? s.Substring(start, slash - start) : s.Substring(start);
    }

    private void RefreshJobs()
    {
        // Least-recently-served selection: of all pending transfers, start the one whose target
        // connection has waited longest (a never-served connection sorts first). This stops a single
        // peer that floods requests from monopolizing all the outbound slots and starving everyone else. -xlinka
        while (_transmitJobs.Count < MaxTransmitJobs && _transmitJobsToInitialize.Count > 0)
        {
            int index = -1;
            int lowest = int.MaxValue;
            for (int i = 0; i < _transmitJobsToInitialize.Count; i++)
            {
                var candidate = _transmitJobsToInitialize[i];
                if (!_connectionIndexes.TryGetValue(candidate.Target, out var served))
                    served = -1;
                if (served < lowest)
                {
                    index = i;
                    lowest = served;
                }
            }

            var job = _transmitJobsToInitialize[index];
            _transmitJobsToInitialize.RemoveAt(index);

            _connectionIndexes.Remove(job.Target);
            _connectionIndexes[job.Target] = _globalConnectionIndex++;
            if (_globalConnectionIndex == int.MaxValue)
                _globalConnectionIndex = 0;

            var key = new JobID(job.Target, job.ID);
            _transmitJobs[key] = job;
            Session.Sync.EnqueueForTransmission(job.Initialize());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _transmitJobs.Clear();
            _receiveJobs.Clear();
            _assetRequests.Clear();
            _pendingRelays.Clear();
            _transmitJobsToInitialize.Clear();
            _connectionIndexes.Clear();
        }
    }
}
