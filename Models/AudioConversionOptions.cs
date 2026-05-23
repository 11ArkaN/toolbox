namespace Toolbox.Models;

public enum AudioChannelMode
{
    Original,
    Stereo,
    Mono
}

public sealed record AudioConversionOptions(
    string Suffix,
    int BitrateKbps,
    int SampleRate,
    AudioChannelMode ChannelMode);
