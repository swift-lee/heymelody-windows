namespace HeyMelodyWindows.Protocol;

public static class FindHeadphonesToneCodec
{
    public const ushort Command = 0x0400;
    public static byte[] BuildPayload(bool play) => [play ? (byte)1 : (byte)0];
}
