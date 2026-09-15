
using System;
using Godot;
using Lumora.Core.External.Audio.Godot;
using Lumora.Core.Math;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
namespace Lumora.Source.Godot;

public class AudioCaptureEffectProxy : IAudioCapureEffect
{
    private readonly AudioEffectCapture _target;
    internal AudioEffectCapture target { get => _target; }
    public AudioCaptureEffectProxy(AudioEffectCapture target)
    {
        _target = target;
    }
    public void ClearBuffer() => _target.ClearBuffer();
    public long GetMissedFrames() => _target.GetDiscardedFrames();
    public int GetAvailableFrameCount() => _target.GetFramesAvailable();
    public bool TryGetFrames(int frameCount, [NotNull] out float2[] result)
    {
        int availableCount = _target.GetFramesAvailable();
        if (availableCount >= frameCount)
        {
            var source = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector2, float2>(_target.GetBuffer(frameCount));
            result = new float2[source.Length];
            source.CopyTo(result);
            return true;
        }
        result = Array.Empty<float2>();
        return false;
    }
}
