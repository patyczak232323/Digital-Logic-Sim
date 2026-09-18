using System;

namespace UnityEngine
{
    public enum KeyCode
    {
        A='A', B='B', C='C', D='D', E='E', F='F', G='G', H='H', I='I', J='J', K='K', L='L', M='M', N='N',
        O='O', P='P', Q='Q', R='R', S='S', T='T', U='U', V='V', W='W', X='X', Y='Y', Z='Z',
        Alpha0='0', Alpha1='1', Alpha2='2', Alpha3='3', Alpha4='4', Alpha5='5', Alpha6='6', Alpha7='7', Alpha8='8', Alpha9='9'
    }

    public static class Application
    {
        public static string persistentDataPath => ".";
    }
}

namespace Seb.Helpers
{
    public static class Maths
    {
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float EaseQuadInOut(float t) => t;
    }

    public static class InputHelper
    {
        public static bool AnyKeyOrMouseHeldThisFrame => false;
        public static bool CtrlIsHeld => false;
        public static bool ShiftIsHeld => false;
        public static bool AltIsHeld => false;
        public static bool IsKeyHeld(UnityEngine.KeyCode key) => false;
    }
}

namespace DLS.Description
{
    public enum ChipType
    {
        Custom, Nand, TriStateBuffer, Clock, Pulse, dev_Ram_8Bit, Rom_256x16,
        SevenSegmentDisplay, DisplayRGB, DisplayDot, DisplayLED,
        Merge_1To4Bit, Merge_1To8Bit, Merge_4To8Bit, Split_4To1Bit, Split_8To4Bit, Split_8To1Bit,
        In_1Bit, In_4Bit, In_8Bit, Out_1Bit, Out_4Bit, Out_8Bit, Key,
        Bus_1Bit, BusTerminus_1Bit, Bus_4Bit, BusTerminus_4Bit, Bus_8Bit, BusTerminus_8Bit, Buzzer
    }

    public enum ChipCacheMode
    {
        Auto,
        Normal,
        Cached
    }

    public static class ChipTypeHelper
    {
        public static bool IsBusOriginType(ChipType type) => type is ChipType.Bus_1Bit or ChipType.Bus_4Bit or ChipType.Bus_8Bit;
        public static bool IsBusTerminusType(ChipType type) => type is ChipType.BusTerminus_1Bit or ChipType.BusTerminus_4Bit or ChipType.BusTerminus_8Bit;
    }

    public struct PinAddress
    {
        public int PinID;
        public int PinOwnerID;
        public PinAddress(int pinOwnerID, int pinID) { PinOwnerID = pinOwnerID; PinID = pinID; }
    }

    public struct PinDescription { public int ID; }
    public struct SubChipDescription { public string Name; public int ID; public uint[] InternalData; }
    public struct WireDescription { public PinAddress SourcePinAddress; public PinAddress TargetPinAddress; }
    public struct DisplayDescription { }

    public class ChipDescription
    {
        public const StringComparison NameComparison = StringComparison.OrdinalIgnoreCase;
        public static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
        public string Name;
        public ChipType ChipType;
        public ChipCacheMode CacheMode;
        public PinDescription[] InputPins = Array.Empty<PinDescription>();
        public PinDescription[] OutputPins = Array.Empty<PinDescription>();
        public SubChipDescription[] SubChips = Array.Empty<SubChipDescription>();
        public WireDescription[] Wires = Array.Empty<WireDescription>();
        public DisplayDescription[] Displays = Array.Empty<DisplayDescription>();
    }
}

namespace DLS.Game
{
    using DLS.Description;

    public class PinInstance
    {
        public PinAddress Address;
        public uint PlayerInputState;
        public uint State;
    }

    public class DevPinInstance
    {
        public PinInstance Pin = new PinInstance();
    }

    public class ChipLibrary
    {
        public ChipDescription GetChipDescription(string name) => new ChipDescription { Name = name };
    }
}
