using System;
using Lumora.Core.Math;
namespace Lumora.Core.External.Audio.Godot;

public partial interface IAudioCapureEffect
{
    public void ClearBuffer();
    public bool TryGetFrames(int frameCount, out float2[] result);
    public long GetMissedFrames();
    public int GetAvailableFrameCount();
};
