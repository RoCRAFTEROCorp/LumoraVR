using System;
using System.Diagnostics.CodeAnalysis;
namespace Lumora.Core.External.Audio.Godot;
#nullable enable
public interface IAudioCaputreProvider {
    public bool TryGetCatpureEffect([NotNullWhen(true)] out IAudioCapureEffect? effect);
    public void InitializeInput();
    public int GetSampleRate();
}
